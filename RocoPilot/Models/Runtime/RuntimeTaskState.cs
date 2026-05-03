using RocoPilot.Models.Capture;
using RocoPilot.Models.Recognition;

namespace RocoPilot.Models.Runtime;

public sealed class RuntimeTaskState
{
    public RuntimeTaskState(
        CaptureTargetWindow targetWindow,
        RecognitionRegionConfig recognitionRegionConfig,
        RuntimeTaskStartOptions options,
        DateTimeOffset startedAt)
    {
        TargetWindow = targetWindow;
        RecognitionRegionConfig = recognitionRegionConfig;
        Options = options;
        StartedAt = startedAt;
    }

    public CaptureTargetWindow TargetWindow
    {
        get;
    }

    public RecognitionRegionConfig RecognitionRegionConfig
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
