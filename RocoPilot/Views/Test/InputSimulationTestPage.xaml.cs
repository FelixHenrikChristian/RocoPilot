using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

using RocoPilot.Contracts.Services;
using RocoPilot.Models.Capture;

namespace RocoPilot.Views.Test;

public sealed partial class InputSimulationTestPage : Page
{
    private const uint WmKeyDown = 0x0100;
    private const uint WmKeyUp = 0x0101;
    private const uint WmSysKeyDown = 0x0104;
    private const uint WmSysKeyUp = 0x0105;
    private const uint MapVkToVsc = 0;
    private const int ErrorAccessDenied = 5;
    private const int TokenIntegrityLevelClass = 25;
    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const uint TokenQuery = 0x0008;

    private static readonly IReadOnlyDictionary<string, KeyDefinition> KeyDefinitions = BuildKeyDefinitions();

    private readonly IGameWindowService _gameWindowService;
    private readonly ObservableCollection<KeySequencePreset> _presets =
    [
        new("技能键 1-6", "1, 2, 3, 4, 5, 6", 45, 120, "顺序发送数字键 1 到 6。"),
        new("移动键 WASD", "W, A, S, D", 60, 150, "顺序发送移动方向键。"),
        new("确认返回", "Enter, Escape", 45, 160, "顺序发送确认和返回。"),
        new("方向键", "Up, Right, Down, Left", 45, 140, "顺序发送键盘方向键。")
    ];
    private CancellationTokenSource? _sendCancellationTokenSource;
    private CaptureTargetWindow? _targetWindow;
    private bool _isSending;

    public ObservableCollection<KeyStrokePreview> KeyPreviewItems
    {
        get;
    } = new();

    public InputSimulationTestPage()
    {
        _gameWindowService = App.GetService<IGameWindowService>();

        InitializeComponent();

        PresetComboBox.ItemsSource = _presets;
        PreviewListView.ItemsSource = KeyPreviewItems;
        PresetComboBox.SelectedIndex = 0;

        Loaded += InputSimulationTestPage_Loaded;
        Unloaded += InputSimulationTestPage_Unloaded;
    }

    private void InputSimulationTestPage_Loaded(object sender, RoutedEventArgs e)
    {
        RefreshTargetWindow();
        UpdatePreview();
        UpdateRunState();
    }

    private void InputSimulationTestPage_Unloaded(object sender, RoutedEventArgs e)
    {
        _sendCancellationTokenSource?.Cancel();
        _sendCancellationTokenSource?.Dispose();
        _sendCancellationTokenSource = null;
    }

    private void RefreshTargetWindowButton_Click(object sender, RoutedEventArgs e)
    {
        RefreshTargetWindow();
    }

    private void PresetComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (PresetComboBox.SelectedItem is not KeySequencePreset preset)
        {
            return;
        }

