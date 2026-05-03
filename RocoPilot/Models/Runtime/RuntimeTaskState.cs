using RocoPilot.Models.Capture;

namespace RocoPilot.Models.Runtime;

public sealed class RuntimeTaskState
{
    public RuntimeTaskState(
        CaptureTargetWindow targetWindow,
        RuntimeTaskStartOptions options,
        DateTimeOffset startedAt)
    {
        TargetWindow = targetWindow;
        Options = options;
        StartedAt = startedAt;
    }

    public CaptureTargetWindow TargetWindow
    {
        get;
    }

    public RuntimeTaskStartOptions Options
    {
        get;
    }

    public DateTimeOffset StartedAt
    {
        get;
    }
}
