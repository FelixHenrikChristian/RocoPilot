using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

using Microsoft.Extensions.Logging;

using RocoPilot.Configuration;
using RocoPilot.Contracts.Services;
using RocoPilot.Models.Hotkeys;
using RocoPilot.Models.Input;

namespace RocoPilot.Services;

public sealed class HotkeyService : IHotkeyService, IDisposable
{
    private const int WhKeyboardLl = 13;
    private const int WmKeyDown = 0x0100;
    private const int WmKeyUp = 0x0101;
    private const int WmSysKeyDown = 0x0104;
    private const int WmSysKeyUp = 0x0105;
    private const int EscapeVirtualKey = 0x1B;

    private static readonly int[] ModifierVirtualKeys =
    [
        0x11,
        0x12,
        0x10,
        0x5B,
        0x5C
    ];

    private readonly ILocalSettingsService _localSettingsService;
    private readonly IGameWindowService _gameWindowService;
    private readonly ILogger<HotkeyService> _logger;
    private readonly SemaphoreSlim _settingsLock = new(1, 1);
    private readonly object _stateLock = new();
    private readonly HashSet<int> _activeHotkeyKeys = [];
    private readonly LowLevelKeyboardProc _keyboardProc;

    private HotkeySettings _settings = HotkeySettings.CreateDefault();
    private IntPtr _keyboardHook;
    private bool _settingsLoaded;
    private bool _isDisposed;

    public event EventHandler? SettingsChanged;

    public event EventHandler<HotkeyTriggeredEventArgs>? HotkeyTriggered;

    public HotkeySettings Settings
    {
        get
        {
            lock (_stateLock)
            {
                return _settings.Clone();
            }
        }
    }

    public HotkeyService(
        ILocalSettingsService localSettingsService,
        IGameWindowService gameWindowService,
        ILogger<HotkeyService> logger)
    {
        _localSettingsService = localSettingsService;
        _gameWindowService = gameWindowService;
        _logger = logger;
        _keyboardProc = KeyboardHookProc;
    }

    public async Task LoadSettingsAsync(CancellationToken cancellationToken = default)
    {
        if (_isDisposed)
        {
            return;
        }

        await _settingsLock.WaitAsync(cancellationToken);
        try
        {
            if (_settingsLoaded)
            {
                return;
            }

            var savedSettings =
                await _localSettingsService.ReadSettingAsync<HotkeySettings>(SettingsKeys.HotkeySettings);
            var settings = NormalizeSettings(savedSettings);
            await RunOnDispatcherAsync(() =>
            {
                EnsureKeyboardHook();
                ApplySettingsCore(settings);
            });

            _settingsLoaded = true;
            _logger.LogDebug("热键服务已初始化。");
        }
        catch (Exception ex)
        {
            lock (_stateLock)
            {
                _settings = HotkeySettings.CreateDefault();
                _activeHotkeyKeys.Clear();
                _settingsLoaded = true;
            }

            _logger.LogWarning(ex, "读取热键设置失败，已使用默认未绑定设置。");
        }
        finally
        {
            _settingsLock.Release();
        }
    }

