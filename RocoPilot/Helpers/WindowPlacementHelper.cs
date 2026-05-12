using System.Runtime.InteropServices;

using WinRT.Interop;

using WinUIEx;

namespace RocoPilot.Helpers;

internal static class WindowPlacementHelper
{
    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    public static void CenterOnParent(WindowEx child, WindowEx parent)
    {
        var parentHwnd = WindowNative.GetWindowHandle(parent);
        if (!GetWindowRect(parentHwnd, out var parentRect))
        {
            return;
        }

        const int defaultWidth = 800;
        const int defaultHeight = 720;
        var width = child.AppWindow.Size.Width;
        var height = child.AppWindow.Size.Height;
        if (width <= 0 || height <= 0)
        {
            width = defaultWidth;
            height = defaultHeight;
        }

        var parentWidth = parentRect.Right - parentRect.Left;
        var parentHeight = parentRect.Bottom - parentRect.Top;
        var x = parentRect.Left + (parentWidth - width) / 2;
        var y = parentRect.Top + (parentHeight - height) / 2;

        child.AppWindow.Move(new Windows.Graphics.PointInt32(x, y));
    }
}
