namespace Weixin.Channel;

public sealed partial class WeixinChannel
{
    private readonly object _lifetimeGate = new();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, SemaphoreSlim> _accountGates = new();
    private int _activeOperations;
    private bool _closing;
    private TaskCompletionSource? _idle;
    private Task? _disposeTask;

    // 复用 CTS 和 TaskCompletionSource 协调在途操作，不引入新的公共生命周期对象。
    private CancellationTokenSource BeginOperation(CancellationToken cancellationToken)
    {
        lock (_lifetimeGate)
        {
            ObjectDisposedException.ThrowIf(_closing || _disposed, this);
            var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdown.Token);
            _activeOperations++;
            return linked;
        }
    }
    private void EndOperation(CancellationTokenSource operation)
    {
        operation.Dispose();
        lock (_lifetimeGate)
            if (--_activeOperations == 0) _idle?.TrySetResult();
    }
    private async Task<T> TrackOperationAsync<T>(Func<CancellationToken, Task<T>> action, CancellationToken cancellationToken)
    {
        var operation = BeginOperation(cancellationToken);
        try { return await action(operation.Token).ConfigureAwait(false); }
        finally { EndOperation(operation); }
    }
    private Task TrackOperationAsync(Func<CancellationToken, Task> action, CancellationToken cancellationToken) =>
        TrackOperationAsync(async token => { await action(token).ConfigureAwait(false); return true; }, cancellationToken);

    private T ReadState<T>(Func<T> read)
    {
        lock (_lifetimeGate)
        {
            ObjectDisposedException.ThrowIf(_closing || _disposed, this);
            return read();
        }
    }
    private SemaphoreSlim AccountGate(string accountId) => _accountGates.GetOrAdd(accountId, _ => new SemaphoreSlim(1, 1));

    public ValueTask DisposeAsync()
    {
        lock (_lifetimeGate)
        {
            if (_disposeTask is not null) return new ValueTask(_disposeTask);
            _closing = true;
            var drained = _activeOperations == 0 ? Task.CompletedTask : (_idle = new(TaskCreationOptions.RunContinuationsAsynchronously)).Task;
            _disposeTask = DisposeCoreAsync(drained);
            return new ValueTask(_disposeTask);
        }
    }
    private async Task DisposeCoreAsync(Task drained)
    {
        try { await _shutdown.CancelAsync().ConfigureAwait(false); }
        catch (AggregateException) { Log("关闭取消回调失败，仍等待在途操作结束。"); }
        foreach (var subscriber in _subscribers.Values) subscriber.Writer.TryComplete();
        await drained.ConfigureAwait(false);
        _disposed = true;
        DisposeMessaging();
        _store.Dispose();
        _directoryLock.Dispose();
        _shutdown.Dispose();
        _loginCommit.Dispose();
        foreach (var gate in _accountGates.Values) gate.Dispose();
        if (_options.HttpClient is null) _http.Dispose();
    }
}
