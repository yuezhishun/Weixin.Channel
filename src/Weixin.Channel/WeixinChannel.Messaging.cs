using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace Weixin.Channel;

public sealed partial class WeixinChannel
{
    private QuoteStore _quotes = null!;
    private readonly ConcurrentDictionary<string, (string Ticket, DateTimeOffset Next, TimeSpan Delay)> _typingTickets = new();
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _typingGates = new();
    private readonly object _progressGate = new();
    private readonly Dictionary<(string Account, string Peer, string Run), (Task Tail, bool Complete, string? Delivery)> _progress = [];
    private readonly Queue<(string Account, string Peer, string Run)> _completedProgress = [];

    internal void InitializeMessaging()
    {
        _quotes = new QuoteStore(_options, Log);
        CleanupTemporaryMedia();
    }
    internal void DisposeMessaging()
    {
        _quotes.Dispose();
        foreach (var gate in _typingGates.Values) gate.Dispose();
    }
    internal void DeleteMessagingAccount(string accountId)
    {
        _quotes.DeleteAccount(accountId);
        foreach (var key in _typingTickets.Keys.Where(key => key.StartsWith(accountId + "\0", StringComparison.Ordinal))) _typingTickets.TryRemove(key, out _);
    }

    private string DeliveryMediaDirectory(string deliveryId) => Path.Combine(_options.StateDirectory, "media", "inbox", QuoteStore.AccountDirectory(deliveryId));

    private void CleanupTemporaryMedia()
    {
        var retained = _store.Inbox(null, true).Where(row => row.State is not ("acked" or "denied"))
            .Select(row => QuoteStore.AccountDirectory(row.DeliveryId)).ToHashSet(StringComparer.Ordinal);
        var inbox = Path.Combine(_options.StateDirectory, "media", "inbox");
        var outgoing = Path.Combine(_options.StateDirectory, "media", "outgoing");
        var enumeration = new EnumerationOptions { AttributesToSkip = FileAttributes.ReparsePoint };
        try
        {
            if (Directory.Exists(inbox))
                foreach (var directory in Directory.EnumerateDirectories(inbox, "*", enumeration))
                    if (!retained.Contains(Path.GetFileName(directory))) Directory.Delete(directory, true);
            if (Directory.Exists(outgoing))
                foreach (var file in Directory.EnumerateFiles(outgoing, "*", enumeration)) File.Delete(file);
        }
        catch (IOException) { Log("启动时临时媒体清理失败，将在下次启动重试。"); }
        catch (UnauthorizedAccessException) { Log("启动时临时媒体无删除权限，将在下次启动重试。"); }
    }

    internal void DeleteDeliveryMedia(string deliveryId)
    {
        var directory = DeliveryMediaDirectory(deliveryId);
        if (!Directory.Exists(directory)) return;
        try { Directory.Delete(directory, true); }
        catch (IOException) { Log("消息临时媒体清理失败，保留文件供核查。"); }
        catch (UnauthorizedAccessException) { Log("消息临时媒体无删除权限，保留文件供核查。"); }
    }

