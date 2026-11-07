using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

using RocoPilot.Contracts.Services;

using InterceptionInput = InputInterceptorNS.InputInterceptor;
using InterceptionMouseFilter = InputInterceptorNS.MouseFilter;
using InterceptionMouseHook = InputInterceptorNS.MouseHook;
using InterceptionMouseState = InputInterceptorNS.MouseState;

namespace RocoPilot.Services;

public sealed class MouseInputService(IKeyboardInputService keyboardInputService) : IMouseInputService, IDisposable
{
    private const uint MouseEventLeftDown = 0x0002;
    private const uint MouseEventLeftUp = 0x0004;
    private const uint MouseEventWheel = 0x0800;

    private readonly SemaphoreSlim _inputGate = new(1, 1);
    private readonly object _interceptionSyncRoot = new();
    private InterceptionMouseHook? _mouseHook;

    public Task ClickAsync(
        IntPtr hwnd,
        int x,
        int y,
        KeyboardInputMethod method = KeyboardInputMethod.SendInput,
        CancellationToken cancellationToken = default)
        => PressAsync(hwnd, x, y, null, method, cancellationToken);

    public Task DragAsync(IntPtr hwnd, int x, int y, int endX, int endY, KeyboardInputMethod method,
        CancellationToken cancellationToken)
        => PressAsync(hwnd, x, y, new WindowPoint { X = endX, Y = endY }, method, cancellationToken);

    private async Task PressAsync(IntPtr hwnd, int x, int y, WindowPoint? end, KeyboardInputMethod method,
        CancellationToken cancellationToken)
    {
        await _inputGate.WaitAsync(cancellationToken);
        try
        {
            var hook = GetMouseHook(method);
            MoveCursorToClientPoint(hook, hwnd, x, y);
            await Task.Delay(50, cancellationToken);
            EnsureForeground(hwnd);
            SendMouseInput(hook, MouseEventLeftDown, InterceptionMouseState.LeftButtonDown);
            try
            {
                await Task.Delay(50, cancellationToken);
                if (end is { } target)
                {
                    const int steps = 5;
                    for (var i = 1; i <= steps; i++)
                    {
                        MoveCursorToClientPoint(hook, hwnd, x + (target.X - x) * i / steps, y + (target.Y - y) * i / steps);
                        await Task.Delay(80, cancellationToken);
                    }
                }
            }
            finally
            {
                SendMouseInput(hook, MouseEventLeftUp, InterceptionMouseState.LeftButtonUp);
            }
        }
        finally
        {
            _inputGate.Release();
        }
    }

    public Task ScrollAsync(
        IntPtr hwnd,
        int x,
        int y,
        int delta,
        KeyboardInputMethod method = KeyboardInputMethod.SendInput,
        CancellationToken cancellationToken = default)
        => ScrollCoreAsync(hwnd, delta, method, new WindowPoint { X = x, Y = y }, cancellationToken);

    public Task ScrollAtCurrentPositionAsync(
        IntPtr hwnd,
        int delta,
        KeyboardInputMethod method = KeyboardInputMethod.SendInput,
        CancellationToken cancellationToken = default)
        => ScrollCoreAsync(hwnd, delta, method, null, cancellationToken);

    private async Task ScrollCoreAsync(
        IntPtr hwnd,
        int delta,
        KeyboardInputMethod method,
        WindowPoint? point,
        CancellationToken cancellationToken)
    {
        if (delta is < short.MinValue or > short.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(delta));
        }

