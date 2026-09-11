namespace RocoPilot.Services.Statistics.Sync;

/// <summary>等待期内合并变更；上传期间的新变更留到下一轮，只有关闭时取消正在上传的任务。</summary>
internal sealed class StatisticsAutoUploadScheduler(
    TimeSpan debounceDelay,
    Func<CancellationToken, Task> upload,
    Action<Exception> reportFailure,
    Func<TimeSpan, CancellationToken, Task> delay) : IAsyncDisposable
{
    private readonly object _gate = new();
    private readonly CancellationTokenSource _shutdown = new();
    private CancellationTokenSource? _delayCancellation;
    private Task? _worker;
    private Task? _disposeTask;
    private bool _pending;
    private bool _stopping;

    public void Request()
    {
        lock (_gate)
        {
            if (_stopping) return;
            _pending = true;
            _delayCancellation?.Cancel();
            _worker ??= Task.Run(RunAsync);
        }
    }

    public void CancelPending()
    {
        lock (_gate)
        {
            _pending = false;
            _delayCancellation?.Cancel();
        }
    }

    private async Task RunAsync()
    {
        try
        {
            while (true)
            {
                CancellationTokenSource waiting;
                lock (_gate)
                {
                    if (_stopping || !_pending) return;
                    _pending = false;
                    waiting = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token);
                    _delayCancellation = waiting;
                }
                try
                {
                    await delay(debounceDelay, waiting.Token);
                    waiting.Token.ThrowIfCancellationRequested();
                }
                catch (OperationCanceledException) when (waiting.IsCancellationRequested) { continue; }
                finally
                {
                    lock (_gate) _delayCancellation = null;
                    waiting.Dispose();
                }

                try { await upload(_shutdown.Token); }
                catch (OperationCanceledException) when (_shutdown.IsCancellationRequested) { return; }
                catch (Exception ex) { reportFailure(ex); }
            }
        }
        finally
        {
            lock (_gate)
            {
                _worker = null;
                if (_pending && !_stopping) _worker = Task.Run(RunAsync);
            }
        }
    }

    public ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            _stopping = true;
            _pending = false;
            return new ValueTask(_disposeTask ??= StopAsync(_worker));
        }
    }

    private async Task StopAsync(Task? worker)
    {
        try
        {
            try { await _shutdown.CancelAsync(); }
            finally { if (worker is not null) await worker; }
        }
        finally { _shutdown.Dispose(); }
    }
}