    public async Task<HotkeyBindingUpdateResult> SetBindingAsync(
        HotkeyAction action,
        HotkeyBinding? binding,
        CancellationToken cancellationToken = default)
    {
        await LoadSettingsAsync(cancellationToken);

        await _settingsLock.WaitAsync(cancellationToken);
        try
        {
            var normalizedBinding = NormalizeBinding(binding);
            HotkeyAction? replacedAction = null;
            HotkeySettings nextSettings;

            lock (_stateLock)
            {
                nextSettings = NormalizeSettings(_settings);
            }

            nextSettings.Bindings.RemoveAll(assignment => assignment.Action == action);
            if (normalizedBinding is not null)
            {
                var duplicatedAssignment = nextSettings.Bindings.FirstOrDefault(
                    assignment => assignment.Action != action
                        && string.Equals(
                            assignment.Binding?.GestureId,
                            normalizedBinding.GestureId,
                            StringComparison.Ordinal));

                if (duplicatedAssignment is not null)
                {
                    replacedAction = duplicatedAssignment.Action;
                    nextSettings.Bindings.Remove(duplicatedAssignment);
                }

                nextSettings.Bindings.Add(new HotkeyBindingAssignment
                {
                    Action = action,
                    Binding = normalizedBinding
                });
            }

            await RunOnDispatcherAsync(() =>
            {
                EnsureKeyboardHook();
                ApplySettingsCore(nextSettings);
            });
            await _localSettingsService.SaveSettingAsync(SettingsKeys.HotkeySettings, nextSettings);

            SettingsChanged?.Invoke(this, EventArgs.Empty);
            return new HotkeyBindingUpdateResult(replacedAction);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "保存热键设置失败。");
            throw;
        }
        finally
        {
            _settingsLock.Release();
        }
    }

    public void Dispose()
    {
        if (_isDisposed)
        {
            return;
        }

        _isDisposed = true;
        RunOnDispatcher(UninstallKeyboardHook);
        _settingsLock.Dispose();
    }

    private void ApplySettingsCore(HotkeySettings settings)
    {
        lock (_stateLock)
        {
            _settings = NormalizeSettings(settings);
            _activeHotkeyKeys.Clear();
        }
    }

    private void EnsureKeyboardHook()
    {
        ThrowIfNotOnDispatcher();

        if (_keyboardHook != IntPtr.Zero)
        {
            return;
        }

        var moduleHandle = GetModuleHandle(null);
        _keyboardHook = SetWindowsHookEx(WhKeyboardLl, _keyboardProc, moduleHandle, 0);
        if (_keyboardHook == IntPtr.Zero)
        {
            var errorCode = Marshal.GetLastWin32Error();
            throw new InvalidOperationException(
                $"安装热键键盘监听失败：{new Win32Exception(errorCode).Message}");
        }
    }

    private void UninstallKeyboardHook()
    {
        ThrowIfNotOnDispatcher();

        if (_keyboardHook == IntPtr.Zero)
        {
            return;
        }

        _ = UnhookWindowsHookEx(_keyboardHook);
        _keyboardHook = IntPtr.Zero;
        lock (_stateLock)
        {
            _activeHotkeyKeys.Clear();
        }
    }

    private IntPtr KeyboardHookProc(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code < 0)
        {
            return CallNextHookEx(_keyboardHook, code, wParam, lParam);
        }

        try
        {
            var message = wParam.ToInt32();
            var hookData = Marshal.PtrToStructure<KeyboardHookData>(lParam);
            var virtualKey = checked((int)hookData.VirtualKeyCode);

            if (message is WmKeyUp or WmSysKeyUp)
            {
                lock (_stateLock)
                {
                    _activeHotkeyKeys.Remove(virtualKey);
                }
            }
            else if ((message is WmKeyDown or WmSysKeyDown) && TryHandleKeyDown(virtualKey))
            {
                return new IntPtr(1);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "处理热键键盘事件失败。");
        }

        return CallNextHookEx(_keyboardHook, code, wParam, lParam);
    }

    private bool TryHandleKeyDown(int virtualKey)
    {
        lock (_stateLock)
        {
            if (_activeHotkeyKeys.Contains(virtualKey))
            {
                return true;
            }
        }

        if (!IsTargetGameForegroundWindow())
        {
            return false;
        }

        var binding = CreateCurrentBinding(virtualKey);
        if (binding is null)
        {
            return false;
        }

        HotkeyAction? action = null;
        lock (_stateLock)
        {
            var assignment = _settings.Bindings.FirstOrDefault(assignment =>
                assignment.Binding is not null
                && string.Equals(assignment.Binding.GestureId, binding.GestureId, StringComparison.Ordinal));
            if (assignment is null)
            {
                return false;
            }

            _activeHotkeyKeys.Add(virtualKey);
            action = assignment.Action;
        }

        HotkeyTriggered?.Invoke(this, new HotkeyTriggeredEventArgs(action.Value));
        _logger.LogInformation("热键已触发：{Action}", GetActionName(action.Value));
        return true;
    }

    private bool IsTargetGameForegroundWindow()
    {
        var foregroundWindow = GetForegroundWindow();
        if (foregroundWindow == IntPtr.Zero)
        {
            return false;
        }

        _ = GetWindowThreadProcessId(foregroundWindow, out var processId);
        if (processId == 0)
        {
            return false;
        }

        try
        {
            using var process = Process.GetProcessById(checked((int)processId));
            var targetProcessName = Path.GetFileNameWithoutExtension(_gameWindowService.TargetProcessName);
            return string.Equals(process.ProcessName, targetProcessName, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or Win32Exception)
        {
            return false;
        }
    }

    private static HotkeyBinding? CreateCurrentBinding(int virtualKey)
    {
        if (!KeyCatalog.TryGetDefinitionByVirtualKey(virtualKey, out var keyDefinition)
            || keyDefinition.IsModifier)
        {
            return null;
        }

        var modifiers = ModifierVirtualKeys
            .Where(IsKeyDown)
            .ToArray();
        return HotkeyBinding.Create(modifiers, virtualKey);
    }

    private static HotkeySettings NormalizeSettings(HotkeySettings? settings)
    {
        var normalized = HotkeySettings.CreateDefault();
        if (settings?.Bindings is null)
        {
            return normalized;
        }

        var validActions = HotkeyActionDescriptor.CreateDefault()
            .Select(descriptor => descriptor.Action)
            .ToHashSet();
        var usedGestureIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var assignment in settings.Bindings)
        {
            if (!validActions.Contains(assignment.Action))
            {
                continue;
            }

            var binding = NormalizeBinding(assignment.Binding);
            if (binding is null || !usedGestureIds.Add(binding.GestureId))
            {
                continue;
            }

            normalized.Bindings.Add(new HotkeyBindingAssignment
            {
                Action = assignment.Action,
                Binding = binding
            });
        }

        return normalized;
    }

    private static HotkeyBinding? NormalizeBinding(HotkeyBinding? binding)
    {
        if (binding is null
            || binding.Key == EscapeVirtualKey
            || !KeyCatalog.TryGetDefinitionByVirtualKey(binding.Key, out var keyDefinition)
            || keyDefinition.IsModifier)
        {
            return null;
        }

        var modifiers = binding.Modifiers
            .Where(virtualKey => KeyCatalog.TryGetDefinitionByVirtualKey(virtualKey, out var modifier)
                && modifier.IsModifier)
            .Distinct()
            .ToArray();
        if (modifiers.Length != binding.Modifiers.Distinct().Count())
        {
            return null;
        }

        return HotkeyBinding.Create(modifiers, binding.Key);
    }

    private void RunOnDispatcher(Action action)
    {
        var dispatcherQueue = App.MainWindow.DispatcherQueue;
        if (dispatcherQueue is null || dispatcherQueue.HasThreadAccess)
        {
            action();
            return;
        }

        _ = dispatcherQueue.TryEnqueue(() => action());
    }

    private Task RunOnDispatcherAsync(Action action)
    {
        var dispatcherQueue = App.MainWindow.DispatcherQueue;
        if (dispatcherQueue is null || dispatcherQueue.HasThreadAccess)
        {
            action();
            return Task.CompletedTask;
        }

        var completionSource = new TaskCompletionSource();
        if (!dispatcherQueue.TryEnqueue(() =>
            {
                try
                {
                    action();
                    completionSource.SetResult();
                }
                catch (Exception ex)
                {
                    completionSource.SetException(ex);
                }
            }))
        {
            completionSource.SetException(new InvalidOperationException("无法调度到 UI 线程。"));
        }

        return completionSource.Task;
    }

    private static void ThrowIfNotOnDispatcher()
    {
        var dispatcherQueue = App.MainWindow.DispatcherQueue;
        if (dispatcherQueue is not null && !dispatcherQueue.HasThreadAccess)
        {
            throw new InvalidOperationException("热键监听必须在 UI 线程管理。");
        }
    }

    private static bool IsKeyDown(int virtualKey)
    {
        return (GetAsyncKeyState(virtualKey) & 0x8000) != 0;
    }

    private static string GetActionName(HotkeyAction action)
    {
        return action switch
        {
            HotkeyAction.ToggleInfoOverlay => "信息遮罩窗口",
            HotkeyAction.ToggleEncounterStatistics => "奇遇统计",
            HotkeyAction.ToggleAutoBattle => "自动战斗",
            _ => action.ToString()
        };
    }

    private delegate IntPtr LowLevelKeyboardProc(int code, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct KeyboardHookData
    {
        public uint VirtualKeyCode;
        public uint ScanCode;
        public uint Flags;
        public uint Time;
        public IntPtr ExtraInfo;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(
        int idHook,
        LowLevelKeyboardProc callback,
        IntPtr module,
        uint threadId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(IntPtr hook);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int virtualKey);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr GetModuleHandle(string? moduleName);
}
