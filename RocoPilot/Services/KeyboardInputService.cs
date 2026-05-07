using System.ComponentModel;
using System.Runtime.InteropServices;

using RocoPilot.Contracts.Services;
using RocoPilot.Models.Input;

namespace RocoPilot.Services;

public sealed class KeyboardInputService : IKeyboardInputService
{
    private const uint WmKeyDown = 0x0100;
    private const uint WmKeyUp = 0x0101;
    private const uint WmSysKeyDown = 0x0104;
    private const uint WmSysKeyUp = 0x0105;
    private const uint MapVkToVsc = 0;

    private static readonly IReadOnlyDictionary<string, KeyDefinition> KeyDefinitions = BuildKeyDefinitions();

    public bool IsWindowAvailable(IntPtr hwnd)
    {
        return hwnd != IntPtr.Zero && IsWindow(hwnd);
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
        foreach (var keyStroke in keyStrokes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await SendKeyStrokeAsync(hwnd, keyStroke, normalizedOptions.HoldDurationMs, cancellationToken);

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
            var keyName = NormalizeKeyName(parts[index]);
            if (!KeyDefinitions.TryGetValue(keyName, out var keyDefinition))
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

    private static async Task SendKeyStrokeAsync(
        IntPtr hwnd,
        KeyStroke keyStroke,
        int holdDurationMs,
        CancellationToken cancellationToken)
    {
        var pressedKeys = new List<KeyDefinition>();
        var useSystemMessage = keyStroke.Modifiers.Any(modifier => modifier.VirtualKey == 0x12)
            || keyStroke.Key.VirtualKey == 0x12;

        try
        {
            foreach (var modifier in keyStroke.Modifiers)
            {
                PostKeyboardMessage(
                    hwnd,
                    modifier.VirtualKey == 0x12 ? WmSysKeyDown : WmKeyDown,
                    modifier,
                    isKeyUp: false);
                pressedKeys.Add(modifier);
            }

            PostKeyboardMessage(
                hwnd,
                useSystemMessage ? WmSysKeyDown : WmKeyDown,
                keyStroke.Key,
                isKeyUp: false);
            pressedKeys.Add(keyStroke.Key);

            await Task.Delay(holdDurationMs, cancellationToken);
        }
        finally
        {
            for (var index = pressedKeys.Count - 1; index >= 0; index--)
            {
                var key = pressedKeys[index];
                var isSystemKey = useSystemMessage || key.VirtualKey == 0x12;
                PostKeyboardMessage(
                    hwnd,
                    isSystemKey ? WmSysKeyUp : WmKeyUp,
                    key,
                    isKeyUp: true);
            }
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

    private static string NormalizeKeyName(string keyName)
    {
        return keyName.Trim().Replace(" ", string.Empty, StringComparison.Ordinal).ToUpperInvariant();
    }

    private static IReadOnlyDictionary<string, KeyDefinition> BuildKeyDefinitions()
    {
        var definitions = new Dictionary<string, KeyDefinition>(StringComparer.OrdinalIgnoreCase);

        void Add(string displayName, int virtualKey, bool isExtended = false, bool isModifier = false, params string[] aliases)
        {
            var key = new KeyDefinition(displayName, virtualKey, isExtended, isModifier);
            definitions[NormalizeKeyName(displayName)] = key;
            foreach (var alias in aliases)
            {
                definitions[NormalizeKeyName(alias)] = key;
            }
        }

        for (var key = 'A'; key <= 'Z'; key++)
        {
            Add(key.ToString(), key);
        }

        for (var number = 0; number <= 9; number++)
        {
            Add(number.ToString(), 0x30 + number, aliases: [$"D{number}", $"Digit{number}"]);
        }

        for (var number = 0; number <= 9; number++)
        {
            Add($"Numpad{number}", 0x60 + number, aliases: [$"Num{number}"]);
        }

        for (var number = 1; number <= 24; number++)
        {
            Add($"F{number}", 0x70 + number - 1);
        }

        Add("Backspace", 0x08, aliases: ["Back"]);
        Add("Tab", 0x09);
        Add("Enter", 0x0D, aliases: ["Return"]);
        Add("Shift", 0x10, isModifier: true);
        Add("Ctrl", 0x11, isModifier: true, aliases: ["Control"]);
        Add("Alt", 0x12, isModifier: true, aliases: ["Menu"]);
        Add("Pause", 0x13);
        Add("CapsLock", 0x14, aliases: ["Caps"]);
        Add("Escape", 0x1B, aliases: ["Esc"]);
        Add("Space", 0x20, aliases: ["Spacebar"]);
        Add("PageUp", 0x21, isExtended: true, aliases: ["PgUp"]);
        Add("PageDown", 0x22, isExtended: true, aliases: ["PgDn"]);
        Add("End", 0x23, isExtended: true);
        Add("Home", 0x24, isExtended: true);
        Add("Left", 0x25, isExtended: true, aliases: ["ArrowLeft"]);
        Add("Up", 0x26, isExtended: true, aliases: ["ArrowUp"]);
        Add("Right", 0x27, isExtended: true, aliases: ["ArrowRight"]);
        Add("Down", 0x28, isExtended: true, aliases: ["ArrowDown"]);
        Add("Insert", 0x2D, isExtended: true, aliases: ["Ins"]);
        Add("Delete", 0x2E, isExtended: true, aliases: ["Del"]);
        Add("LeftWin", 0x5B, isExtended: true, isModifier: true, aliases: ["LWin", "Win"]);
        Add("RightWin", 0x5C, isExtended: true, isModifier: true, aliases: ["RWin"]);
        Add("Multiply", 0x6A, aliases: ["NumpadMultiply"]);
        Add("Add", 0x6B, aliases: ["NumpadAdd"]);
        Add("Subtract", 0x6D, aliases: ["NumpadSubtract"]);
        Add("Decimal", 0x6E, aliases: ["NumpadDecimal"]);
        Add("Divide", 0x6F, isExtended: true, aliases: ["NumpadDivide"]);

        return definitions;
    }

    [DllImport("user32.dll", EntryPoint = "PostMessageW", SetLastError = true)]
    private static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", EntryPoint = "MapVirtualKeyW")]
    private static extern uint MapVirtualKey(uint uCode, uint uMapType);

    [DllImport("user32.dll")]
    private static extern bool IsWindow(IntPtr hWnd);
}
