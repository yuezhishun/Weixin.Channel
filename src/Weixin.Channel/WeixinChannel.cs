using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using System.Threading.Channels;

namespace Weixin.Channel;

public sealed partial class WeixinChannel : IAsyncDisposable
{
    private readonly WeixinOptions _options;
    private readonly HttpClient _http;
    private readonly CoreStore _store;
    private readonly ProtocolClient _protocol;
    private readonly FileStream _directoryLock;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly SemaphoreSlim _loginCommit = new(1, 1);
    private readonly object _runGate = new();
    private readonly Dictionary<string, (CancellationTokenSource Stop, bool Handler, TaskCompletionSource Done)> _runs = [];
    private readonly ConcurrentDictionary<Guid, System.Threading.Channels.Channel<WeixinEvent>> _subscribers = new();
    private readonly ConcurrentDictionary<string, (DateTimeOffset? Activity, DateTimeOffset? Inbound, string? Error)> _status = new();
    private bool _disposed;

    public WeixinChannel(WeixinOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.TextChunkSize < 2) throw new ArgumentOutOfRangeException(nameof(options), "文本分块至少两个 UTF-16 单元。");
        if (options.LeaseDuration <= TimeSpan.Zero || options.LeaseRenewInterval <= TimeSpan.Zero || options.LeaseRenewInterval >= options.LeaseDuration)
            throw new ArgumentOutOfRangeException(nameof(options), "续租间隔必须大于零且小于租约长度。");
        if (options.MediaTimeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(options), "媒体请求超时必须大于零。");
        _options = options with { StateDirectory = Path.GetFullPath(options.StateDirectory) };
        Directory.CreateDirectory(_options.StateDirectory);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(_options.StateDirectory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        try { _directoryLock = new FileStream(Path.Combine(_options.StateDirectory, ".process.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException e) { throw new IOException("状态目录正被另一个进程使用；CLI 与 Web 必须使用不同目录或停止另一进程。", e); }
        _http = options.HttpClient ?? new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        try
        {
            _store = new CoreStore(_options.StateDirectory, options.TimeProvider);
            _store.RecoverInterruptedSends();
            _protocol = new ProtocolClient(_http, options, _store);
            InitializeMessaging();
            DeleteConversationDirectory(ConversationStagingDirectory);
        }
        catch
        {
            _directoryLock.Dispose();
            if (options.HttpClient is null) _http.Dispose();
            throw;
        }
    }

    public IReadOnlyList<WeixinAccount> GetAccounts() => ReadState<IReadOnlyList<WeixinAccount>>(() =>
    {
        lock (_runGate) return _store.Accounts().Select(a =>
        {
            var status = _status.GetValueOrDefault(a.AccountId);
            return PublicAccount(a, _runs.ContainsKey(a.AccountId)) with { LastActivityAt = status.Activity, LastInboundAt = status.Inbound, LastError = status.Error };
        }).ToArray();
    });
    private static WeixinAccount PublicAccount(AccountRow account, bool running = false) => new(account.AccountId, account.OwnerId, account.BaseUrl, account.Enabled, account.Debug, account.Allowlist, account.PausedUntil, running)
    {
        Remark = account.Remark
    };
    public void ConfigureAccount(string accountId, AccountConfiguration configuration)
    {
        var stop = ReadState(() =>
        {
            _store.Configure(accountId, configuration);
            CancellationTokenSource? cancellation = null;
            if (configuration.Enabled == false)
                lock (_runGate) if (_runs.TryGetValue(CoreStore.NormalizeAccountId(accountId), out var run)) cancellation = run.Stop;
            Publish("account", CoreStore.NormalizeAccountId(accountId), null, "configured");
            return cancellation;
        });
        try { stop?.Cancel(); }
        catch (ObjectDisposedException) { }
        catch (AggregateException) { Log("账号取消回调失败，配置变更仍已保存。"); }
    }
    public Task DeleteAccountAsync(string accountId, CancellationToken cancellationToken = default) =>
        TrackOperationAsync(token => DeleteAccountCoreAsync(accountId, token), cancellationToken);
    private async Task DeleteAccountCoreAsync(string accountId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var id = CoreStore.NormalizeAccountId(accountId);
        var gate = AccountGate(id);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await StopRunAsync(id).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            DeleteAccountState(id);
            Publish("account", id, null, "deleted");
        }
        finally { gate.Release(); }
    }
    private void DeleteAccountState(string accountId)
    {
        foreach (var entry in _store.Inbox(accountId, true)) DeleteDeliveryMedia(entry.DeliveryId);
        _store.DeleteAccount(accountId);
        DeleteMessagingAccount(accountId);
        DeleteConversationDirectory(ConversationAccountDirectory(accountId));
        _status.TryRemove(accountId, out _);
    }

    internal void EnsureAccountAvailable(AccountRow account)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        account = _store.GetAccount(account.AccountId);
        if (!account.Enabled) throw new InvalidOperationException("账号已停用。");
        if (account.PausedUntil > _options.TimeProvider.GetUtcNow()) throw new InvalidOperationException("账号登录态失效，当前暂停一小时；请重新登录或等待暂停结束。");
    }

    public Task RunAsync(string accountId, Func<InboundMessage, CancellationToken, Task>? handler = null, CancellationToken cancellationToken = default) =>
        TrackOperationAsync(token => RunCoreAsync(accountId, handler, token), cancellationToken);
    private async Task RunCoreAsync(string accountId, Func<InboundMessage, CancellationToken, Task>? handler, CancellationToken cancellationToken)
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdown.Token);
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var id = CoreStore.NormalizeAccountId(accountId);
        var gate = AccountGate(id);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        AccountRow account;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            account = _store.GetAccount(id);
            if (!account.Enabled) throw new InvalidOperationException("账号已停用。");
            lock (_runGate)
            {
                if (_runs.ContainsKey(account.AccountId)) throw new InvalidOperationException("该账号已经运行；一个账号只能选择 handler 或手动消费模式。");
                _runs.Add(account.AccountId, (stop, handler is not null, done));
            }
        }
        finally { gate.Release(); }
        Publish("account", account.AccountId, null, "started");
        try
        {
            await NotifyAsync(account, "notifystart", stop.Token).ConfigureAwait(false);
            var poll = PollAsync(account.AccountId, stop.Token);
            if (handler is null) await poll.ConfigureAwait(false);
            else await Task.WhenAll(poll, ConsumeAsync(account.AccountId, handler, stop.Token)).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        finally
        {
            stop.Cancel();
            await NotifyAsync(account, "notifystop", CancellationToken.None).ConfigureAwait(false);
            lock (_runGate) _runs.Remove(account.AccountId);
            Publish("account", account.AccountId, null, "stopped");
            done.TrySetResult();
        }
    }

    public Task StopAsync(string accountId) => TrackOperationAsync(async token =>
    {
        var id = CoreStore.NormalizeAccountId(accountId);
        var gate = AccountGate(id);
        await gate.WaitAsync(token).ConfigureAwait(false);
        try { await StopRunAsync(id).ConfigureAwait(false); }
        finally { gate.Release(); }
    }, CancellationToken.None);

    private async Task StopRunAsync(string accountId)
    {
        Task? task = null;
        CancellationTokenSource? stop = null;
        lock (_runGate)
        {
            if (_runs.TryGetValue(CoreStore.NormalizeAccountId(accountId), out var run))
            {
                stop = run.Stop;
                task = run.Done.Task;
            }
        }
        try { if (stop is not null) await stop.CancelAsync().ConfigureAwait(false); }
        catch (ObjectDisposedException) { }
        catch (AggregateException) { Log("账号取消回调失败，继续等待账号停止。"); }
        if (task is not null) await task.ConfigureAwait(false);
    }

    private async Task NotifyAsync(AccountRow account, string operation, CancellationToken cancellationToken)
    {
        try { await _protocol.PostAsync(account, "ilink/bot/msg/" + operation, new JsonObject(), TimeSpan.FromSeconds(3), cancellationToken).ConfigureAwait(false); }
        catch (Exception e) { Log($"{operation}: {ProtocolClient.Classify(e)}（最佳努力通知）"); }
    }

    private async Task PollAsync(string accountId, CancellationToken cancellationToken)
    {
        var timeout = TimeSpan.FromSeconds(35);
        var failures = 0;
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var account = _store.GetAccount(accountId);
                if (!account.Enabled) return;
                if (account.PausedUntil is { } pause && pause > _options.TimeProvider.GetUtcNow())
                {
                    await DelayAsync(pause - _options.TimeProvider.GetUtcNow(), cancellationToken).ConfigureAwait(false);
                    continue;
                }
                var response = await _protocol.PostAsync(account, "ilink/bot/getupdates", new JsonObject { ["get_updates_buf"] = _store.Cursor(accountId) }, timeout, cancellationToken).ConfigureAwait(false);
                if (ProtocolClient.Number(response["longpolling_timeout_ms"]) is > 0 and <= int.MaxValue)
                    timeout = TimeSpan.FromMilliseconds(ProtocolClient.Number(response["longpolling_timeout_ms"]));
                if (ProtocolClient.Error(response) == -14)
                {
                    _store.Pause(accountId, TimeSpan.FromHours(1));
                    Publish("account", accountId, null, "session-paused");
                    failures = 0;
                    continue;
                }
                if (ProtocolClient.Error(response) != 0) throw new InvalidDataException($"轮询接口错误代码 {ProtocolClient.Error(response)}。");
                if (response["msgs"] is not null and not JsonArray) throw new InvalidDataException("轮询 msgs 必须是数组；拒绝推进游标。");
                var deliveries = _store.CommitBatch(accountId, response["msgs"] as JsonArray ?? [], ProtocolClient.Text(response["get_updates_buf"]));
                var now = _options.TimeProvider.GetUtcNow();
                _status.AddOrUpdate(accountId, (now, deliveries.Count > 0 ? now : null, null), (_, prior) => (now, deliveries.Count > 0 ? now : prior.Inbound, null));
                failures = 0;
                foreach (var delivery in deliveries) Publish("inbox", accountId, delivery, "pending");
            }
            catch (TimeoutException) { failures = 0; }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return; }
            catch (Exception e)
            {
                failures++;
                Log($"轮询失败 {accountId}: {ProtocolClient.Classify(e)} ({failures}/3)");
                Publish("error", accountId, null, ProtocolClient.Classify(e));
                _status.AddOrUpdate(accountId, (null, null, ProtocolClient.Classify(e)), (_, prior) => (prior.Activity, prior.Inbound, ProtocolClient.Classify(e)));
                await DelayAsync(TimeSpan.FromSeconds(failures >= 3 ? 30 : 2), cancellationToken).ConfigureAwait(false);
                if (failures >= 3) failures = 0;
            }
        }
    }

    public IReadOnlyList<InboxEntry> GetInbox(string? accountId = null, bool includeCompleted = false) =>
        ReadState(() => _store.Inbox(accountId, includeCompleted));
    public IReadOnlyList<SendSegment> GetSendSegments(string deliveryId) => ReadState(() => _store.SendSegments(deliveryId));
    public OwnerConversation GetOwnerConversation(string accountId, string? before = null) =>
        ReadState(() => _store.ReadOwnerConversation(accountId, before));

    public Task<InboundMessage?> ClaimAsync(string? accountId = null, CancellationToken cancellationToken = default) =>
        TrackOperationAsync(token => ClaimCoreAsync(accountId, false, token), cancellationToken);

    private async Task<InboundMessage?> ClaimCoreAsync(string? accountId, bool handler, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        HashSet<string> excluded;
        lock (_runGate)
        {
            excluded = _runs.Where(x => x.Value.Handler && !handler).Select(x => x.Key).ToHashSet(StringComparer.Ordinal);
            if (accountId is not null && excluded.Contains(CoreStore.NormalizeAccountId(accountId))) throw new InvalidOperationException("账号使用 handler 消费，不能手动 claim。");
        }
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var row = _store.Claim(accountId, _options.LeaseDuration, excluded);
            if (row is null) return null;
            using var processing = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var renewal = RenewLoopAsync(row.DeliveryId, row.LeaseId, processing);
            try
            {
                var account = _store.GetAccount(row.AccountId);
                var raw = JsonNode.Parse(row.Raw)!.AsObject();
                var denial = account.InboundDenial(row.PeerId, raw);
                if (denial is not null)
                {
                    _store.Complete(row.DeliveryId, row.LeaseId, true, denial);
                    DeleteDeliveryMedia(row.DeliveryId);
                    Publish("inbox", row.AccountId, row.DeliveryId, "denied");
                    continue;
                }
                BeginDeliveryAttempt(row.DeliveryId);
                if (_options.EnableBuiltinCommands && await BuiltinAsync(row, raw, processing.Token).ConfigureAwait(false))
                {
                    _store.Complete(row.DeliveryId, row.LeaseId);
                    DeleteDeliveryMedia(row.DeliveryId);
                    Publish("inbox", row.AccountId, row.DeliveryId, "acked");
                    continue;
                }
                var message = await MaterializeAsync(row, processing.Token).ConfigureAwait(false);
                var until = _store.Renew(row.DeliveryId, row.LeaseId, _options.LeaseDuration);
                Publish("inbox", row.AccountId, row.DeliveryId, "leased");
                return message with { LeaseUntil = until };
            }
            catch (Exception e)
            {
                try { _store.Retry(row.DeliveryId, row.LeaseId, false, true, ProtocolClient.Classify(e), cancellationToken.IsCancellationRequested); }
                catch (InvalidOperationException) { Log("预处理期间租约已失效，当前领取者保留所有权。"); }
                throw;
            }
            finally
            {
                processing.Cancel();
                await renewal.ConfigureAwait(false);
            }
        }
    }
    private async Task<bool> BuiltinAsync(InboxRow message, JsonObject raw, CancellationToken cancellationToken)
    {
        var textItem = (raw["item_list"] as JsonArray ?? []).FirstOrDefault(item => ProtocolClient.Number(item?["type"]) == 1 && item?["text_item"]?["text"] is not null);
        var trimmed = (ProtocolClient.Text(textItem?["text_item"]?["text"]) ?? "").Trim();
        var parts = trimmed.Split(' ', 2, StringSplitOptions.TrimEntries);
        string? response = null;
        if (string.Equals(parts[0], "/toggle-debug", StringComparison.OrdinalIgnoreCase)) response = _store.ToggleDebugOnce(message.DeliveryId);
        else if (string.Equals(parts[0], "/echo", StringComparison.OrdinalIgnoreCase))
            response = (parts.Length > 1 ? parts[1] + "\n" : "") + $"⏱ 通道耗时\n平台→插件: {(_options.TimeProvider.GetUtcNow() - message.CreatedAt).TotalMilliseconds:F0}ms";
        if (response is null) return false;
        var result = await ReplyAsync(message.DeliveryId, response, cancellationToken: cancellationToken).ConfigureAwait(false);
        if (result.Status is not "success" and not "cancelled") throw new IOException("内置命令回复未全部成功。");
        return true;
    }

    private async Task ConsumeAsync(string accountId, Func<InboundMessage, CancellationToken, Task> handler, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            InboundMessage? message = null;
            try
            {
                message = await ClaimCoreAsync(accountId, true, cancellationToken).ConfigureAwait(false);
                if (message is null)
                {
                    await DelayAsync(TimeSpan.FromMilliseconds(200), cancellationToken).ConfigureAwait(false);
                    continue;
                }
                using var handling = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                var renewal = RenewLoopAsync(message.DeliveryId, message.LeaseId, handling);
                var started = Stopwatch.GetTimestamp();
                try
                {
                    await WithTypingAsync(message.PeerId, token => handler(message, token), message.AccountId, message.DeliveryId, handling.Token).ConfigureAwait(false);
                    handling.Token.ThrowIfCancellationRequested();
                    if (_store.HasFailedSendInCurrentAttempt(message.DeliveryId)) throw new IOException("本次回复存在失败或未知分段，不能自动确认消息。");
                    if (_store.GetAccount(accountId).Debug)
                        await ReportTimingAsync(message.DeliveryId, "宿主处理", Stopwatch.GetElapsedTime(started), handling.Token).ConfigureAwait(false);
                    _store.Complete(message.DeliveryId, message.LeaseId);
                    DeleteDeliveryMedia(message.DeliveryId);
                    Publish("inbox", accountId, message.DeliveryId, "acked");
                }
                finally
                {
                    try { await DrainDeliveryProgressAsync(message.DeliveryId, CancellationToken.None).ConfigureAwait(false); }
                    catch (Exception e) { Log($"工具进度排空失败: {ProtocolClient.Classify(e)}"); }
                    handling.Cancel();
                    await renewal.ConfigureAwait(false);
                }
            }
            catch (Exception e)
            {
                if (message is not null)
                {
                    try { _store.Retry(message.DeliveryId, message.LeaseId, false, true, ProtocolClient.Classify(e), cancellationToken.IsCancellationRequested); }
                    catch (InvalidOperationException) { Log("handler 租约已失效，拒绝旧持有者更改状态。"); }
                }
                if (cancellationToken.IsCancellationRequested) return;
                Log($"业务处理失败 {accountId}: {ProtocolClient.Classify(e)}，保留消息重试。");
                await DelayAsync(TimeSpan.FromMilliseconds(200), cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private async Task RenewLoopAsync(string delivery, string lease, CancellationTokenSource lifetime)
    {
        try
        {
            while (true)
            {
                await DelayAsync(_options.LeaseRenewInterval, lifetime.Token).ConfigureAwait(false);
                _store.Renew(delivery, lease, _options.LeaseDuration);
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception e) { Log($"自动续租失败: {ProtocolClient.Classify(e)}"); lifetime.Cancel(); }
    }

    public Task AcknowledgeAsync(string deliveryId, string leaseId, CancellationToken cancellationToken = default) => TrackOperationAsync(token =>
    {
        token.ThrowIfCancellationRequested();
        _store.Complete(deliveryId, leaseId);
        DeleteDeliveryMedia(deliveryId);
        Publish("inbox", null, deliveryId, "acked");
        return Task.CompletedTask;
    }, cancellationToken);
    public Task ReportTimingAsync(string deliveryId, string operation, TimeSpan elapsed, CancellationToken cancellationToken = default) =>
        TrackOperationAsync(token => ReportTimingCoreAsync(deliveryId, operation, elapsed, token), cancellationToken);
    private async Task ReportTimingCoreAsync(string deliveryId, string operation, TimeSpan elapsed, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (elapsed < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(elapsed));
        ArgumentException.ThrowIfNullOrWhiteSpace(operation);
        var target = _store.ResolveSend(new SendRequest { DeliveryId = deliveryId });
        try
        {
            EnsureAccountAvailable(target.Account);
            var text = $"⏱ {operation.Trim()}耗时: {elapsed.TotalMilliseconds:F0}ms";
            await SendItemAsync(new SendRequest { RunId = deliveryId }, target.Account, target.PeerId, target.ContextToken,
                new JsonObject { ["type"] = 1, ["text_item"] = new JsonObject { ["text"] = text } }, text, null, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception e) { Log($"耗时上报失败: {ProtocolClient.Classify(e)}（最佳努力通知）"); }
    }
    public Task RetryAsync(string deliveryId, string? leaseId = null, bool allowUnknownSend = false, CancellationToken cancellationToken = default) => TrackOperationAsync(token =>
    {
        token.ThrowIfCancellationRequested();
        _store.Retry(deliveryId, leaseId, allowUnknownSend);
        Publish("inbox", null, deliveryId, "pending");
        return Task.CompletedTask;
    }, cancellationToken);
    public Task<DateTimeOffset> RenewLeaseAsync(string deliveryId, string leaseId, CancellationToken cancellationToken = default) => TrackOperationAsync(token =>
    {
        token.ThrowIfCancellationRequested();
        return Task.FromResult(_store.Renew(deliveryId, leaseId, _options.LeaseDuration));
    }, cancellationToken);

    public async IAsyncEnumerable<WeixinEvent> EventsAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var id = Guid.NewGuid();
        var channel = System.Threading.Channels.Channel.CreateBounded<WeixinEvent>(new BoundedChannelOptions(256) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });
        var pending = ReadState(() => { _subscribers.TryAdd(id, channel); return _store.Inbox(null, false).Count; });
        try
        {
            yield return new WeixinEvent("pending", null, null, pending.ToString(System.Globalization.CultureInfo.InvariantCulture), _options.TimeProvider.GetUtcNow());
            await foreach (var item in channel.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false)) yield return item;
        }
        finally { _subscribers.TryRemove(id, out _); }
    }
    internal void Publish(string kind, string? accountId, string? deliveryId, string? detail)
    {
        var item = new WeixinEvent(kind, accountId, deliveryId, detail, _options.TimeProvider.GetUtcNow());
        foreach (var subscriber in _subscribers.Values) subscriber.Writer.TryWrite(item);
    }
    internal void Log(string message)
    {
        try { _options.Log?.Invoke(message); }
        catch { /* 宿主日志失败不改变消息结果。 */ }
    }
    private Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken) => Task.Delay(delay, _options.TimeProvider, cancellationToken);
}
