using System.Threading.Channels;

namespace RocoPilot.Tests.TestDoubles;

internal sealed class ManualAsyncDelay
{
    private readonly Channel<PendingDelay> _requests = Channel.CreateUnbounded<PendingDelay>();
    public async Task DelayAsync(TimeSpan duration, CancellationToken cancellationToken)
    {
        var pending = new PendingDelay();
        using var registration = cancellationToken.Register(() => pending.Completion.TrySetCanceled(cancellationToken));
        _requests.Writer.TryWrite(pending);
        await pending.Completion.Task;
    }
    public Task<PendingDelay> NextAsync() => _requests.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
    public bool HasPending => _requests.Reader.TryPeek(out _);
    internal sealed class PendingDelay
    {
        public TaskCompletionSource Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Complete() => Completion.TrySetResult();
    }
}
