using System.Runtime.InteropServices;

using WinRT;

using Windows.Graphics.Capture;

namespace RocoPilot.Services.Capture.Backends;

internal static class GraphicsCaptureItemInterop
{
    private static readonly Guid GraphicsCaptureItemGuid = new("79C3F95B-31F7-4EC2-A464-632EF5D30760");

    public static GraphicsCaptureItem CreateForWindow(IntPtr hwnd)
    {
        var interop = GraphicsCaptureItem.As<IGraphicsCaptureItemInterop>();
        var graphicsCaptureItemGuid = GraphicsCaptureItemGuid;
        var item = interop.CreateForWindow(hwnd, ref graphicsCaptureItemGuid);
        if (item == IntPtr.Zero)
        {
            throw new InvalidOperationException("Failed to create a graphics capture item for the target window.");
        }

        try
        {
            return GraphicsCaptureItem.FromAbi(item);
        }
        finally
        {
            Marshal.Release(item);
        }
    }

    [ComImport]
    [Guid("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [ComVisible(true)]
    private interface IGraphicsCaptureItemInterop
    {
        IntPtr CreateForWindow(IntPtr window, [In] ref Guid iid);

        IntPtr CreateForMonitor(IntPtr monitor, [In] ref Guid iid);
    }
}
