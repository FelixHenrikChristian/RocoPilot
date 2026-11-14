using RocoPilot.Contracts.Services;
using RocoPilot.Models.Input;

namespace RocoPilot.Services.IndependentTasks;

// Only a fresh approach decision extends the hold. The keyboard service releases W
// in its finally block even when capture or recognition stops responding.
internal sealed class ForwardWalk(IKeyboardInputService keyboard, nint hwnd, KeyboardInputMethod method,
    CancellationToken token) : IAsyncDisposable
{
    private const int FrameDeadlineMs = 3000;
    private CancellationTokenSource? _hold;
    private Task _walking = Task.CompletedTask;

    public bool Active => _hold != null;

    public async Task AdvanceAsync(DateTimeOffset capturedAt)
    {
        var remaining = FrameDeadlineMs - (int)Math.Clamp((DateTimeOffset.UtcNow - capturedAt).TotalMilliseconds, 0, FrameDeadlineMs);
        if (remaining == 0 || !keyboard.IsWindowForeground(hwnd))
        {
            await StopAsync();
            return;
        }
        if (_hold != null && !_hold.IsCancellationRequested && !_walking.IsCompleted)
        {
            _hold.CancelAfter(remaining);
            return;
        }
        await StopAsync();
        token.ThrowIfCancellationRequested();
        _hold = CancellationTokenSource.CreateLinkedTokenSource(token);
        _hold.CancelAfter(remaining);
        _walking = WalkAsync(_hold);
    }

    private async Task WalkAsync(CancellationTokenSource hold)
    {
        var input = keyboard.SendSequenceAsync(hwnd, "W", new KeyboardInputOptions
            { Method = method, HoldDurationMs = Timeout.Infinite, IntervalMs = 0 }, hold.Token);
        try
        {
            while (!hold.IsCancellationRequested && !input.IsCompleted && keyboard.IsWindowForeground(hwnd))
                await Task.WhenAny(input, Task.Delay(60, hold.Token));
        }
        finally
        {
            hold.Cancel();
            try { await input; }
            catch (OperationCanceledException) when (hold.IsCancellationRequested) { }
        }
    }

    public async Task StopAsync()
    {
        if (_hold == null) return;
        _hold.Cancel();
        try { await _walking; }
        finally
        {
            _hold.Dispose();
            _hold = null;
        }
    }

    public async ValueTask DisposeAsync() => await StopAsync();
}
