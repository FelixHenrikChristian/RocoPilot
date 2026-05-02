using System.Runtime.InteropServices;

using RocoPilot.Models.Capture;

namespace RocoPilot.Services.Capture.Backends;

public sealed class PrintWindowCaptureBackend : GdiWindowCaptureBackendBase
{
    private const uint PrintWindowDefault = 0;
    private const uint PrintWindowRenderFullContent = 2;

    public override CaptureMethod Method => CaptureMethod.PrintWindow;

    protected override bool RenderWindow(IntPtr hwnd, IntPtr memoryDc, int width, int height)
    {
        return PrintWindow(hwnd, memoryDc, PrintWindowRenderFullContent)
            || PrintWindow(hwnd, memoryDc, PrintWindowDefault);
    }

    [DllImport("user32.dll")]
    private static extern bool PrintWindow(IntPtr hwnd, IntPtr hdcBlt, uint flags);
}
