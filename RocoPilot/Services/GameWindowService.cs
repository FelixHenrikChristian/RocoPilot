using System.Runtime.InteropServices;

using RocoPilot.Contracts.Services;
using RocoPilot.Contracts.Services.Capture;
using RocoPilot.Models.Capture;

namespace RocoPilot.Services;

public sealed class GameWindowService : IGameWindowService
{
    private const int SwRestore = 9;
    private const int SwShow = 5;
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpShowWindow = 0x0040;
    private static readonly IntPtr HwndTop = IntPtr.Zero;

    private const string TargetProcessNameWithoutExtension = "NRC-Win64-Shipping";

    private readonly IWindowEnumerationService _windowEnumerationService;

    public string TargetProcessName => $"{TargetProcessNameWithoutExtension}.exe";

    public GameWindowService(IWindowEnumerationService windowEnumerationService)
    {
        _windowEnumerationService = windowEnumerationService;
    }

    public CaptureTargetWindow? FindGameWindow()
    {
        return _windowEnumerationService
            .GetVisibleWindows()
            .Where(IsTargetProcessWindow)
            .OrderByDescending(window => window.Width * window.Height)
            .FirstOrDefault();
    }

    public bool TryBringGameWindowToForeground(CaptureTargetWindow window)
    {
        var hwnd = window.Hwnd;
        if (hwnd == IntPtr.Zero || !IsWindow(hwnd))
        {
            return false;
        }

        _ = ShowWindow(hwnd, IsIconic(hwnd) ? SwRestore : SwShow);
        _ = SetWindowPos(
            hwnd,
            HwndTop,
            0,
            0,
            0,
            0,
            SwpNoMove | SwpNoSize | SwpShowWindow);
        _ = BringWindowToTop(hwnd);
        _ = SetForegroundWindow(hwnd);

        return IsForegroundWindowForSameProcess(hwnd);
    }

    private static bool IsTargetProcessWindow(CaptureTargetWindow window)
    {
        if (string.IsNullOrWhiteSpace(window.ProcessName))
        {
            return false;
        }

        var processNameWithoutExtension = Path.GetFileNameWithoutExtension(window.ProcessName);
        return string.Equals(
            processNameWithoutExtension,
            TargetProcessNameWithoutExtension,
            StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsForegroundWindowForSameProcess(IntPtr hwnd)
    {
        var foregroundWindow = GetForegroundWindow();
        if (foregroundWindow == IntPtr.Zero)
        {
            return false;
        }

        if (foregroundWindow == hwnd)
        {
            return true;
        }

        _ = GetWindowThreadProcessId(hwnd, out var targetProcessId);
        if (targetProcessId == 0)
        {
            return false;
        }

        _ = GetWindowThreadProcessId(foregroundWindow, out var foregroundProcessId);
        return foregroundProcessId != 0 && foregroundProcessId == targetProcessId;
    }

    [DllImport("user32.dll")]
    private static extern bool IsWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hwnd, int command);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(
        IntPtr hwnd,
        IntPtr hwndInsertAfter,
        int x,
        int y,
        int cx,
        int cy,
        uint flags);

    [DllImport("user32.dll")]
    private static extern bool BringWindowToTop(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);
}
