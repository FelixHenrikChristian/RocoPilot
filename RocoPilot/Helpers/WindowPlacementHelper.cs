using System.Runtime.InteropServices;

using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;

using WinRT.Interop;

using WinUIEx;

namespace RocoPilot.Helpers;

internal static class WindowPlacementHelper
{
    private const int OwnerWindowIndex = -8;

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hWnd);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr64(IntPtr hWnd, int index, IntPtr newValue);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW")]
    private static extern int SetWindowLong32(IntPtr hWnd, int index, int newValue);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    public static void SetOwner(WindowEx child, WindowEx owner)
    {
        var childHwnd = WindowNative.GetWindowHandle(child);
        var ownerHwnd = WindowNative.GetWindowHandle(owner);
        if (IntPtr.Size == 8)
        {
            _ = SetWindowLongPtr64(childHwnd, OwnerWindowIndex, ownerHwnd);
        }
        else
        {
            _ = SetWindowLong32(childHwnd, OwnerWindowIndex, ownerHwnd.ToInt32());
        }
    }

    public static void ResizeToContent(
        WindowEx window,
        WindowEx dpiSource,
        Windows.Foundation.Size contentSize,
        double minimumWidth,
        double minimumHeight)
    {
        var dpiSourceHwnd = WindowNative.GetWindowHandle(dpiSource);
        var dpi = GetDpiForWindow(dpiSourceHwnd);
        var scale = dpi > 0 ? dpi / 96d : 1d;
        // ResizeClient takes physical pixels and accounts for the native window frame.
        var frameWidth = Math.Max(0, window.AppWindow.Size.Width - window.AppWindow.ClientSize.Width);
        var frameHeight = Math.Max(0, window.AppWindow.Size.Height - window.AppWindow.ClientSize.Height);
        var targetWidth = Math.Max(
            Math.Ceiling(minimumWidth * scale) - frameWidth,
            Math.Ceiling(contentSize.Width * scale));
        var targetHeight = Math.Max(
            Math.Ceiling(minimumHeight * scale) - frameHeight,
            Math.Ceiling(contentSize.Height * scale));

        var workArea = DisplayArea.GetFromWindowId(dpiSource.AppWindow.Id, DisplayAreaFallback.Nearest).WorkArea;
        var workAreaMargin = Math.Ceiling(32 * scale);
        targetWidth = Math.Min(targetWidth, Math.Max(1, workArea.Width - (workAreaMargin * 2) - frameWidth));
        targetHeight = Math.Min(targetHeight, Math.Max(1, workArea.Height - (workAreaMargin * 2) - frameHeight));

        window.AppWindow.ResizeClient(new Windows.Graphics.SizeInt32((int)targetWidth, (int)targetHeight));
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