    internal async Task<InboundMessage> MaterializeAsync(InboxRow row, CancellationToken ct)
    {
        var raw = JsonNode.Parse(row.Raw)?.AsObject() ?? throw new InvalidDataException("入站消息 JSON 无效。");
        var items = raw["item_list"] as JsonArray ?? [];
        var body = Media.Body(items);
        var attachments = new List<WeixinAttachment>();
        var main = Media.FirstMedia(items);
        var refItem = items.FirstOrDefault(item => item?["ref_msg"] is not null)?["ref_msg"] as JsonObject;
        var inline = refItem?["message_item"];
        var selected = main ?? (inline is not null && Media.Downloadable(inline) ? inline : null);
        WeixinAttachment? downloaded = null;
        if (selected is not null)
        {
            var cached = main is null ? null : _quotes.Find(row.AccountId, row.PeerId, row.MessageId, DeliveryMediaDirectory(row.DeliveryId))?.Attachment;
            downloaded = cached is not null ? cached with { Referenced = false } :
                await Media.DownloadAsync(_http, _options.CdnBaseUrl, DeliveryMediaDirectory(row.DeliveryId), selected, main is null, Log, ct, _options.MediaTimeout, _options.TimeProvider).ConfigureAwait(false);
            if (downloaded is not null) attachments.Add(downloaded);
        }
        WeixinQuote? quote = null;
        if (refItem is not null)
        {
            var id = Media.Text(refItem["svr_id"])?.Trim();
            if (string.IsNullOrEmpty(id)) id = Media.Text(inline?["msg_id"])?.Trim();
            var title = Media.Text(refItem["title"])?.Trim();
            var inlineBody = inline is null ? "" : Media.Body([inline]);
            var combined = string.Join(" | ", new[] { title, inlineBody }.Where(x => !string.IsNullOrEmpty(x)));
            if (combined.Length > 0)
                quote = new WeixinQuote(id, combined, false, main is null ? downloaded : null);
            else if (!string.IsNullOrEmpty(id))
                quote = _quotes.Find(row.AccountId, row.PeerId, id, DeliveryMediaDirectory(row.DeliveryId)) ?? new WeixinQuote(id, "[引用消息内容未缓存]", true);
            else quote = new WeixinQuote(null, "[引用消息内容未缓存]", true);
            if (refItem["partial_text"] is JsonObject partial && !quote.Missing && Media.PartialQuote(quote.Text, partial) is { } text)
                quote = quote with { Text = text };
            if (quote.Attachment is not null && !attachments.Any(a => a.Path == quote.Attachment.Path)) attachments.Add(quote.Attachment);
        }
        _quotes.Put(row.AccountId, row.PeerId, row.MessageId, body, main is not null ? downloaded : null, row.CreatedAt);
        return new InboundMessage
        {
            DeliveryId = row.DeliveryId, AccountId = row.AccountId, PeerId = row.PeerId, MessageId = row.MessageId,
            ReceivedAt = row.CreatedAt, Text = body, Attachments = attachments, Quote = quote,
            LeaseId = row.LeaseId, LeaseUntil = row.LeaseUntil, Channel = this
        };
    }

    public Task<SendResult> ReplyAsync(string deliveryId, string? text, string? media = null, CancellationToken cancellationToken = default) =>
        SendAsync(new SendRequest { DeliveryId = deliveryId, RunId = deliveryId, Text = text, Media = media }, cancellationToken);

