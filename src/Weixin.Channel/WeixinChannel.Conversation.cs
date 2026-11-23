using System.Net;
using System.Text.Json.Nodes;

namespace Weixin.Channel;

public sealed partial class WeixinChannel
{
    private static readonly HttpClient ConversationCdn = new(new HttpClientHandler { AllowAutoRedirect = false })
    {
        Timeout = Timeout.InfiniteTimeSpan
    };

    private string ConversationStagingDirectory => Path.Combine(_options.StateDirectory, "media", "conversation-staging");

    private string ConversationAccountDirectory(string accountId) =>
        Path.Combine(_options.StateDirectory, "media", "conversation", QuoteStore.AccountDirectory(accountId));

    private void DeleteConversationDirectory(string directory)
    {
        try
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
        catch (IOException) { Log("会话媒体清理失败。"); }
        catch (UnauthorizedAccessException) { Log("会话媒体目录无删除权限。"); }
    }

    public Task<WeixinAttachment> GetOwnerAttachmentAsync(string accountId, string direction, string messageId, string attachmentId,
        CancellationToken cancellationToken = default) =>
        TrackOperationAsync(token => GetOwnerAttachmentCoreAsync(accountId, direction, messageId, attachmentId, token), cancellationToken);

    private async Task<WeixinAttachment> GetOwnerAttachmentCoreAsync(string accountId, string direction, string messageId, string attachmentId,
        CancellationToken ct)
    {
        var source = _store.GetOwnerAttachment(accountId, direction, messageId, attachmentId);
        var packet = source.Item.ToJsonString();
        var binding = source.Account;
        var key = QuoteStore.AccountDirectory(string.Join('\0', direction, messageId, attachmentId, packet));
        var directory = Path.Combine(ConversationAccountDirectory(binding.AccountId), QuoteStore.AccountDirectory(binding.OwnerId), key);
        var gate = AccountGate(binding.AccountId);
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            VerifyBinding();
            if (ReadCache() is { } existing) return existing;
        }
        finally { gate.Release(); }

        var staging = Path.Combine(ConversationStagingDirectory, Guid.NewGuid().ToString("N"));
        try
        {
            var item = source.Item;
            var media = Media.Reference(item)?["media"] as JsonObject ?? throw new KeyNotFoundException("附件不存在。");
            if (Media.Text(media["full_url"]) is { Length: > 0 } url && !IsConversationCdnUrl(url))
                media.Remove("full_url");
            var downloaded = await Media.DownloadAsync(_options.HttpClient ?? ConversationCdn, _options.CdnBaseUrl, staging,
                item, attachmentId == "quote", Log, ct, _options.MediaTimeout, _options.TimeProvider, conversationPreview: true).ConfigureAwait(false)
                ?? throw new KeyNotFoundException("附件不存在。");
            await gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                VerifyBinding();
                if (ReadCache() is { } existing) return existing;
                Directory.CreateDirectory(directory);
                var path = Path.Combine(directory, "content" + Path.GetExtension(downloaded.Path));
                File.Move(downloaded.Path, path);
                return CreateResult(path);
            }
            finally { gate.Release(); }
        }
        catch (HttpRequestException error) when (error.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Unauthorized or
            HttpStatusCode.Forbidden or HttpStatusCode.NotFound or HttpStatusCode.Gone)
        {
            throw new KeyNotFoundException("附件已不可用。", error);
        }
        finally { DeleteConversationDirectory(staging); }

        void VerifyBinding()
        {
            var current = _store.GetOwnerAttachment(accountId, direction, messageId, attachmentId);
            if (current.Account.OwnerId != binding.OwnerId || current.Item.ToJsonString() != packet)
                throw new KeyNotFoundException("附件不存在。");
        }

        WeixinAttachment? ReadCache()
        {
            var path = Directory.Exists(directory) ? Directory.EnumerateFiles(directory, "content.*").FirstOrDefault() : null;
            return path is null ? null : CreateResult(path);
        }

        WeixinAttachment CreateResult(string path) =>
            new(source.Attachment.Kind, path, Media.Mime(path), source.Attachment.Name, attachmentId == "quote");
    }

    private bool IsConversationCdnUrl(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.UserInfo.Length == 0 &&
        ((uri.Scheme == _options.CdnBaseUrl.Scheme && uri.Host == _options.CdnBaseUrl.Host && uri.Port == _options.CdnBaseUrl.Port) ||
         (uri.Scheme == Uri.UriSchemeHttps && uri.IsDefaultPort && uri.Host.EndsWith(".cdn.weixin.qq.com", StringComparison.OrdinalIgnoreCase)));
}
