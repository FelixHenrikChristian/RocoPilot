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

    public int ExtendedFrameWidth
    {
        get;
        set;
    }

    public int ExtendedFrameHeight
    {
        get;
        set;
    }

    public int ClientWidth
    {
        get;
        set;
    }

    public int ClientHeight
    {
        get;
        set;
    }

    public int ClientOffsetX
    {
        get;
        set;
    }

    public int ClientOffsetY
    {
        get;
        set;
    }

    public int WindowClientOffsetX
    {
        get;
        set;
    }

    public int WindowClientOffsetY
    {
        get;
        set;
    }

    public bool HasClientArea => ClientWidth > 0 && ClientHeight > 0;

    public string DisplayName => string.IsNullOrWhiteSpace(ProcessName)
        ? $"{Title} ({Width} x {Height})"
        : $"{Title} - {ProcessName} ({Width} x {Height})";

    public string HandleText => $"0x{Hwnd.ToInt64():X}";

    public (int X, int Y) GetClientOffsetForFrame(int frameWidth, int frameHeight)
    {
        return frameWidth == Width && frameHeight == Height
            ? (WindowClientOffsetX, WindowClientOffsetY)
            : (ClientOffsetX, ClientOffsetY);
    }

    public override string ToString() => DisplayName;
}
