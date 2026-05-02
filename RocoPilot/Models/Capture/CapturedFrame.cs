namespace RocoPilot.Models.Capture;

public sealed class CapturedFrame
{
    public CapturedFrame(int width, int height, byte[] pixels)
    {
        Width = width;
        Height = height;
        Pixels = pixels;
        CapturedAt = DateTimeOffset.Now;
    }

    public int Width
    {
        get;
    }

    public int Height
    {
        get;
    }

    public byte[] Pixels
    {
        get;
    }

    public DateTimeOffset CapturedAt
    {
        get;
    }
}