        KeySequenceTextBox.Text = preset.Sequence;
        HoldDurationTextBox.Text = preset.HoldDurationMs.ToString();
        KeyIntervalTextBox.Text = preset.IntervalMs.ToString();
        PresetDescriptionText.Text = preset.Description;
        UpdatePreview();
    }

    private void KeySequenceTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        UpdatePreview();
    }

    private async void RunSequenceButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isSending)
        {
            return;
        }

        if (!TryBuildSendRequest(
            out var targetWindow,
            out var keyStrokes,
            out var holdDurationMs,
            out var intervalMs))
        {
            return;
        }

        _sendCancellationTokenSource = new CancellationTokenSource();
        var cancellationToken = _sendCancellationTokenSource.Token;
        SetSendingState(true);

        try
        {
            HideMessage();

            for (var index = 0; index < keyStrokes.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var keyStroke = keyStrokes[index];
                ExecutionStatusText.Text = $"发送中 {index + 1}/{keyStrokes.Count}：{keyStroke.DisplayText}";
                await SendKeyStrokeAsync(targetWindow.Hwnd, keyStroke, holdDurationMs, cancellationToken);

                if (index < keyStrokes.Count - 1 && intervalMs > 0)
                {
                    await Task.Delay(intervalMs, cancellationToken);
                }
            }

            ExecutionStatusText.Text = $"已完成：{keyStrokes.Count} 个按键";
            ShowMessage("键盘序列发送完成。", InfoBarSeverity.Success);
        }
        catch (OperationCanceledException)
        {
            ExecutionStatusText.Text = "已取消";
            ShowMessage("键盘序列发送已取消。", InfoBarSeverity.Warning);
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == ErrorAccessDenied)
        {
            ExecutionStatusText.Text = "发送失败";
            ShowMessage(BuildPostMessageAccessDeniedMessage(targetWindow), InfoBarSeverity.Error);
        }
        catch (Exception ex)
        {
            ExecutionStatusText.Text = "发送失败";
            ShowMessage($"键盘序列发送失败：{ex.Message}", InfoBarSeverity.Error);
        }
        finally
        {
            _sendCancellationTokenSource?.Dispose();
            _sendCancellationTokenSource = null;
            SetSendingState(false);
        }
    }

    private void CancelSequenceButton_Click(object sender, RoutedEventArgs e)
    {
        _sendCancellationTokenSource?.Cancel();
    }

    private void RefreshTargetWindow()
    {
        _targetWindow = _gameWindowService.FindGameWindow();
        if (_targetWindow is null)
        {
            TargetWindowText.Text = $"未找到目标游戏窗口：{_gameWindowService.TargetProcessName}";
            TargetWindowHandleText.Text = "-";
            ShowMessage($"未找到目标游戏窗口：{_gameWindowService.TargetProcessName}", InfoBarSeverity.Warning);
        }
        else
        {
            TargetWindowText.Text = _targetWindow.DisplayName;
            TargetWindowHandleText.Text = $"{_targetWindow.HandleText} · PID {_targetWindow.ProcessId}";
            HideMessage();
        }

        UpdateRunState();
    }

    private void UpdatePreview()
    {
        if (KeyPreviewItems is null)
        {
            return;
        }

        KeyPreviewItems.Clear();

        if (!TryParseSequence(KeySequenceTextBox.Text, out var keyStrokes, out var parseError))
        {
            if (ExecutionStatusText is not null)
            {
                ExecutionStatusText.Text = parseError;
            }

            UpdateRunState();
            return;
        }

        for (var index = 0; index < keyStrokes.Count; index++)
        {
            var keyStroke = keyStrokes[index];
            KeyPreviewItems.Add(new KeyStrokePreview(
                index + 1,
                keyStroke.DisplayText,
                $"VK 0x{keyStroke.Key.VirtualKey:X2}"));
        }

        if (!_isSending && ExecutionStatusText is not null)
        {
            ExecutionStatusText.Text = keyStrokes.Count == 0
                ? "就绪"
                : $"已解析 {keyStrokes.Count} 个按键";
        }

        UpdateRunState();
    }

    private bool TryBuildSendRequest(
        out CaptureTargetWindow targetWindow,
        out IReadOnlyList<KeyStroke> keyStrokes,
        out int holdDurationMs,
        out int intervalMs)
    {
        targetWindow = null!;
        keyStrokes = [];
        holdDurationMs = 0;
        intervalMs = 0;

        var currentWindow = _gameWindowService.FindGameWindow();
        if (currentWindow is null)
        {
            RefreshTargetWindow();
            return false;
        }

        _targetWindow = currentWindow;
        TargetWindowText.Text = currentWindow.DisplayName;
        TargetWindowHandleText.Text = $"{currentWindow.HandleText} · PID {currentWindow.ProcessId}";

        if (!IsWindow(currentWindow.Hwnd))
        {
            ShowMessage("目标游戏窗口句柄已失效，请刷新后重试。", InfoBarSeverity.Warning);
            UpdateRunState();
            return false;
        }

        if (TryGetIntegrityMismatchMessage(currentWindow, out var integrityMismatchMessage))
        {
            ShowMessage(integrityMismatchMessage, InfoBarSeverity.Warning);
            UpdateRunState();
            return false;
        }

        if (!TryParseSequence(KeySequenceTextBox.Text, out keyStrokes, out var parseError))
        {
            ShowMessage(parseError, InfoBarSeverity.Warning);
            UpdateRunState();
            return false;
        }

        if (keyStrokes.Count == 0)
        {
            ShowMessage("请先填写至少一个按键。", InfoBarSeverity.Warning);
            UpdateRunState();
            return false;
        }

        if (!TryReadInteger(
            HoldDurationTextBox.Text,
            "按住时长",
            1,
            5000,
            out holdDurationMs))
        {
            return false;
        }

        if (!TryReadInteger(
            KeyIntervalTextBox.Text,
            "间隔",
            0,
            60000,
            out intervalMs))
        {
            return false;
        }

        targetWindow = currentWindow;
        return true;
    }

    private static bool TryGetIntegrityMismatchMessage(
        CaptureTargetWindow targetWindow,
        out string message)
    {
        message = string.Empty;

        if (!TryGetProcessIntegrityLevel(Environment.ProcessId, out var currentIntegrity, out _)
            || !TryGetProcessIntegrityLevel(targetWindow.ProcessId, out var targetIntegrity, out _)
            || targetIntegrity.Rid <= currentIntegrity.Rid)
        {
            return false;
        }

        message = $"权限不足：RocoPilot 当前权限为 {currentIntegrity.Name}，目标游戏为 {targetIntegrity.Name}。"
            + "Windows 会拒绝低权限进程向高权限窗口 PostMessage。请以管理员身份启动 RocoPilot，或关闭游戏的管理员运行。";
        return true;
    }

    private static string BuildPostMessageAccessDeniedMessage(CaptureTargetWindow targetWindow)
    {
        if (TryGetProcessIntegrityLevel(Environment.ProcessId, out var currentIntegrity, out _)
            && TryGetProcessIntegrityLevel(targetWindow.ProcessId, out var targetIntegrity, out _))
        {
            if (targetIntegrity.Rid > currentIntegrity.Rid)
            {
                return $"Windows 拒绝向目标窗口 PostMessage：RocoPilot 当前权限为 {currentIntegrity.Name}，目标游戏为 {targetIntegrity.Name}。"
                    + "请以管理员身份启动 RocoPilot，或关闭游戏的管理员运行。";
            }

            return $"Windows 拒绝向目标窗口 PostMessage。当前 RocoPilot 权限为 {currentIntegrity.Name}，目标游戏为 {targetIntegrity.Name}；"
                + "如果两者权限一致，可能是游戏或反作弊拦截后台窗口消息。";
        }

        return "Windows 拒绝向目标窗口 PostMessage。通常是 RocoPilot 权限低于游戏窗口；请以管理员身份启动 RocoPilot，或关闭游戏的管理员运行。";
    }

    private static bool TryGetProcessIntegrityLevel(
        int processId,
        out ProcessIntegrityLevel integrityLevel,
        out string error)
    {
        integrityLevel = new ProcessIntegrityLevel(0, "未知");
        error = string.Empty;

        var processHandle = IntPtr.Zero;
        var tokenHandle = IntPtr.Zero;
        var tokenInformation = IntPtr.Zero;

        try
        {
            processHandle = OpenProcess(ProcessQueryLimitedInformation, false, (uint)processId);
            if (processHandle == IntPtr.Zero)
            {
                error = new Win32Exception(Marshal.GetLastWin32Error()).Message;
                return false;
            }

            if (!OpenProcessToken(processHandle, TokenQuery, out tokenHandle))
            {
                error = new Win32Exception(Marshal.GetLastWin32Error()).Message;
                return false;
            }

            _ = GetTokenInformation(
                tokenHandle,
                TokenIntegrityLevelClass,
                IntPtr.Zero,
                0,
                out var tokenInformationLength);
            if (tokenInformationLength <= 0)
            {
                error = new Win32Exception(Marshal.GetLastWin32Error()).Message;
                return false;
            }

            tokenInformation = Marshal.AllocHGlobal(tokenInformationLength);
            if (!GetTokenInformation(
                tokenHandle,
                TokenIntegrityLevelClass,
                tokenInformation,
                tokenInformationLength,
                out _))
            {
                error = new Win32Exception(Marshal.GetLastWin32Error()).Message;
                return false;
            }

            var mandatoryLabel = Marshal.PtrToStructure<TokenMandatoryLabel>(tokenInformation);
            var subAuthorityCountPointer = GetSidSubAuthorityCount(mandatoryLabel.Label.Sid);
            if (subAuthorityCountPointer == IntPtr.Zero)
            {
                error = "无法读取进程完整性级别。";
                return false;
            }

            var subAuthorityCount = Marshal.ReadByte(subAuthorityCountPointer);
            if (subAuthorityCount == 0)
            {
                error = "进程完整性 SID 无效。";
                return false;
            }

            var integrityRidPointer = GetSidSubAuthority(
                mandatoryLabel.Label.Sid,
                (byte)(subAuthorityCount - 1));
            if (integrityRidPointer == IntPtr.Zero)
            {
                error = "无法读取进程完整性 RID。";
                return false;
            }

            var integrityRid = Marshal.ReadInt32(integrityRidPointer);
            integrityLevel = new ProcessIntegrityLevel(integrityRid, FormatIntegrityLevelName(integrityRid));
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
        finally
        {
            if (tokenInformation != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(tokenInformation);
            }

            if (tokenHandle != IntPtr.Zero)
            {
                _ = CloseHandle(tokenHandle);
            }

            if (processHandle != IntPtr.Zero)
            {
                _ = CloseHandle(processHandle);
            }
        }
    }

    private static string FormatIntegrityLevelName(int integrityRid)
    {
        return integrityRid switch
        {
            >= 0x4000 => "System",
            >= 0x3000 => "管理员/高完整性",
            >= 0x2000 => "普通/中完整性",
            >= 0x1000 => "低完整性",
            _ => $"未知(0x{integrityRid:X})"
        };
    }

    private bool TryReadInteger(
        string value,
        string fieldName,
        int minimum,
        int maximum,
        out int result)
    {
        if (!int.TryParse(value, out result) || result < minimum || result > maximum)
        {
            ShowMessage($"{fieldName} 必须是 {minimum}-{maximum} 之间的整数。", InfoBarSeverity.Warning);
            return false;
        }

        return true;
    }

    private static bool TryParseSequence(
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

    private async Task SendKeyStrokeAsync(
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

    private void SetSendingState(bool isSending)
    {
        _isSending = isSending;

        PresetComboBox.IsEnabled = !isSending;
        KeySequenceTextBox.IsEnabled = !isSending;
        HoldDurationTextBox.IsEnabled = !isSending;
        KeyIntervalTextBox.IsEnabled = !isSending;
        CancelSequenceButton.IsEnabled = isSending;
        UpdateRunState();
    }

    private void UpdateRunState()
    {
        if (RunSequenceButton is null)
        {
            return;
        }

        RunSequenceButton.IsEnabled = !_isSending
            && _targetWindow is not null
            && KeyPreviewItems.Count > 0;
    }

    private void ShowMessage(string message, InfoBarSeverity severity)
    {
        InputSimulationInfoBar.Message = message;
        InputSimulationInfoBar.Severity = severity;
        InputSimulationInfoBar.IsOpen = false;
        InputSimulationInfoBar.IsOpen = true;
    }

    private void HideMessage()
    {
        InputSimulationInfoBar.IsOpen = false;
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

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, uint dwProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(IntPtr processHandle, uint desiredAccess, out IntPtr tokenHandle);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool GetTokenInformation(
        IntPtr tokenHandle,
        int tokenInformationClass,
        IntPtr tokenInformation,
        int tokenInformationLength,
        out int returnLength);

    [DllImport("advapi32.dll")]
    private static extern IntPtr GetSidSubAuthorityCount(IntPtr sid);

    [DllImport("advapi32.dll")]
    private static extern IntPtr GetSidSubAuthority(IntPtr sid, byte subAuthorityIndex);

    private readonly record struct ProcessIntegrityLevel(int Rid, string Name);

    [StructLayout(LayoutKind.Sequential)]
    private struct SidAndAttributes
    {
        public IntPtr Sid;
        public int Attributes;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TokenMandatoryLabel
    {
        public SidAndAttributes Label;
    }

    private sealed record KeySequencePreset(
        string Name,
        string Sequence,
        int HoldDurationMs,
        int IntervalMs,
        string Description);

    private sealed record KeyStroke(
        IReadOnlyList<KeyDefinition> Modifiers,
        KeyDefinition Key)
    {
        public string DisplayText
        {
            get
            {
                if (Modifiers.Count == 0)
                {
                    return Key.DisplayName;
                }

                return $"{string.Join("+", Modifiers.Select(modifier => modifier.DisplayName))}+{Key.DisplayName}";
            }
        }
    }

    private sealed record KeyDefinition(
        string DisplayName,
        int VirtualKey,
        bool IsExtended = false,
        bool IsModifier = false);

    public sealed class KeyStrokePreview
    {
        public KeyStrokePreview(int index, string displayText, string virtualKeyText)
        {
            Index = index;
            DisplayText = displayText;
            VirtualKeyText = virtualKeyText;
        }

        public int Index
        {
            get;
            set;
        }

        public string DisplayText
        {
            get;
            set;
        }

        public string VirtualKeyText
        {
            get;
            set;
        }
    }
}