    public Task<SendResult> SendAsync(SendRequest request, CancellationToken cancellationToken = default) =>
        TrackOperationAsync(token => SendCoreAsync(request, token), cancellationToken);
    private async Task<SendResult> SendCoreAsync(SendRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        request = request with { Text = Media.FilterMarkdown(request.Text ?? "") };
        if (_options.BeforeSend is not null)
        {
            try
            {
                var changed = await _options.BeforeSend(request, cancellationToken).ConfigureAwait(false);
                if (changed is null)
                {
                    var cancelled = _store.ResolveSend(request);
                    return new SendResult(cancelled.Account.AccountId, cancelled.PeerId, "cancelled", []);
                }
                request = changed;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception ex) { Log("发送前 hook 失败，保留原发送内容：" + ex.GetType().Name); }
        }
        var target = _store.ResolveSend(request);
        EnsureAccountAvailable(target.Account);
        if (string.IsNullOrEmpty(request.Text) && string.IsNullOrWhiteSpace(request.Media)) return new SendResult(target.Account.AccountId, target.PeerId, "cancelled", []);
        if (_store.HasUnknownSend(request.DeliveryId))
        {
            if (!request.AllowUnknownRetry) throw new InvalidOperationException("此 DeliveryId 存在发送结果未知的记录，请先核查；显式重试可能造成重复消息。");
            Log("已显式允许未知发送重试，可能产生重复消息。");
            _store.ApproveUnknownSends(request.DeliveryId!);
        }
        var parts = new List<SendSegment>();
        var chunks = Media.Chunk(request.Text ?? "", _options.TextChunkSize);
        foreach (var chunk in chunks)
        {
            var segment = await SendItemAsync(request, target.Account, target.PeerId, target.ContextToken,
                new JsonObject { ["type"] = 1, ["text_item"] = new JsonObject { ["text"] = chunk } }, chunk, null, cancellationToken).ConfigureAwait(false);
            parts.Add(segment);
            if (segment.Status != "success") break;
        }
        if (!string.IsNullOrWhiteSpace(request.Media) && parts.All(p => p.Status == "success"))
        {
            string? temporaryPath = null;
            try
            {
                var path = await Media.ResolveFileAsync(_http, request.Media, Path.Combine(_options.StateDirectory, "media", "outgoing"), cancellationToken, _options.MediaTimeout, _options.TimeProvider).ConfigureAwait(false);
                if (Uri.TryCreate(request.Media, UriKind.Absolute, out var source) && source.Scheme is "http" or "https") temporaryPath = path;
                var mime = Media.Mime(path);
                var type = mime.StartsWith("image/", StringComparison.Ordinal) ? 2 : mime.StartsWith("video/", StringComparison.Ordinal) ? 5 : 4;
                var bytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
                if (bytes.Length > Media.MaximumBytes) throw new InvalidDataException("媒体超过 100 MiB 限制。");
                var key = RandomNumberGenerator.GetBytes(16);
                var fileKey = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
                var encryptedSize = (bytes.Length / 16 + 1) * 16;
                var upload = await _protocol.PostAsync(target.Account, "ilink/bot/getuploadurl", new JsonObject
                {
                    ["filekey"] = fileKey, ["media_type"] = type == 2 ? 1 : type == 5 ? 2 : 3,
                    ["to_user_id"] = target.PeerId, ["rawsize"] = bytes.Length, ["rawfilemd5"] = Convert.ToHexStringLower(MD5.HashData(bytes)),
                    ["filesize"] = encryptedSize, ["no_need_thumb"] = true, ["aeskey"] = Convert.ToHexStringLower(key)
                }, null, cancellationToken).ConfigureAwait(false);
                if (ProtocolClient.Error(upload) == -14) _store.Pause(target.Account.AccountId, TimeSpan.FromHours(1));
                if (ProtocolClient.Error(upload) != 0) throw new InvalidDataException("微信拒绝媒体上传申请。");
                var parameter = await Media.UploadAsync(_http, _options.CdnBaseUrl, upload, fileKey, bytes, key, cancellationToken, _options.MediaTimeout, _options.TimeProvider).ConfigureAwait(false);
                var media = new JsonObject { ["encrypt_query_param"] = parameter, ["aes_key"] = Convert.ToBase64String(Encoding.ASCII.GetBytes(Convert.ToHexStringLower(key))), ["encrypt_type"] = 1 };
                var content = new JsonObject { ["media"] = media };
                if (type == 2) content["mid_size"] = encryptedSize;
                else if (type == 5) content["video_size"] = encryptedSize;
                else { content["file_name"] = Path.GetFileName(path); content["len"] = bytes.Length.ToString(System.Globalization.CultureInfo.InvariantCulture); }
                var item = new JsonObject { ["type"] = type, [type == 2 ? "image_item" : type == 5 ? "video_item" : "file_item"] = content };
                var attachment = new WeixinAttachment(type == 2 ? "image" : type == 5 ? "video" : "file", path, mime, Path.GetFileName(path));
                parts.Add(await SendItemAsync(request, target.Account, target.PeerId, target.ContextToken, item, Media.Label(type), attachment, cancellationToken).ConfigureAwait(false));
            }
            catch (Exception ex)
            {
                // 上传尚未调用 sendmessage，失败可以安全重试；caption 的成功记录仍保留。
                var clientId = "weixin-" + Guid.NewGuid().ToString("N");
                var error = "媒体准备失败：" + ProtocolClient.Classify(ex);
                _store.RecordSend(clientId, target.Account.AccountId, target.PeerId, request.DeliveryId, "{}", "failed", null, error);
                parts.Add(new SendSegment(clientId, null, "failed", error));
            }
            finally
            {
                if (temporaryPath is not null)
                    try { File.Delete(temporaryPath); } catch (IOException) { Log("临时出站媒体清理失败。"); }
            }
        }
        var status = parts.Any(p => p.Status == "unknown") ? "unknown" : parts.All(p => p.Status == "success") ? "success" : parts.Any(p => p.Status == "success") ? "partial" : "failed";
        return await NotifySentAsync(new SendResult(target.Account.AccountId, target.PeerId, status, parts), cancellationToken).ConfigureAwait(false);
    }

