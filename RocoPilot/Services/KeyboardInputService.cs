using System.ComponentModel;
using System.Runtime.InteropServices;

using RocoPilot.Contracts.Services;
using RocoPilot.Models.Input;

using InterceptionInput = InputInterceptorNS.InputInterceptor;
using InterceptionKeyboardFilter = InputInterceptorNS.KeyboardFilter;
using InterceptionKeyboardHook = InputInterceptorNS.KeyboardHook;
using InterceptionKeyCode = InputInterceptorNS.KeyCode;
using InterceptionKeyState = InputInterceptorNS.KeyState;

namespace RocoPilot.Services;

public sealed class KeyboardInputService : IKeyboardInputService, IDisposable
{
    private const uint InputKeyboard = 1;
    private const uint WmKeyDown = 0x0100;
    private const uint WmKeyUp = 0x0101;
    private const uint WmSysKeyDown = 0x0104;
    private const uint WmSysKeyUp = 0x0105;
    private const uint MapVkToVsc = 0;
    private const uint KeyEventFExtendedKey = 0x0001;
    private const uint KeyEventFKeyUp = 0x0002;
    private const uint KeyEventFScanCode = 0x0008;

    private readonly object _interceptionSyncRoot = new();
    private InterceptionKeyboardHook? _interceptionKeyboardHook;

    public bool IsWindowAvailable(IntPtr hwnd)
    {
        return hwnd != IntPtr.Zero && IsWindow(hwnd);
    }

