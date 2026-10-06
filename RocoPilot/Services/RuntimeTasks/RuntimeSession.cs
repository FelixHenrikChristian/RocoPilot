using System.Runtime.ExceptionServices;
using RocoPilot.Contracts.Services.Capture;
using RocoPilot.Models.Capture;
using RocoPilot.Models.Runtime;

namespace RocoPilot.Services.RuntimeTasks;

/// <summary>一次运行拥有自己的取消源、后台任务和最新帧，停止完成后统一释放截图资源。</summary>
internal sealed class RuntimeSession(RuntimeTaskState state, IScreenCaptureService capture) : IAsyncDisposable
{
    private readonly CancellationTokenSource _cancellation = new();
    private readonly object _gate = new();
    private readonly List<Task> _jobs = [];
    private Task[] _loops = [];
    private Task? _stopTask;
    private CapturedFrame? _latestFrame;
    private long _frameBattleId;

    public RuntimeTaskState State { get; } = state;
    public CancellationToken Token => _cancellation.Token;

    public void Start(Func<RuntimeSession, CancellationToken, Task> captureLoop, Func<RuntimeSession, CancellationToken, Task> ocrLoop)
    {
        var token = Token;
        _loops = [Task.Run(() => captureLoop(this, token)), Task.Run(() => ocrLoop(this, token))];
    }

    public Task<T> RunBackground<T>(Func<Task<T>> work)
    {
        // 不向 Task.Run 传入取消令牌，确保委托中的帧引用释放逻辑一定执行。
        var task = Task.Run(work);
        Track(task);
        return task;
    }

    public void Track(Task task)
    {
        lock (_gate)
        {
            _jobs.RemoveAll(job => job.IsCompletedSuccessfully || job.IsCanceled);
            _jobs.Add(task);
        }
    }

    /// <summary>调用方先阻止新任务产生，等待后台识别结束；保持会话及截图资源可用。</summary>
    public async Task DrainBackgroundAsync(CancellationToken cancellationToken = default)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Task[] jobs;
            lock (_gate) jobs = _jobs.Where(job => !job.IsCompleted).ToArray();
            if (jobs.Length == 0) return;

            try
            {
                await Task.WhenAll(jobs).WaitAsync(cancellationToken);
            }
            catch (Exception) when (!cancellationToken.IsCancellationRequested)
            {
                // 此处只建立完成屏障，识别结果/错误仍交给任务所有者处理。
                // 不删除失败任务，停止会话时仍会观察并传播其错误。
            }
        }
    }

    public void PublishFrame(CapturedFrame frame, long battleId)
    {
        var reference = frame.AddReference();
        lock (_gate)
        {
            _latestFrame?.Dispose();
            _latestFrame = reference;
            _frameBattleId = battleId;
        }
    }

    public CapturedFrame? RentFrame(long battleId)
    {
        lock (_gate) return _frameBattleId == battleId ? _latestFrame?.AddReference() : null;
    }

    public void ClearFrame()
    {
        lock (_gate)
        {
            _latestFrame?.Dispose();
            _latestFrame = null;
        }
    }

    public ValueTask DisposeAsync()
    {
        lock (_gate) return new ValueTask(_stopTask ??= StopCoreAsync());
    }

    private async Task StopCoreAsync()
    {
        var failures = new List<Exception>();
        // 某个取消回调失败也不能跳过等待，否则后台任务可能继续使用已释放的截图后端。
        await ObserveShutdownAsync(_cancellation.CancelAsync(), failures);
        await ObserveShutdownAsync(Task.WhenAll(_loops), failures);
        Task[] jobs;
        lock (_gate) jobs = _jobs.ToArray();
        await ObserveShutdownAsync(Task.WhenAll(jobs), failures);
        try
        {
            try { ClearFrame(); }
            finally { capture.Release(State.TargetWindow, State.Options.CaptureMethod); }
        }
        catch (Exception ex) { failures.Add(ex); }
        finally { _cancellation.Dispose(); }

        if (failures.Count == 1) ExceptionDispatchInfo.Capture(failures[0]).Throw();
        if (failures.Count > 1) throw new AggregateException(failures);
    }

    private static async Task ObserveShutdownAsync(Task task, List<Exception> failures)
    {
        try { await task; }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            failures.Add(ex);
        }
    }
}
