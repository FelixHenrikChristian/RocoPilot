using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

using RocoPilot.Contracts.Services.Capture;
using RocoPilot.Models.Capture;

namespace RocoPilot.Services.Capture;

public sealed class WindowEnumerationService : IWindowEnumerationService
{
    private const int DwmwaExtendedFrameBounds = 9;
    private const int DwmwaCloaked = 14;

    public IReadOnlyList<CaptureTargetWindow> GetVisibleWindows()
    {
        var windows = new List<CaptureTargetWindow>();

        EnumWindows((hwnd, _) =>
        {
            if (!TryCreateWindowInfo(hwnd, out var window))
            {
                return true;
            }

            windows.Add(window);
            return true;
        }, IntPtr.Zero);

        return windows
            .OrderBy(window => window.Title, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    private static bool TryCreateWindowInfo(IntPtr hwnd, out CaptureTargetWindow window)
    {
        window = default!;

        if (hwnd == IntPtr.Zero || !IsWindowVisible(hwnd) || IsIconic(hwnd) || IsCloaked(hwnd))
        {
            return false;
        }

        var title = GetWindowTitle(hwnd);
        if (string.IsNullOrWhiteSpace(title))
        {
            return false;
        }

        if (!GetWindowRect(hwnd, out var rect) || rect.Width <= 0 || rect.Height <= 0)
        {
            return false;
        }

        _ = GetWindowThreadProcessId(hwnd, out var processId);

        var clientInfo = GetClientAreaInfo(hwnd, rect);

        window = new CaptureTargetWindow
        {
            Hwnd = hwnd,
            Title = title,
            ProcessId = (int)processId,
            ProcessName = GetProcessName(processId),
            Width = rect.Width,
            Height = rect.Height,
            ExtendedFrameWidth = clientInfo.ExtendedFrameWidth,
            ExtendedFrameHeight = clientInfo.ExtendedFrameHeight,
            ClientWidth = clientInfo.Width,
            ClientHeight = clientInfo.Height,
            ClientOffsetX = clientInfo.OffsetX,
            ClientOffsetY = clientInfo.OffsetY,
            WindowClientOffsetX = clientInfo.WindowOffsetX,
            WindowClientOffsetY = clientInfo.WindowOffsetY
        };

        return true;
    }

    private static string GetWindowTitle(IntPtr hwnd)
    {
        var length = GetWindowTextLength(hwnd);
        if (length <= 0)
        {
            return string.Empty;
        }

        var builder = new StringBuilder(length + 1);
        _ = GetWindowText(hwnd, builder, builder.Capacity);
        return builder.ToString().Trim();
    }

    private static string GetProcessName(uint processId)
    {
        try
        {
            using var process = Process.GetProcessById((int)processId);
            return process.ProcessName;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static bool IsCloaked(IntPtr hwnd)
    {
        try
        {
            var result = DwmGetWindowAttribute(hwnd, DwmwaCloaked, out var cloaked, Marshal.SizeOf<int>());
            return result == 0 && cloaked != 0;
        }
        catch (DllNotFoundException)
        {
            return false;
        }
        catch (EntryPointNotFoundException)
        {
            return false;
        }
    }

    private static ClientAreaInfo GetClientAreaInfo(IntPtr hwnd, WindowRect windowRect)
    {
        if (!GetClientRect(hwnd, out var clientRect)
            || clientRect.Width <= 0
            || clientRect.Height <= 0)
        {
            return new ClientAreaInfo();
        }

        var clientTopLeft = new WindowPoint();
        if (!ClientToScreen(hwnd, ref clientTopLeft))
        {
            return new ClientAreaInfo(
                clientRect.Width,
                clientRect.Height,
                windowRect.Width,
                windowRect.Height,
                0,
                0,
                0,
                0);
        }

        var captureBounds = TryGetExtendedFrameBounds(hwnd, out var extendedFrameBounds)
            ? extendedFrameBounds
            : windowRect;

        return new ClientAreaInfo(
            clientRect.Width,
            clientRect.Height,
            captureBounds.Width,
            captureBounds.Height,
            clientTopLeft.X - captureBounds.Left,
            clientTopLeft.Y - captureBounds.Top,
            clientTopLeft.X - windowRect.Left,
            clientTopLeft.Y - windowRect.Top);
    }

    private static bool TryGetExtendedFrameBounds(IntPtr hwnd, out WindowRect bounds)
    {
        try
        {
            var result = DwmGetWindowAttributeRect(
                hwnd,
                DwmwaExtendedFrameBounds,
                out bounds,
                Marshal.SizeOf<WindowRect>());

            return result == 0 && bounds.Width > 0 && bounds.Height > 0;
        }
        catch (DllNotFoundException)
        {
            bounds = default;
            return false;
        }
        catch (EntryPointNotFoundException)
        {
            bounds = default;
            return false;
        }
    }

    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc enumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hwnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hwnd, StringBuilder text, int maxCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextLength(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hwnd, out WindowRect rect);

    [DllImport("user32.dll")]
    private static extern bool GetClientRect(IntPtr hwnd, out WindowRect rect);

    [DllImport("user32.dll")]
    private static extern bool ClientToScreen(IntPtr hwnd, ref WindowPoint point);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out int value, int size);

    [DllImport("dwmapi.dll", EntryPoint = "DwmGetWindowAttribute")]
    private static extern int DwmGetWindowAttributeRect(IntPtr hwnd, int attribute, out WindowRect value, int size);

    private readonly struct ClientAreaInfo
    {
        public ClientAreaInfo(
            int width,
            int height,
            int extendedFrameWidth,
            int extendedFrameHeight,
            int offsetX,
            int offsetY,
            int windowOffsetX,
            int windowOffsetY)
        {
            Width = width;
            Height = height;
            ExtendedFrameWidth = extendedFrameWidth;
            ExtendedFrameHeight = extendedFrameHeight;
            OffsetX = offsetX;
            OffsetY = offsetY;
            WindowOffsetX = windowOffsetX;
            WindowOffsetY = windowOffsetY;
        }

        public int Width
        {
            get;
        }

        public int Height
        {
            get;
        }

        public int ExtendedFrameWidth
        {
            get;
        }

        public int ExtendedFrameHeight
        {
            get;
        }

        public int OffsetX
        {
            get;
        }

        public int OffsetY
        {
            get;
        }

        public int WindowOffsetX
        {
            get;
        }

        public int WindowOffsetY
        {
            get;
        }
    }

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