    public bool IsWindowForeground(IntPtr hwnd)
    {
        if (!IsWindowAvailable(hwnd))
        {
            return false;
        }

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

    public bool RequiresForeground(KeyboardInputMethod method)
    {
        return method switch
        {
            KeyboardInputMethod.PostMessage => false,
            KeyboardInputMethod.SendInput => true,
            KeyboardInputMethod.Interception => true,
            _ => throw new InvalidOperationException($"不支持的键盘输入方式：{method}")
        };
    }

    public void Dispose()
    {
        lock (_interceptionSyncRoot)
        {
            _interceptionKeyboardHook?.Dispose();
            _interceptionKeyboardHook = null;

            if (!InterceptionInput.Disposed)
            {
                InterceptionInput.Dispose();
            }
        }
    }

    public bool TryParseSequence(
        string sequence,
        out IReadOnlyList<KeyStroke> keyStrokes,
        out string error)
    {
        keyStrokes = [];
        error = string.Empty;

        if (string.IsNullOrWhiteSpace(sequence))
        {
            return true;
        }

        var tokens = SplitSequenceTokens(sequence).ToArray();
        var parsedStrokes = new List<KeyStroke>(tokens.Length);

        foreach (var token in tokens)
        {
            if (!TryParseKeyStroke(token, out var keyStroke, out error))
            {
                keyStrokes = [];
                return false;
            }

            parsedStrokes.Add(keyStroke);
        }

        keyStrokes = parsedStrokes;
        return true;
    }

    public Task SendSequenceAsync(
        IntPtr hwnd,
        string sequence,
        KeyboardInputOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        if (!TryParseSequence(sequence, out var keyStrokes, out var error))
        {
            throw new ArgumentException(error, nameof(sequence));
        }

        return SendSequenceAsync(hwnd, keyStrokes, options, cancellationToken);
    }

    public async Task SendSequenceAsync(
        IntPtr hwnd,
        IReadOnlyList<KeyStroke> keyStrokes,
        KeyboardInputOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        if (!IsWindowAvailable(hwnd))
        {
            throw new InvalidOperationException("目标游戏窗口句柄已失效。");
        }

        if (keyStrokes.Count == 0)
        {
            return;
        }

        var normalizedOptions = options ?? new KeyboardInputOptions();
        if (!Enum.IsDefined(normalizedOptions.Method))
        {
            throw new InvalidOperationException($"不支持的键盘输入方式：{normalizedOptions.Method}");
        }

        var requiresForeground = RequiresForeground(normalizedOptions.Method);

        foreach (var keyStroke in keyStrokes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (requiresForeground)
            {
                EnsureTargetWindowForeground(hwnd, normalizedOptions.Method);
            }

            await SendKeyStrokeAsync(hwnd, keyStroke, normalizedOptions, cancellationToken);

            if (normalizedOptions.IntervalMs > 0 && !ReferenceEquals(keyStroke, keyStrokes[^1]))
            {
                await Task.Delay(normalizedOptions.IntervalMs, cancellationToken);
            }
        }
    }

    private static IEnumerable<string> SplitSequenceTokens(string sequence)
    {
        var normalized = sequence
            .Replace("\r", "\n", StringComparison.Ordinal)
            .Replace(';', ',');

        foreach (var group in normalized.Split([',', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (group.Contains('+', StringComparison.Ordinal))
            {
                yield return group;
                continue;
            }

            foreach (var token in group.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                yield return token;
            }
        }
    }

    private static bool TryParseKeyStroke(
        string token,
        out KeyStroke keyStroke,
        out string error)
    {
        keyStroke = null!;
        error = string.Empty;

        var parts = token.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0)
        {
            error = "按键列表中存在空按键。";
            return false;
        }

        var modifiers = new List<KeyDefinition>();
        for (var index = 0; index < parts.Length; index++)
        {
            if (!KeyCatalog.TryGetDefinitionByName(parts[index], out var keyDefinition))
            {
                error = $"不支持的按键：{parts[index]}";
                return false;
            }

            var isLast = index == parts.Length - 1;
            if (!isLast && !keyDefinition.IsModifier)
            {
                error = $"组合键前缀必须是 Ctrl、Alt 或 Shift：{parts[index]}";
                return false;
            }

            if (isLast)
            {
                keyStroke = new KeyStroke(modifiers, keyDefinition);
                return true;
            }

            modifiers.Add(keyDefinition);
        }

        error = $"无效按键：{token}";
        return false;
    }

    private async Task SendKeyStrokeAsync(
        IntPtr hwnd,
        KeyStroke keyStroke,
        KeyboardInputOptions options,
        CancellationToken cancellationToken)
    {
        var pressedKeys = new List<KeyDefinition>();
        var useSystemMessage = keyStroke.Modifiers.Any(modifier => modifier.VirtualKey == 0x12)
            || keyStroke.Key.VirtualKey == 0x12;

        try
        {
            foreach (var modifier in keyStroke.Modifiers)
            {
                SendKeyboardMessage(
                    hwnd,
                    options.Method,
                    modifier.VirtualKey == 0x12 ? WmSysKeyDown : WmKeyDown,
                    modifier,
                    isKeyUp: false);
                pressedKeys.Add(modifier);
            }

            SendKeyboardMessage(
                hwnd,
                options.Method,
                useSystemMessage ? WmSysKeyDown : WmKeyDown,
                keyStroke.Key,
                isKeyUp: false);
            pressedKeys.Add(keyStroke.Key);

            await Task.Delay(options.HoldDurationMs, cancellationToken);
        }
        finally
        {
            for (var index = pressedKeys.Count - 1; index >= 0; index--)
            {
                var key = pressedKeys[index];
                var isSystemKey = useSystemMessage || key.VirtualKey == 0x12;
                SendKeyboardMessage(
                    hwnd,
                    options.Method,
                    isSystemKey ? WmSysKeyUp : WmKeyUp,
                    key,
                    isKeyUp: true);
            }
        }
    }

    private void EnsureTargetWindowForeground(IntPtr hwnd, KeyboardInputMethod method)
    {
        if (IsWindowForeground(hwnd))
        {
            return;
        }

        throw new InvalidOperationException($"目标游戏窗口未处于前台，{method} 已取消。");
    }

    private void SendKeyboardMessage(
        IntPtr hwnd,
        KeyboardInputMethod method,
        uint postMessage,
        KeyDefinition key,
        bool isKeyUp)
    {
        switch (method)
        {
            case KeyboardInputMethod.SendInput:
                SendKeyboardInput(key, isKeyUp);
                break;
            case KeyboardInputMethod.PostMessage:
                PostKeyboardMessage(hwnd, postMessage, key, isKeyUp);
                break;
            case KeyboardInputMethod.Interception:
                SendInterceptionKeyboardInput(key, isKeyUp);
                break;
            default:
                throw new InvalidOperationException($"不支持的键盘输入方式：{method}");
        }
    }

    private void SendInterceptionKeyboardInput(KeyDefinition key, bool isKeyUp)
    {
        var keyboardHook = GetInterceptionKeyboardHook();
        var keyCode = GetInterceptionKeyCode(key);
        var keyState = isKeyUp ? InterceptionKeyState.Up : InterceptionKeyState.Down;
        if (key.IsExtended)
        {
            keyState |= InterceptionKeyState.E0;
        }

        bool isSent;
        lock (_interceptionSyncRoot)
        {
            isSent = keyboardHook.SetKeyState(keyCode, keyState);
        }

        if (!isSent)
        {
            throw new InvalidOperationException($"Interception 按键发送失败：{key.DisplayName}。");
        }
    }

    private InterceptionKeyboardHook GetInterceptionKeyboardHook()
    {
        lock (_interceptionSyncRoot)
        {
            if (_interceptionKeyboardHook?.CanSimulateInput == true)
            {
                return _interceptionKeyboardHook;
            }

            _interceptionKeyboardHook?.Dispose();
            _interceptionKeyboardHook = null;

            if (!InterceptionInput.CheckDriverInstalled())
            {
                throw new InvalidOperationException("Interception 驱动未安装或尚未重启。请先安装 Interception 驱动并重启电脑。");
            }

            if (InterceptionInput.Disposed && !InterceptionInput.Initialize())
            {
                throw new InvalidOperationException("Interception 初始化失败。请确认驱动已安装，并以管理员身份启动 RocoPilot。");
            }

            var keyboardHook = new InterceptionKeyboardHook(InterceptionKeyboardFilter.None);
            if (!keyboardHook.CanSimulateInput)
            {
                keyboardHook.Dispose();
                throw new InvalidOperationException("Interception 未找到可用键盘设备。请确认驱动已安装，并在键盘上按任意键后重试。");
            }

            _interceptionKeyboardHook = keyboardHook;
            return keyboardHook;
        }
    }

    private static InterceptionKeyCode GetInterceptionKeyCode(KeyDefinition key)
    {
        return key.VirtualKey switch
        {
            0x41 => InterceptionKeyCode.A,
            0x42 => InterceptionKeyCode.B,
            0x43 => InterceptionKeyCode.C,
            0x44 => InterceptionKeyCode.D,
            0x45 => InterceptionKeyCode.E,
            0x46 => InterceptionKeyCode.F,
            0x47 => InterceptionKeyCode.G,
            0x48 => InterceptionKeyCode.H,
            0x49 => InterceptionKeyCode.I,
            0x4A => InterceptionKeyCode.J,
            0x4B => InterceptionKeyCode.K,
            0x4C => InterceptionKeyCode.L,
            0x4D => InterceptionKeyCode.M,
            0x4E => InterceptionKeyCode.N,
            0x4F => InterceptionKeyCode.O,
            0x50 => InterceptionKeyCode.P,
            0x51 => InterceptionKeyCode.Q,
            0x52 => InterceptionKeyCode.R,
            0x53 => InterceptionKeyCode.S,
            0x54 => InterceptionKeyCode.T,
            0x55 => InterceptionKeyCode.U,
            0x56 => InterceptionKeyCode.V,
            0x57 => InterceptionKeyCode.W,
            0x58 => InterceptionKeyCode.X,
            0x59 => InterceptionKeyCode.Y,
            0x5A => InterceptionKeyCode.Z,
            0x30 => InterceptionKeyCode.Zero,
            0x31 => InterceptionKeyCode.One,
            0x32 => InterceptionKeyCode.Two,
            0x33 => InterceptionKeyCode.Three,
            0x34 => InterceptionKeyCode.Four,
            0x35 => InterceptionKeyCode.Five,
            0x36 => InterceptionKeyCode.Six,
            0x37 => InterceptionKeyCode.Seven,
            0x38 => InterceptionKeyCode.Eight,
            0x39 => InterceptionKeyCode.Nine,
            0x60 => InterceptionKeyCode.Numpad0,
            0x61 => InterceptionKeyCode.Numpad1,
            0x62 => InterceptionKeyCode.Numpad2,
            0x63 => InterceptionKeyCode.Numpad3,
            0x64 => InterceptionKeyCode.Numpad4,
            0x65 => InterceptionKeyCode.Numpad5,
            0x66 => InterceptionKeyCode.Numpad6,
            0x67 => InterceptionKeyCode.Numpad7,
            0x68 => InterceptionKeyCode.Numpad8,
            0x69 => InterceptionKeyCode.Numpad9,
            >= 0x70 and <= 0x7B => (InterceptionKeyCode)(key.VirtualKey - 0x70 + (int)InterceptionKeyCode.F1),
            0x08 => InterceptionKeyCode.Backspace,
            0x09 => InterceptionKeyCode.Tab,
            0x0D => InterceptionKeyCode.Enter,
            0x10 => InterceptionKeyCode.LeftShift,
            0x11 => InterceptionKeyCode.Control,
            0x12 => InterceptionKeyCode.Alt,
            0x14 => InterceptionKeyCode.CapsLock,
            0x1B => InterceptionKeyCode.Escape,
            0x20 => InterceptionKeyCode.Space,
            0x21 => InterceptionKeyCode.PageUp,
            0x22 => InterceptionKeyCode.PageDown,
            0x23 => InterceptionKeyCode.End,
            0x24 => InterceptionKeyCode.Home,
            0x25 => InterceptionKeyCode.Left,
            0x26 => InterceptionKeyCode.Up,
            0x27 => InterceptionKeyCode.Right,
            0x28 => InterceptionKeyCode.Down,
            0x2D => InterceptionKeyCode.Insert,
            0x2E => InterceptionKeyCode.Delete,
            0x5B => InterceptionKeyCode.LeftWindowsKey,
            0x5C => InterceptionKeyCode.RightWindowsKey,
            0x6A => InterceptionKeyCode.NumpadAsterisk,
            0x6B => InterceptionKeyCode.NumpadPlus,
            0x6D => InterceptionKeyCode.NumpadMinus,
            0x6E => InterceptionKeyCode.NumpadDelete,
            0x6F => InterceptionKeyCode.NumpadDivide,
            _ => throw new InvalidOperationException($"Interception 暂不支持按键：{key.DisplayName}。")
        };
    }

    private static void SendKeyboardInput(KeyDefinition key, bool isKeyUp)
    {
        var scanCode = (ushort)(MapVirtualKey((uint)key.VirtualKey, MapVkToVsc) & 0xFF);
        if (scanCode == 0)
        {
            throw new InvalidOperationException($"无法解析按键扫描码：{key.DisplayName}");
        }

        var flags = KeyEventFScanCode;
        if (key.IsExtended)
        {
            flags |= KeyEventFExtendedKey;
        }

        if (isKeyUp)
        {
            flags |= KeyEventFKeyUp;
        }

        var inputs = new[]
        {
            new KeyboardInputEnvelope
            {
                Type = InputKeyboard,
                Data = new KeyboardInputUnion
                {
                    Keyboard = new KeyboardInput
                    {
                        ScanCode = scanCode,
                        Flags = flags
                    }
                }
            }
        };

        var sent = SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<KeyboardInputEnvelope>());
        if (sent != (uint)inputs.Length)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }
    }