    private async Task<SendResult> NotifySentAsync(SendResult result, CancellationToken ct)
    {
        Publish("sent", result.AccountId, null, result.Status);
        if (_options.AfterSend is not null)
        {
            try { await _options.AfterSend(result, ct).ConfigureAwait(false); }
            catch (Exception ex) { Log("发送后 hook 失败，已成功的发送结果保持不变：" + ex.GetType().Name); }
        }
        return result;
    }

    private async Task<SendSegment> SendItemAsync(SendRequest request, AccountRow account, string peer, string? token,
        JsonObject item, string body, WeixinAttachment? attachment, CancellationToken ct, bool auxiliary = false)
    {
        var clientId = "weixin-" + Guid.NewGuid().ToString("N");
        var message = new JsonObject
        {
            ["from_user_id"] = "", ["to_user_id"] = peer, ["client_id"] = clientId,
            ["message_type"] = 2, ["message_state"] = 2, ["item_list"] = new JsonArray(item)
        };
        if (!string.IsNullOrEmpty(token)) message["context_token"] = token;
        if (!string.IsNullOrEmpty(request.RunId)) message["run_id"] = request.RunId;
        var payload = new JsonObject { ["msg"] = message };
        var raw = payload.ToJsonString();
        ct.ThrowIfCancellationRequested();
        var delivery = auxiliary ? null : request.DeliveryId;
        _store.RecordSend(clientId, account.AccountId, peer, delivery, raw, "sending", null, null);
        string? serverId = null;
        var status = "unknown";
        string? error = null;
        try
        {
            var response = await _protocol.PostAsync(account, "ilink/bot/sendmessage", payload, null, ct).ConfigureAwait(false);
            if (ProtocolClient.Error(response) != 0)
            {
                if (ProtocolClient.Error(response) == -14) _store.Pause(account.AccountId, TimeSpan.FromHours(1));
                status = "failed";
                error = "微信拒绝发送，错误码：" + ProtocolClient.Error(response);
            }
            else { serverId = Media.Text(response["message_id"]); status = "success"; }
        }
        catch (Exception ex)
        {
            status = ex is HttpRequestException { StatusCode: >= System.Net.HttpStatusCode.BadRequest and < System.Net.HttpStatusCode.InternalServerError } ||
                ex.Data.Contains("WeixinRequestNotSent") ? "failed" : "unknown";
            error = "发送未完成：" + ProtocolClient.Classify(ex) + (status == "unknown" ? "，可能已送达，请核查后再重试。" : "");
        }
        try { _store.RecordSend(clientId, account.AccountId, peer, delivery, raw, status, serverId, error); }
        catch (Exception ex)
        {
            Log("发送结果持久化失败，原 sending 记录必须按未知结果核查：" + ex.GetType().Name);
            return new SendSegment(clientId, serverId, "unknown", "发送结果未能持久化，必须核查，不能自动重发。");
        }
        if (status == "success" && !string.IsNullOrEmpty(serverId)) _quotes.Put(account.AccountId, peer, serverId, body, attachment, _options.TimeProvider.GetUtcNow());
        return new SendSegment(clientId, serverId, status, error);
    }

