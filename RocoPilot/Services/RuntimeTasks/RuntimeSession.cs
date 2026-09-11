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
        try
        {
            _cancellation.Cancel();
            try { await Task.WhenAll(_loops); }
            finally
            {
                Task[] jobs;
                lock (_gate) jobs = _jobs.ToArray();
                await Task.WhenAll(jobs);
            }
        }
        catch (OperationCanceledException) { }
        finally
        {
            ClearFrame();
            capture.Release(State.TargetWindow, State.Options.CaptureMethod);
            _cancellation.Dispose();
        }
    }
}
