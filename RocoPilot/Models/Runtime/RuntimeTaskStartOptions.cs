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
        set;
    }

    public bool InfoOverlayEnabled
    {
        get;
        set;
    }

    public bool InfoOverlayLocked
    {
        get;
        set;
    }

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
