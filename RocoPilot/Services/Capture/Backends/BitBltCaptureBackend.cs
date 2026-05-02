using System.Runtime.InteropServices;

using RocoPilot.Models.Capture;

namespace RocoPilot.Services.Capture.Backends;

public sealed class BitBltCaptureBackend : GdiWindowCaptureBackendBase
{
    private const int Srccopy = 0x00CC0020;
    private const int Captureblt = 0x40000000;

    public override CaptureMethod Method => CaptureMethod.BitBlt;

    protected override bool RenderWindow(IntPtr hwnd, IntPtr memoryDc, int width, int height)
    {
        var sourceDc = GetTargetWindowDc(hwnd);
        if (sourceDc == IntPtr.Zero)
        {
            return false;
        }

        try
        {
            return BitBlt(memoryDc, 0, 0, width, height, sourceDc, 0, 0, Srccopy | Captureblt);
        }
        finally
        {
            _ = ReleaseTargetWindowDc(hwnd, sourceDc);
        }
    }

    [DllImport("gdi32.dll")]
    private static extern bool BitBlt(
        IntPtr hdc,
        int x,
        int y,
        int width,
        int height,
        IntPtr sourceHdc,
        int sourceX,
        int sourceY,
        int rasterOperation);
}
