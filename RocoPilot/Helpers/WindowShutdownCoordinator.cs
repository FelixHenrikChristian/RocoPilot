namespace RocoPilot.Helpers;

internal sealed class WindowShutdownCoordinator(
    Func<Task> shutdownAsync,
    Func<Action, bool> tryEnqueue,
    Action close,
    Action<Exception> reportError)
{
    private bool _isCloseAllowed;
    public bool IsShutdownStarted { get; private set; }

    public bool HandleCloseRequest()
    {
        if (_isCloseAllowed)
        {
            return false;
        }

        if (!IsShutdownStarted)
        {
            IsShutdownStarted = true;
            _ = ShutdownAndQueueCloseAsync();
        }

        return true;
    }

    private async Task ShutdownAndQueueCloseAsync()
    {
        try
        {
            await shutdownAsync();
        }
        catch (Exception ex)
        {
            ReportError(ex);
        }

        try
        {
            if (!tryEnqueue(AllowAndClose))
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
        close();
    }

    private void ReportError(Exception exception)
    {
        try
        {
            reportError(exception);
        }
        catch
        {
        }
    }
}
