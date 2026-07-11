namespace RocoPilot.Helpers;

internal sealed class WindowShutdownCoordinator
{
    private readonly Func<Task> _shutdownAsync;
    private readonly Func<Action, bool> _tryEnqueue;
    private readonly Action _close;
    private readonly Action<Exception> _reportError;

    private bool _isShutdownStarted;
    private bool _isCloseAllowed;

    public WindowShutdownCoordinator(
        Func<Task> shutdownAsync,
        Func<Action, bool> tryEnqueue,
        Action close,
        Action<Exception> reportError)
    {
        ArgumentNullException.ThrowIfNull(shutdownAsync);
        ArgumentNullException.ThrowIfNull(tryEnqueue);
        ArgumentNullException.ThrowIfNull(close);
        ArgumentNullException.ThrowIfNull(reportError);

        _shutdownAsync = shutdownAsync;
        _tryEnqueue = tryEnqueue;
        _close = close;
        _reportError = reportError;
    }

    public bool IsShutdownStarted => _isShutdownStarted;

    public bool HandleCloseRequest()
    {
        if (_isCloseAllowed)
        {
            return false;
        }

        if (!_isShutdownStarted)
        {
            _isShutdownStarted = true;
            _ = ShutdownAndQueueCloseAsync();
        }

        return true;
    }

    private async Task ShutdownAndQueueCloseAsync()
    {
        try
        {
            await _shutdownAsync();
        }
        catch (Exception ex)
        {
            ReportError(ex);
        }

        try
        {
            if (!_tryEnqueue(AllowAndClose))
            {
                ReportError(new InvalidOperationException("无法将最终关闭操作加入 UI 调度队列。"));
            }
        }
        catch (Exception ex)
        {
            ReportError(ex);
        }
    }

    private void AllowAndClose()
    {
        _isCloseAllowed = true;
        _close();
    }

    private void ReportError(Exception exception)
    {
        try
        {
            _reportError(exception);
        }
        catch
        {
        }
    }
}