        await _inputGate.WaitAsync(cancellationToken);
        try
        {
            var hook = GetMouseHook(method);
            if (point is { } clientPoint)
            {
                MoveCursorToClientPoint(hook, hwnd, clientPoint.X, clientPoint.Y);
                // 等游戏完成鼠标悬停更新，避免滚动仍落在上一次的奖励栏。
                await Task.Delay(50, cancellationToken);
            }

            EnsureForeground(hwnd);
            SendMouseInput(hook, MouseEventWheel, InterceptionMouseState.ScrollVertical, (short)delta);
        }
        finally
        {
            _inputGate.Release();
        }
    }

    public void Dispose()
    {
        lock (_interceptionSyncRoot)
        {
            _mouseHook?.Dispose();
            _mouseHook = null;
        }
    }

    private InterceptionMouseHook? GetMouseHook(KeyboardInputMethod method)
    {
        if (method is KeyboardInputMethod.PostMessage or KeyboardInputMethod.SendInput)
        {
            return null;
        }

        if (method != KeyboardInputMethod.Interception)
        {
            throw new InvalidOperationException($"不支持的鼠标输入方式：{method}");
        }

        lock (_interceptionSyncRoot)
        {
            if (_mouseHook?.CanSimulateInput == true)
            {
                return _mouseHook;
            }

            _mouseHook?.Dispose();
            _mouseHook = null;
            if (!InterceptionInput.CheckDriverInstalled())
            {
                throw new InvalidOperationException("Interception 驱动未安装或尚未重启。请先安装驱动并重启电脑。");
            }

            if (InterceptionInput.Disposed && !InterceptionInput.Initialize())
            {
                throw new InvalidOperationException("Interception 初始化失败。请确认安装驱动后已重启电脑。");
            }

            var hook = new InterceptionMouseHook(InterceptionMouseFilter.None);
            if (!hook.CanSimulateInput)
            {
                hook.Dispose();
                throw new InvalidOperationException("Interception 未找到可用鼠标设备。请移动鼠标后重试。");
            }

            _mouseHook = hook;
            return hook;
        }
    }

    private void SendMouseInput(InterceptionMouseHook? hook, uint flags, InterceptionMouseState state, short delta = 0)
    {
        if (hook is not null)
        {
            lock (_interceptionSyncRoot)
            {
                if (!hook.SetMouseState(state, delta))
                {
                    throw new InvalidOperationException("Interception 鼠标输入发送失败。");
                }
            }

            return;
        }

        var inputs = new[]
        {
            new InputEnvelope
            {
                Type = 0,
                Mouse = new MouseInput { Flags = flags, MouseData = unchecked((uint)delta) }
            }
        };
        if (SendInput(1, inputs, Marshal.SizeOf<InputEnvelope>()) != 1)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }
    }

    private void EnsureForeground(IntPtr hwnd)
    {
        if (!keyboardInputService.IsWindowForeground(hwnd))
        {
            throw new InvalidOperationException("目标游戏窗口未处于前台，鼠标输入已取消。");
        }
    }

    private void MoveCursorToClientPoint(InterceptionMouseHook? hook, IntPtr hwnd, int x, int y)
    {
        EnsureForeground(hwnd);
        if (!IsWindowVisible(hwnd) || IsIconic(hwnd))
        {
            throw new InvalidOperationException("目标游戏窗口不可见。");
        }

        // 截图坐标是物理客户区像素；此上下文必须在同一线程同步恢复。
        var previousContext = SetThreadDpiAwarenessContext(new IntPtr(-4));
        try
        {
            if (!GetClientRect(hwnd, out var bounds))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            if (x < 0 || y < 0 || x >= bounds.Right || y >= bounds.Bottom)
            {
                throw new ArgumentOutOfRangeException(nameof(x), "鼠标位置超出游戏客户区。");
            }

            var point = new WindowPoint { X = x, Y = y };
            if (!ClientToScreen(hwnd, ref point))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            if (GetAncestor(WindowFromPoint(point), 2) != GetAncestor(hwnd, 2))
            {
                throw new InvalidOperationException("鼠标目标位置被其他窗口遮挡，请切回游戏后重试。");
            }

            if (hook is null)
            {
                if (!SetCursorPos(point.X, point.Y))
                    throw new InvalidOperationException($"Windows 无法移动鼠标至屏幕 ({point.X}, {point.Y})。请检查游戏与助手的运行权限。");
            }
            else
            {
                // 库内的绝对移动只映射主屏。驱动使用虚拟桌面坐标才能覆盖副屏和负坐标。
                var stroke = new InputInterceptorNS.Stroke();
                stroke.Mouse.Flags = InputInterceptorNS.MouseFlags.MoveAbsolute | InputInterceptorNS.MouseFlags.VirtualDesktop;
                stroke.Mouse.X = AbsoluteCoordinate(point.X, GetSystemMetrics(76), GetSystemMetrics(78));
                stroke.Mouse.Y = AbsoluteCoordinate(point.Y, GetSystemMetrics(77), GetSystemMetrics(79));
                lock (_interceptionSyncRoot)
                {
                    var device = hook.Device == -1 ? hook.RandomDevice : hook.Device;
                    if (InterceptionInput.Send(hook.Context, device, ref stroke, 1) != 1)
                        throw new InvalidOperationException("Interception 鼠标移动发送失败。");
                }
            }

            var timer = Stopwatch.StartNew();
            WindowPoint actual;
            do
            {
                if (!GetCursorPos(out actual)) throw new Win32Exception(Marshal.GetLastWin32Error());
                if (actual.X == point.X && actual.Y == point.Y) return;
                Thread.Sleep(5);
            } while (timer.ElapsedMilliseconds < 150);
            throw new InvalidOperationException($"鼠标未到达目标：屏幕 ({point.X}, {point.Y})，实际 ({actual.X}, {actual.Y})。");
        }
        finally
        {
            if (previousContext != IntPtr.Zero)
            {
                _ = SetThreadDpiAwarenessContext(previousContext);
            }
        }
    }

    private static int AbsoluteCoordinate(int coordinate, int origin, int span)
        => Math.Clamp((int)((coordinate - origin + 0.5) * 65536 / span), 0, 65535);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint count, InputEnvelope[] inputs, int size);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hwnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetClientRect(IntPtr hwnd, out WindowRect rect);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool ClientToScreen(IntPtr hwnd, ref WindowPoint point);

    [DllImport("user32.dll")]
    private static extern IntPtr WindowFromPoint(WindowPoint point);

    [DllImport("user32.dll")]
    private static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetCursorPos(int x, int y);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out WindowPoint point);

    [DllImport("user32.dll")]
    private static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowPoint
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct InputEnvelope
    {
        public uint Type;
        public MouseInput Mouse;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MouseInput
    {
        public int X;
        public int Y;
        public uint MouseData;
        public uint Flags;
        public uint Time;
        public IntPtr ExtraInfo;
    }
}
