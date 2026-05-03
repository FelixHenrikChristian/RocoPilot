using System.Runtime.InteropServices;

using Windows.Graphics;

namespace RocoPilot.Helpers;

internal static class TransparentOverlayWindowHelper
{
    private static readonly IntPtr HwndTopMost = new(-1);
    private static readonly UIntPtr SubclassId = new(0x52504F4C);

    private const int GwlStyle = -16;
    private const int GwlExStyle = -20;

    private const long BorderlessStyleMask =
        0x00C00000L | // WS_CAPTION
        0x00040000L | // WS_THICKFRAME
        0x00020000L | // WS_MINIMIZEBOX
        0x00010000L | // WS_MAXIMIZEBOX
        0x00080000L;  // WS_SYSMENU

    private const long WsExToolWindow = 0x00000080L;
    private const long WsExTransparent = 0x00000020L;
    private const long WsExNoActivate = 0x08000000L;

    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpFrameChanged = 0x0020;
    private const uint SwpShowWindow = 0x0040;

    private const int WmEraseBackground = 0x0014;
    private const int WmNcHitTest = 0x0084;
    private const int WmDwmCompositionChanged = 0x031E;
    private const int HtTransparent = -1;

    private const int DwmwaWindowCornerPreference = 33;
    private const int DwmwaBorderColor = 34;
    private const uint DwmwcpDoNotRound = 1;
    private const uint DwmwaColorNone = 0xFFFFFFFE;

    public static void ApplyTransparentOverlayStyles(IntPtr hwnd, bool topMost, bool passThrough)
    {
        if (hwnd == IntPtr.Zero)
        {
            return;
        }

        RemoveStandardWindowFrame(hwnd);
        ApplyOverlayExtendedStyle(hwnd, passThrough);
        RefreshWindowFrame(hwnd);
        TryDisableDwmFrame(hwnd);

        if (topMost)
        {
            SetTopMost(hwnd);
        }
    }

    public static IDisposable InstallMessageHook(IntPtr hwnd)
    {
        var hook = new WindowMessageHook(hwnd);
        hook.Attach();
        return hook;
    }

    public static bool TryGetClientScreenBounds(IntPtr hwnd, out RectInt32 bounds)
    {
        bounds = default;

        if (hwnd == IntPtr.Zero || !IsWindowVisible(hwnd) || IsIconic(hwnd))
        {
            return false;
        }

        if (!GetClientRect(hwnd, out var clientRect)
            || clientRect.Width <= 0
            || clientRect.Height <= 0)
        {
            return false;
        }

        var topLeft = new WindowPoint();
        if (!ClientToScreen(hwnd, ref topLeft))
        {
            return false;
        }

        bounds = new RectInt32(
            topLeft.X,
            topLeft.Y,
            clientRect.Width,
            clientRect.Height);
        return true;
    }

    public static void MoveTopMostNoActivate(IntPtr hwnd, RectInt32 bounds)
    {
        if (hwnd == IntPtr.Zero || bounds.Width <= 0 || bounds.Height <= 0)
        {
            return;
        }

        _ = SetWindowPos(
            hwnd,
            HwndTopMost,
            bounds.X,
            bounds.Y,
            bounds.Width,
            bounds.Height,
            SwpNoActivate | SwpShowWindow);
    }

    public static void SetTopMost(IntPtr hwnd)
    {
        _ = SetWindowPos(
            hwnd,
            HwndTopMost,
            0,
            0,
            0,
            0,
            SwpNoMove | SwpNoSize | SwpNoActivate | SwpFrameChanged | SwpShowWindow);
    }

    private static void RemoveStandardWindowFrame(IntPtr hwnd)
    {
        var style = GetWindowLongPtr(hwnd, GwlStyle).ToInt64();
        var updatedStyle = new IntPtr(style & ~BorderlessStyleMask);
        _ = SetWindowLongPtr(hwnd, GwlStyle, updatedStyle);
    }

    private static void ApplyOverlayExtendedStyle(IntPtr hwnd, bool passThrough)
    {
        var exStyle = GetWindowLongPtr(hwnd, GwlExStyle).ToInt64();
        var updatedStyle = exStyle | WsExToolWindow | WsExNoActivate;

        if (passThrough)
        {
            updatedStyle |= WsExTransparent;
        }
        else
        {
            updatedStyle &= ~WsExTransparent;
        }

        _ = SetWindowLongPtr(hwnd, GwlExStyle, new IntPtr(updatedStyle));
    }

