using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Weixin.Channel.Tests;

public sealed class ConversationMediaTests : IDisposable
{
    private const string Bot = "bot";
    private const string Owner = "owner";
    private static readonly Uri Cdn = new("https://cdn.test/");
    private static readonly byte[] Key = Enumerable.Range(0, 16).Select(value => (byte)value).ToArray();
    private readonly DirectoryInfo _directory = Directory.CreateTempSubdirectory("weixin-web-media-");

    [Fact]
    public async Task Mixed_history_keeps_wire_order_and_resolves_only_quotes_in_the_same_conversation()
    {
        const string originalId = "18446744073709551615";
        using (var store = Store())
        {
            Bind(store);
            store.SaveAccount("other", "other-owner", "token", "https://unit.test/");
            store.RecordSend("sent", Bot, Owner, null,
                new JsonObject { ["msg"] = new JsonObject { ["item_list"] = new JsonArray(Text("开始 选中 结束"), Item(2, "picture")) } }.ToJsonString(),
                "success", originalId, null);
            store.CommitBatch("other", [Message("foreign-id", "other-owner", Text("其他账号的正文"))], null);
            var quoted = Text("带引用的正文");
            quoted["ref_msg"] = new JsonObject
            {
                ["svr_id"] = originalId, ["partial_text"] = new JsonObject
                {
                    ["start"] = "选中", ["end"] = "选中", ["startindex"] = 0, ["endindex"] = 0,
                    ["quotemd5"] = Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes("选中")))
                }
            };
            var missing = Text("引用其他账号");
            missing["ref_msg"] = new JsonObject { ["svr_id"] = "foreign-id" };
            var inline = Text("收到内联引用");
            inline["ref_msg"] = new JsonObject { ["message_item"] = Text("内联正文") };
            store.CommitBatch(Bot, [Message("mixed", Owner, quoted, Item(2, "picture"), Voice("speech", "语音转写"),
                Text("末尾文字"), Item(5, "clip"), Item(4, "document")), Message("missing", Owner, missing), Message("inline", Owner, inline)], null);
        }

        var requests = 0;
        byte[] png = [137, 80, 78, 71];
        using var http = new HttpClient(new Handler((request, _) =>
        {
            requests++;
            Assert.Equal("/picture", request.RequestUri!.AbsolutePath);
            return Task.FromResult(Response(png));
        }));
        await using var channel = Open(http);
        var inboxBefore = channel.GetInbox(includeCompleted: true).ToArray();
        var history = channel.GetOwnerConversation(Bot);
        Assert.Equal(0, requests);
        var mixed = Assert.Single(history.Items, row => row.Text.Contains("带引用的正文", StringComparison.Ordinal));
        Assert.Equal(new[] { "带引用的正文", "", "语音转写", "末尾文字", "", "" }, mixed.Parts.Select(part => part.Text));
        Assert.Equal(new[] { "image", "voice", "video", "file" }, mixed.Attachments.Select(part => part.Kind));
        Assert.Equal(new[] { "1", "2", "4", "5" }, mixed.Attachments.Select(part => part.Id));
        Assert.True(mixed.CanReply);
        Assert.Equal("选中", mixed.Quote?.Text);
        Assert.Equal("quote", mixed.Quote?.Attachment?.Id);
        Assert.Equal("内联正文", Assert.Single(history.Items, row => row.Text == "收到内联引用").Quote?.Text);
        Assert.True(Assert.Single(history.Items, row => row.Text == "引用其他账号").Quote?.Missing);

        await Assert.ThrowsAsync<KeyNotFoundException>(() => channel.GetOwnerAttachmentAsync("other", "inbound", mixed.Id, "quote"));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => channel.GetOwnerAttachmentAsync(Bot, "inbound", mixed.Id, "01"));
        var attachment = await channel.GetOwnerAttachmentAsync(Bot, "inbound", mixed.Id, "quote");
        Assert.Equal(png, await File.ReadAllBytesAsync(attachment.Path));
        Assert.Equal("image/png", attachment.MimeType);
        Assert.Equal(1, requests);
        Assert.Equal(inboxBefore, channel.GetInbox(includeCompleted: true).ToArray());
    }

    [Fact]
    public void Malformed_attachments_and_inline_quotes_leave_other_history_renderable()
    {
        using var store = Store();
        Bind(store);
        var invalidImage = Item(2, "picture");
        invalidImage["image_item"]!["media"] = true;
        var invalidVideo = Item(5, "clip");
        invalidVideo["video_item"]!["media"]!["full_url"] = new JsonArray("bad");
        var invalidQuote = Text("引用损坏语音");
        invalidQuote["ref_msg"] = new JsonObject
        {
            ["message_item"] = new JsonObject { ["type"] = 3, ["voice_item"] = true }
        };
        var recoverableQuote = Text("恢复原文");
        recoverableQuote["ref_msg"] = new JsonObject
        {
            ["svr_id"] = "original",
            ["message_item"] = invalidImage.DeepClone()
        };
        store.CommitBatch(Bot,
            [
                Message("original", Owner, Text("原文")),
                Message("mixed", Owner, Text("正常正文"), invalidImage, invalidVideo, Item(2, "valid")),
                Message("bad-quote", Owner, invalidQuote),
                Message("fallback-quote", Owner, recoverableQuote)
            ], null);

        var history = store.ReadOwnerConversation(Bot, null);
        Assert.Equal(4, history.Items.Count);
        var mixed = Assert.Single(history.Items, row => row.Text == "正常正文");
        Assert.Collection(mixed.Attachments,
            attachment => { Assert.Equal("image", attachment.Kind); Assert.Null(attachment.Id); },
            attachment => { Assert.Equal("video", attachment.Kind); Assert.Null(attachment.Id); },
            attachment => { Assert.Equal("image", attachment.Kind); Assert.Equal("3", attachment.Id); });
        var unavailable = Assert.IsType<ConversationQuote>(Assert.Single(history.Items, row => row.Text == "引用损坏语音").Quote);
        Assert.True(unavailable.Missing);
        Assert.Null(unavailable.Attachment);
        Assert.Equal("原文", Assert.Single(history.Items, row => row.Text == "恢复原文").Quote?.Text);
        Assert.Throws<KeyNotFoundException>(() => store.GetOwnerAttachment(Bot, "inbound", mixed.Id, "1"));
    }

    [Fact]
    public void History_pages_across_filtered_batches_with_inbound_and_outbound_timestamp_ties()
    {
        using var store = new CoreStore(_directory.FullName, new FakeTimeProvider());
        Bind(store);
        const int outboundCount = 24;
        const int inboundCount = 28;
        for (var i = 0; i < outboundCount; i++)
        {
            var id = $"sent-{i:D2}";
            var wire = new JsonObject { ["msg"] = new JsonObject { ["item_list"] = new JsonArray(Text(id)) } };
            store.RecordSend(id, Bot, Owner, null, wire.ToJsonString(), i == 23 ? "failed" : "success", i % 2 == 0 ? $"platform-{i}" : null, null);
        }
        store.CommitBatch(Bot, new JsonArray(Enumerable.Range(0, inboundCount)
            .Select(i => Message($"received-{i:D2}", Owner, Text($"received-{i:D2}"))).ToArray()), null);
        for (var i = 0; i < 55; i++)
            store.RecordSend($"filtered-send-{i}", Bot, Owner, null, "{}", "success", null, null);
        store.CommitBatch(Bot, new JsonArray(Enumerable.Range(0, 53).Select(i => Message($"filtered-inbound-{i}", Owner)).ToArray()), null);
        var group = Message("ignored-group", Owner, Text("群消息"));
        group["group_id"] = "group";
        store.CommitBatch(Bot, [group], null);

        var first = store.ReadOwnerConversation(Bot, null);
        Assert.Equal(50, first.Items.Count);
        var second = store.ReadOwnerConversation(Bot, Assert.IsType<string>(first.NextBefore));
        Assert.Null(second.NextBefore);
        var messages = first.Items.Concat(second.Items).ToArray();
        Assert.Equal(
            Enumerable.Range(0, outboundCount).Reverse().Select(i => $"sent-{i:D2}")
                .Concat(Enumerable.Range(0, inboundCount).Reverse().Select(i => $"received-{i:D2}")),
            messages.Select(row => row.Text));
        Assert.False(messages[0].AwaitingDeliveryConfirmation); // Failed request.
        Assert.False(messages[1].AwaitingDeliveryConfirmation); // Successful request with a server ID.
        Assert.True(messages[2].AwaitingDeliveryConfirmation); // Accepted without a server ID.
        Assert.False(messages[^1].AwaitingDeliveryConfirmation); // An inbound message needs no receipt.

        store.Configure(Bot, new AccountConfiguration(Allowlist: ["other-peer"]));
        var restricted = store.ReadOwnerConversation(Bot, null);
        Assert.False(restricted.HasSendContext);
        Assert.Null(restricted.NextBefore);
        Assert.Equal(messages.Take(outboundCount).Select(row => row.Id), restricted.Items.Select(row => row.Id));
    }

    [Fact]
    public async Task Downloaded_voice_is_playable_after_ack_and_restart_until_account_is_deleted()
    {
        string delivery;
        using (var store = Store())
        {
            Bind(store);
            delivery = Assert.Single(store.CommitBatch(Bot, [Message("speech", Owner, Voice("speech", "您好"))], null));
        }
        var silk = await File.ReadAllBytesAsync(Path.Combine(AppContext.BaseDirectory, "Fixtures", "tone-24000.silk"));
        var downloads = 0;
        using var http = new HttpClient(new Handler((_, _) =>
        {
            downloads++;
            return Task.FromResult(Response(silk));
        }));
        string path;
        await using (var channel = Open(http))
        {
            var attachment = await channel.GetOwnerAttachmentAsync(Bot, "inbound", delivery, "0");
            path = attachment.Path;
            Assert.Equal("audio/wav", attachment.MimeType);
            Assert.StartsWith("RIFF", Encoding.ASCII.GetString(await File.ReadAllBytesAsync(path)));
            var claim = Assert.IsType<InboundMessage>(await channel.ClaimAsync(Bot));
            Assert.Equal("您好", claim.Text);
            await channel.AcknowledgeAsync(claim.DeliveryId, claim.LeaseId);
            Assert.True(File.Exists(path));
        }
        await using var reopened = Open(http);
        Assert.Equal(path, (await reopened.GetOwnerAttachmentAsync(Bot, "inbound", delivery, "0")).Path);
        Assert.Equal(1, downloads);
        await reopened.DeleteAccountAsync(Bot);
        Assert.False(File.Exists(path));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => reopened.GetOwnerAttachmentAsync(Bot, "inbound", delivery, "0"));
    }

    [Fact]
    public async Task Unavailable_media_keeps_history_and_bad_silk_remains_downloadable()
    {
        var invalidUrl = Item(2, "picture");
        invalidUrl["image_item"]!["media"]!["full_url"] = "https://elsewhere.test/picture";
        string delivery;
        using (var store = Store())
        {
            Bind(store);
            delivery = Assert.Single(store.CommitBatch(Bot, [Message("mixed", Owner, invalidUrl, Voice("broken", "转写"), Item(5, "expired"))], null));
        }
        var broken = Encoding.ASCII.GetBytes("#!SILK_V3!");
        using var http = new HttpClient(new Handler((request, _) =>
        {
            if (request.RequestUri == new Uri(Cdn, "expired")) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest));
            Assert.Equal(new Uri(Cdn, "broken"), request.RequestUri);
            return Task.FromResult(Response(broken));
        }));
        await using var channel = Open(http);
        await Assert.ThrowsAsync<KeyNotFoundException>(() => channel.GetOwnerAttachmentAsync(Bot, "inbound", delivery, "0"));
        var attachment = await channel.GetOwnerAttachmentAsync(Bot, "inbound", delivery, "1");
        Assert.Equal("audio/silk", attachment.MimeType);
        Assert.Equal(broken, await File.ReadAllBytesAsync(attachment.Path));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => channel.GetOwnerAttachmentAsync(Bot, "inbound", delivery, "2"));
        Assert.Equal(3, Assert.Single(channel.GetOwnerConversation(Bot).Items).Attachments.Count);
    }

    [Fact]
    public async Task In_flight_download_cannot_restore_files_after_account_deletion()
    {
        string delivery;
        using (var store = Store())
        {
            Bind(store);
            delivery = Assert.Single(store.CommitBatch(Bot, [Message("picture", Owner, Item(2, "picture"))], null));
        }
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var http = new HttpClient(new Handler(async (_, ct) =>
        {
            started.SetResult();
            await release.Task.WaitAsync(ct);
            return Response([137, 80, 78, 71]);
        }));
        await using var channel = Open(http);
        var fetch = channel.GetOwnerAttachmentAsync(Bot, "inbound", delivery, "0", timeout.Token);
        await started.Task.WaitAsync(timeout.Token);
        await channel.DeleteAccountAsync(Bot, timeout.Token);
        release.SetResult();
        await Assert.ThrowsAsync<KeyNotFoundException>(() => fetch);
        Assert.False(Directory.Exists(Path.Combine(_directory.FullName, "media", "conversation")));
    }

    private CoreStore Store() => new(_directory.FullName, TimeProvider.System);

    private WeixinChannel Open(HttpClient http) => new(new WeixinOptions
    {
        StateDirectory = _directory.FullName, CdnBaseUrl = Cdn, HttpClient = http
    });

    private static void Bind(CoreStore store) => store.SaveAccount(Bot, Owner, "token", "https://unit.test/");

    private static JsonObject Message(string id, string peer, params JsonNode[] items) => new()
    {
        ["message_id"] = id, ["from_user_id"] = peer, ["message_type"] = 1, ["context_token"] = "context",
        ["item_list"] = new JsonArray(items)
    };

    private static JsonObject Text(string text) => new()
    {
        ["type"] = 1, ["text_item"] = new JsonObject { ["text"] = text }
    };

    private static JsonObject Voice(string path, string transcript)
    {
        var voice = Item(3, path);
        voice["voice_item"]!["text"] = transcript;
        return voice;
    }

    private static JsonObject Item(int type, string path)
    {
        var field = type switch { 2 => "image_item", 3 => "voice_item", 4 => "file_item", _ => "video_item" };
        return new JsonObject
        {
            ["type"] = type, [field] = new JsonObject
            {
                ["file_name"] = type == 4 ? "测试.txt" : null,
                ["media"] = new JsonObject { ["full_url"] = new Uri(Cdn, path).AbsoluteUri, ["aes_key"] = Convert.ToBase64String(Key) }
            }
        };
    }

    private static HttpResponseMessage Response(byte[] bytes)
    {
        using var aes = Aes.Create();
        aes.Key = Key;
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(aes.EncryptEcb(bytes, PaddingMode.PKCS7)) };
    }

    public void Dispose() => _directory.Delete(recursive: true);

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> request) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage message, CancellationToken cancellationToken) => request(message, cancellationToken);
    }
}
