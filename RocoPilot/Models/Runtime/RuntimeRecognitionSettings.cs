namespace RocoPilot.Models.Runtime;

public sealed class RuntimeRecognitionSettings
{
    public const int DefaultFrameCaptureIntervalMs = 100;
    public const int DefaultGameStateScanIntervalMs = 500;
    public const int DefaultOcrScanIntervalMs = 1000;

    public const int MinimumFrameCaptureIntervalMs = 16;
    public const int MinimumGameStateScanIntervalMs = 100;
    public const int MinimumOcrScanIntervalMs = 250;
    public const int MaximumIntervalMs = 30000;

    public int FrameCaptureIntervalMs
    {
        get;
        set;
    } = DefaultFrameCaptureIntervalMs;

    public int GameStateScanIntervalMs
    {
        get;
        set;
    } = DefaultGameStateScanIntervalMs;

    public int OcrScanIntervalMs
    {
        get;
        set;
    } = DefaultOcrScanIntervalMs;

    public static RuntimeRecognitionSettings CreateDefault()
    {
        return new RuntimeRecognitionSettings();
    }

    public RuntimeRecognitionSettings Clone()
    {
        return new RuntimeRecognitionSettings
        {
            FrameCaptureIntervalMs = FrameCaptureIntervalMs,
            GameStateScanIntervalMs = GameStateScanIntervalMs,
            OcrScanIntervalMs = OcrScanIntervalMs
        };
    }
}
