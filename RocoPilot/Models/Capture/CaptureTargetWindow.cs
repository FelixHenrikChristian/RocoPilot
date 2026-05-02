namespace RocoPilot.Models.Capture;

public sealed class CaptureTargetWindow
{
    public IntPtr Hwnd
    {
        get;
        set;
    }

    public string Title
    {
        get;
        set;
    } = string.Empty;

    public string ProcessName
    {
        get;
        set;
    } = string.Empty;

    public int ProcessId
    {
        get;
        set;
    }

    public int Width
    {
        get;
        set;
    }

    public int Height
    {
        get;
        set;
    }

    public string DisplayName => string.IsNullOrWhiteSpace(ProcessName)
        ? $"{Title} ({Width} x {Height})"
        : $"{Title} - {ProcessName} ({Width} x {Height})";

    public string HandleText => $"0x{Hwnd.ToInt64():X}";

    public override string ToString() => DisplayName;
}
