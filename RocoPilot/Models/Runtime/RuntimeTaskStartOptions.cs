using RocoPilot.Models.Capture;
using RocoPilot.Models.TextRecognition;

namespace RocoPilot.Models.Runtime;

public sealed class RuntimeTaskStartOptions
{
    public CaptureMethod CaptureMethod
    {
        get;
        init;
    }

    public TextRecognitionMethod TextRecognitionMethod
    {
        get;
        init;
    } = TextRecognitionMethod.PaddleOcrV5;

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

    public bool EncounterStatisticsEnabled
    {
        get;
        init;
    }

    public AutoBattleSettings AutoBattleSettings
    {
        get;
        init;
    } = AutoBattleSettings.CreateDefault();
}