    public Task TypingAsync(string peerId, bool active = true, string? accountId = null, string? deliveryId = null, CancellationToken cancellationToken = default) =>
        TrackOperationAsync(token => TypingCoreAsync(peerId, active, accountId, deliveryId, token), cancellationToken);
    private async Task TypingCoreAsync(string peerId, bool active, string? accountId, string? deliveryId, CancellationToken cancellationToken)
    {
        var target = _store.ResolveSend(new SendRequest { PeerId = peerId, AccountId = accountId, DeliveryId = deliveryId });
        EnsureAccountAvailable(target.Account);
        var cacheKey = target.Account.AccountId + "\0" + target.PeerId;
        var gate = _typingGates.GetOrAdd(cacheKey, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        string ticket;
        try
        {
            var now = _options.TimeProvider.GetUtcNow();
            var exists = _typingTickets.TryGetValue(cacheKey, out var entry);
            if (active && (!exists || entry.Next <= now))
            {
                try
                {
                    var request = new JsonObject { ["ilink_user_id"] = target.PeerId };
                    if (!string.IsNullOrEmpty(target.ContextToken)) request["context_token"] = target.ContextToken;
                    var response = await _protocol.PostAsync(target.Account, "ilink/bot/getconfig", request, TimeSpan.FromSeconds(10), cancellationToken).ConfigureAwait(false);
                    if (ProtocolClient.Error(response) == -14) _store.Pause(target.Account.AccountId, TimeSpan.FromHours(1));
                    if (response["ret"] is null || ProtocolClient.Error(response) != 0) throw new InvalidDataException("typing 配置不可用。");
                    entry = (Media.Text(response["typing_ticket"]) ?? "", now + TimeSpan.FromMilliseconds(Random.Shared.NextDouble() * TimeSpan.FromHours(24).TotalMilliseconds), TimeSpan.FromSeconds(2));
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                catch (Exception ex)
                {
                    var delay = exists ? TimeSpan.FromMilliseconds(Math.Min(TimeSpan.FromHours(1).TotalMilliseconds, entry.Delay.TotalMilliseconds * 2)) : TimeSpan.FromSeconds(2);
                    entry = (exists ? entry.Ticket : "", now + delay, delay);
                    Log("typing 配置获取失败，保留缓存并退避：" + ex.GetType().Name);
                }
                _typingTickets[cacheKey] = entry;
            }
            ticket = entry.Ticket ?? "";
        }
        finally { gate.Release(); }
        if (string.IsNullOrEmpty(ticket)) return;
        var result = await _protocol.PostAsync(target.Account, "ilink/bot/sendtyping", new JsonObject
        {
            ["ilink_user_id"] = target.PeerId, ["typing_ticket"] = ticket, ["status"] = active ? 1 : 2
        }, TimeSpan.FromSeconds(10), cancellationToken).ConfigureAwait(false);
        if (ProtocolClient.Error(result) == -14) _store.Pause(target.Account.AccountId, TimeSpan.FromHours(1));
        if (ProtocolClient.Error(result) != 0) throw new InvalidDataException("微信拒绝 typing 状态。");
    }

    public Task WithTypingAsync(string peerId, Func<CancellationToken, Task> action, string? accountId = null,
        string? deliveryId = null, CancellationToken cancellationToken = default) =>
        TrackOperationAsync(token => WithTypingCoreAsync(peerId, action, accountId, deliveryId, token), cancellationToken);
    private async Task WithTypingCoreAsync(string peerId, Func<CancellationToken, Task> action, string? accountId,
        string? deliveryId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(action);
        using var keepalive = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        async Task SetTyping(bool active, CancellationToken ct)
        {
            try { await TypingAsync(peerId, active, accountId, deliveryId, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
            catch (Exception ex) { Log("typing 更新失败：" + ex.GetType().Name); }
        }
        await SetTyping(true, cancellationToken).ConfigureAwait(false);
        async Task Loop()
        {
            try
            {
                while (!keepalive.IsCancellationRequested)
                {
                    await Task.Delay(TimeSpan.FromSeconds(5), _options.TimeProvider, keepalive.Token).ConfigureAwait(false);
                    await SetTyping(true, keepalive.Token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (keepalive.IsCancellationRequested) { }
        }
        var loop = Loop();
        try { await action(cancellationToken).ConfigureAwait(false); }
        finally
        {
            await keepalive.CancelAsync().ConfigureAwait(false);
            await loop.ConfigureAwait(false);
            await SetTyping(false, CancellationToken.None).ConfigureAwait(false);
        }
    }

    public Task ProgressAsync(ToolProgress progress, CancellationToken cancellationToken = default) =>
        TrackOperationAsync(token => ProgressCoreAsync(progress, token), cancellationToken);
    private Task ProgressCoreAsync(ToolProgress progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(progress);
        ArgumentException.ThrowIfNullOrWhiteSpace(progress.RunId);
        ArgumentException.ThrowIfNullOrWhiteSpace(progress.ToolCallId);
        ArgumentException.ThrowIfNullOrWhiteSpace(progress.ToolName);
        if (!_options.EnableProgress) return Task.CompletedTask;
        var request = new SendRequest { AccountId = progress.AccountId, PeerId = progress.PeerId, DeliveryId = progress.DeliveryId, RunId = progress.RunId };
        var target = _store.ResolveSend(request);
        EnsureAccountAvailable(target.Account);
        var key = (target.Account.AccountId, target.PeerId, progress.RunId);
        lock (_progressGate)
        {
            var previous = _progress.GetValueOrDefault(key, (Tail: Task.CompletedTask, Complete: false, Delivery: progress.DeliveryId));
            if (previous.Complete) return Task.CompletedTask;
            async Task SendNext()
            {
                await previous.Tail.ConfigureAwait(false);
                var completed = progress.Status is not null;
                var payload = new JsonObject { ["tool_name"] = string.IsNullOrWhiteSpace(progress.ToolName) ? "tool" : progress.ToolName.Trim(), ["tool_call_id"] = progress.ToolCallId };
                if (completed) payload["status"] = progress.Status is "completed" or "failed" or "blocked" ? progress.Status : "unknown";
                var item = new JsonObject
                {
                    ["type"] = completed ? 12 : 11, ["create_time_ms"] = _options.TimeProvider.GetUtcNow().ToUnixTimeMilliseconds(),
                    ["is_completed"] = completed, [completed ? "tool_call_result_item" : "tool_call_start_item"] = payload
                };
                try
                {
                    var result = await SendItemAsync(request, target.Account, target.PeerId, target.ContextToken, item, "", null, cancellationToken, true).ConfigureAwait(false);
                    if (result.Status != "success") Log("工具进度发送未成功：" + result.Status);
                }
                catch (Exception ex) { Log("工具进度发送失败：" + ex.GetType().Name); }
            }
            var task = SendNext();
            _progress[key] = (task, false, progress.DeliveryId);
            return task;
        }
    }

    public Task DrainProgressAsync(string runId, CancellationToken cancellationToken = default) =>
        TrackOperationAsync(token => DrainProgressCoreAsync(runId, token), cancellationToken);
    private async Task DrainProgressCoreAsync(string runId, CancellationToken cancellationToken)
    {
        Task pending;
        lock (_progressGate)
        {
            var keys = _progress.Keys.Where(key => key.Run == runId).ToArray();
            pending = CompleteProgress(keys);
        }
        await pending.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    internal async Task DrainDeliveryProgressAsync(string deliveryId, CancellationToken ct = default)
    {
        Task pending;
        lock (_progressGate) pending = CompleteProgress(_progress.Where(pair => pair.Value.Delivery == deliveryId).Select(pair => pair.Key).ToArray());
        await pending.WaitAsync(ct).ConfigureAwait(false);
    }

    internal void BeginDeliveryAttempt(string deliveryId)
    {
        lock (_progressGate)
        {
            foreach (var key in _progress.Where(pair => pair.Value.Delivery == deliveryId && pair.Value.Complete).Select(pair => pair.Key).ToArray())
            {
                var entry = _progress[key];
                _progress[key] = (entry.Tail, false, entry.Delivery);
            }
            var completed = _completedProgress.Where(key => _progress.TryGetValue(key, out var entry) && entry.Complete).ToArray();
            _completedProgress.Clear();
            foreach (var key in completed) _completedProgress.Enqueue(key);
        }
    }

    private Task CompleteProgress((string Account, string Peer, string Run)[] keys)
    {
        var pending = keys.Select(key => _progress[key].Tail).ToArray();
        foreach (var key in keys)
        {
            var entry = _progress[key];
            if (!entry.Complete) _completedProgress.Enqueue(key);
            _progress[key] = (entry.Tail, true, entry.Delivery);
        }
        while (_completedProgress.Count > 256)
        {
            var oldest = _completedProgress.Peek();
            if (!_progress[oldest].Tail.IsCompleted) break;
            _completedProgress.Dequeue();
            _progress.Remove(oldest);
        }
        return Task.WhenAll(pending);
    }
}
