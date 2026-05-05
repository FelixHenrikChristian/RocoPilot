using RocoPilot.Models.Capture;

namespace RocoPilot.Models.Runtime;

public sealed class RuntimeTaskStartOptions
{
    public CaptureMethod CaptureMethod
    {
        get;
        init;
    }

    public bool RecognitionOverlayEnabled
    {
        get;
        init;
    }

    public bool InfoOverlayEnabled
    {
        get;
        init;
    }

    public bool InfoOverlayLocked
    {
        get;
        init;
    }

    public bool PollutionCounterEnabled
    {
        get;
        init;
    } = true;

    public bool AutoBattleEnabled
    {
        get;
        init;
    }
}