    private static void RefreshWindowFrame(IntPtr hwnd)
    {
        _ = SetWindowPos(
            hwnd,
            IntPtr.Zero,
            0,
            0,
            0,
            0,
            SwpNoMove | SwpNoSize | SwpNoZOrder | SwpNoActivate | SwpFrameChanged | SwpShowWindow);
    }

    private static void TryDisableDwmFrame(IntPtr hwnd)
    {
        try
        {
            var cornerPreference = DwmwcpDoNotRound;
            _ = DwmSetWindowAttribute(
                hwnd,
                DwmwaWindowCornerPreference,
                ref cornerPreference,
                Marshal.SizeOf<uint>());

            var borderColor = DwmwaColorNone;
            _ = DwmSetWindowAttribute(
                hwnd,
                DwmwaBorderColor,
                ref borderColor,
                Marshal.SizeOf<uint>());
        }
        catch (DllNotFoundException)
        {
        }
        catch (EntryPointNotFoundException)
        {
        }
    }

    private static IntPtr GetWindowLongPtr(IntPtr hwnd, int index)
    {
        return IntPtr.Size == 8
            ? GetWindowLongPtr64(hwnd, index)
            : new IntPtr(GetWindowLong32(hwnd, index));
    }

    private static IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr value)
    {
        return IntPtr.Size == 8
            ? SetWindowLongPtr64(hwnd, index, value)
            : new IntPtr(SetWindowLong32(hwnd, index, value.ToInt32()));
    }

    private sealed class WindowMessageHook : IDisposable
    {
        private readonly IntPtr _hwnd;
        private readonly SubclassProc _subclassProc;
        private bool _attached;

        public WindowMessageHook(IntPtr hwnd)
        {
            _hwnd = hwnd;
            _subclassProc = WndProc;
        }

        public void Attach()
        {
            if (_hwnd == IntPtr.Zero)
            {
                return;
            }

            _attached = SetWindowSubclass(_hwnd, _subclassProc, SubclassId, UIntPtr.Zero);
        }

        public void Dispose()
        {
            if (!_attached)
            {
                return;
            }

            _ = RemoveWindowSubclass(_hwnd, _subclassProc, SubclassId);
            _attached = false;
        }

        private IntPtr WndProc(
            IntPtr hwnd,
            uint message,
            IntPtr wParam,
            IntPtr lParam,
            UIntPtr subclassId,
            UIntPtr refData)
        {
            if (message == WmEraseBackground)
            {
                return new IntPtr(1);
            }

            if (message == WmNcHitTest)
            {
                return new IntPtr(HtTransparent);
            }

            if (message == WmDwmCompositionChanged)
            {
                TryDisableDwmFrame(hwnd);
            }

            return DefSubclassProc(hwnd, message, wParam, lParam);
        }
    }

    private delegate IntPtr SubclassProc(
        IntPtr hwnd,
        uint message,
        IntPtr wParam,
        IntPtr lParam,
        UIntPtr subclassId,
        UIntPtr refData);

    [DllImport("user32.dll", EntryPoint = "GetWindowLong", SetLastError = true)]
    private static extern int GetWindowLong32(IntPtr hwnd, int index);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtr", SetLastError = true)]
    private static extern IntPtr GetWindowLongPtr64(IntPtr hwnd, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLong", SetLastError = true)]
    private static extern int SetWindowLong32(IntPtr hwnd, int index, int value);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtr", SetLastError = true)]
    private static extern IntPtr SetWindowLongPtr64(IntPtr hwnd, int index, IntPtr value);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(
        IntPtr hwnd,
        IntPtr hwndInsertAfter,
        int x,
        int y,
        int cx,
        int cy,
        uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr hwnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(IntPtr hwnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetClientRect(IntPtr hwnd, out WindowRect rect);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ClientToScreen(IntPtr hwnd, ref WindowPoint point);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref uint value, int size);

    [DllImport("comctl32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowSubclass(
        IntPtr hwnd,
        SubclassProc subclassProc,
        UIntPtr subclassId,
        UIntPtr refData);

    [DllImport("comctl32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RemoveWindowSubclass(
        IntPtr hwnd,
        SubclassProc subclassProc,
        UIntPtr subclassId);

    [DllImport("comctl32.dll")]
    private static extern IntPtr DefSubclassProc(
        IntPtr hwnd,
        uint message,
        IntPtr wParam,
        IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowPoint
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct WindowRect
    {
        public readonly int Left;
        public readonly int Top;
        public readonly int Right;
        public readonly int Bottom;

        public int Width => Right - Left;

        public int Height => Bottom - Top;
    }
}