    private static void PostKeyboardMessage(
        IntPtr hwnd,
        uint message,
        KeyDefinition key,
        bool isKeyUp)
    {
        var lParam = BuildKeyboardLParam(key, isKeyUp);
        if (!PostMessage(hwnd, message, new IntPtr(key.VirtualKey), new IntPtr(lParam)))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }
    }

    private static int BuildKeyboardLParam(KeyDefinition key, bool isKeyUp)
    {
        var scanCode = (int)(MapVirtualKey((uint)key.VirtualKey, MapVkToVsc) & 0xFF);
        var lParam = 1 | (scanCode << 16);
        if (key.IsExtended)
        {
            lParam |= 1 << 24;
        }

        if (isKeyUp)
        {
            lParam |= 1 << 30;
            lParam |= 1 << 31;
        }

        return lParam;
    }

    [DllImport("user32.dll", EntryPoint = "PostMessageW", SetLastError = true)]
    private static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", EntryPoint = "MapVirtualKeyW")]
    private static extern uint MapVirtualKey(uint uCode, uint uMapType);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint numberOfInputs, KeyboardInputEnvelope[] inputs, int sizeOfInputStructure);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("user32.dll")]
    private static extern bool IsWindow(IntPtr hWnd);

    [StructLayout(LayoutKind.Sequential)]
    private struct KeyboardInputEnvelope
    {
        public uint Type;
        public KeyboardInputUnion Data;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct KeyboardInputUnion
    {
        [FieldOffset(0)]
        public MouseInput Mouse;

        [FieldOffset(0)]
        public KeyboardInput Keyboard;

        [FieldOffset(0)]
        public HardwareInput Hardware;
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

    [StructLayout(LayoutKind.Sequential)]
    private struct KeyboardInput
    {
        public ushort VirtualKey;
        public ushort ScanCode;
        public uint Flags;
        public uint Time;
        public IntPtr ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct HardwareInput
    {
        public uint Message;
        public ushort ParameterLow;
        public ushort ParameterHigh;
    }
}
