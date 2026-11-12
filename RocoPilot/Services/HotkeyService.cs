using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using RocoPilot.Configuration;
using RocoPilot.Contracts.Services;
using RocoPilot.Models.Hotkeys;
using RocoPilot.Models.Input;
using RocoPilot.Services.Hotkeys;

namespace RocoPilot.Services;

public sealed class HotkeyService : IHotkeyService
{
    private const int EscapeVirtualKey = 0x1B;
    private readonly ILocalSettingsService _localSettingsService;
    private readonly IGameWindowService _gameWindowService;
    private readonly ILogger<HotkeyService> _logger;
    private readonly Func<ProcessStartInfo, Process?> _startProcess;
    private readonly SemaphoreSlim _settingsLock = new(1, 1);
    private readonly object _stateLock = new();
    private readonly CancellationTokenSource _stopping = new();
    private HotkeySettings _settings = HotkeySettings.CreateDefault();
    private NamedPipeServerStream? _pipe;
    private bool _settingsLoaded;
    private bool _isDisposed;

    public event EventHandler? SettingsChanged;
    public event EventHandler<HotkeyTriggeredEventArgs>? HotkeyTriggered;
    public string? UnavailableReason { get; private set; }

    public HotkeySettings Settings
    {
        get { lock (_stateLock) return _settings.Clone(); }
    }

    public HotkeyService(ILocalSettingsService localSettingsService,
        IGameWindowService gameWindowService, ILogger<HotkeyService> logger)
        : this(localSettingsService, gameWindowService, logger, Process.Start)
    {
    }

    internal HotkeyService(ILocalSettingsService localSettingsService,
        IGameWindowService gameWindowService, ILogger<HotkeyService> logger,
        Func<ProcessStartInfo, Process?> startProcess)
    {
        _localSettingsService = localSettingsService;
        _gameWindowService = gameWindowService;
        _logger = logger;
        _startProcess = startProcess;
    }

    public async Task LoadSettingsAsync(CancellationToken cancellationToken = default)
    {
        if (_isDisposed) return;
        await _settingsLock.WaitAsync(cancellationToken);
        try
        {
            if (_settingsLoaded) return;
            try
            {
                var saved = await _localSettingsService.ReadSettingAsync<HotkeySettings>(SettingsKeys.HotkeySettings);
                lock (_stateLock) _settings = NormalizeSettings(saved);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "读取热键设置失败，使用默认未绑定设置。");
            }

            _settingsLoaded = true;
            try
            {
                await ConfigureHostAsync(Settings, allowStart: true, cancellationToken);
            }
            catch (Exception ex)
            {
                // 授权取消或监听启动失败只影响监听，不丢失用户已保存的绑定。
                _logger.LogWarning(ex, "热键辅助进程未启动。");
            }
        }
        finally
        {
            _settingsLock.Release();
        }

        SettingsChanged?.Invoke(this, EventArgs.Empty);
    }

    public async Task<HotkeyBindingUpdateResult> SetBindingAsync(HotkeyAction action,
        HotkeyBinding? binding, CancellationToken cancellationToken = default)
    {
        await LoadSettingsAsync(cancellationToken);
        await _settingsLock.WaitAsync(cancellationToken);
        try
        {
            ObjectDisposedException.ThrowIf(_isDisposed, this);
            var next = Settings;
            var normalized = NormalizeBinding(binding);
            HotkeyAction? replacedAction = null;
            next.Bindings.RemoveAll(item => item.Action == action);
            if (normalized is not null)
            {
                var duplicate = next.Bindings.FirstOrDefault(item => item.Binding?.GestureId == normalized.GestureId);
                if (duplicate is not null)
                {
                    replacedAction = duplicate.Action;
                    next.Bindings.Remove(duplicate);
                }
                next.Bindings.Add(new HotkeyBindingAssignment { Action = action, Binding = normalized });
            }

            await _localSettingsService.SaveSettingAsync(SettingsKeys.HotkeySettings, next);
            lock (_stateLock) _settings = next;
            try
            {
                await ConfigureHostAsync(next, allowStart: false, cancellationToken);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"绑定已保存，但监听未启动：{UnavailableReason}", ex);
            }
            return new HotkeyBindingUpdateResult(replacedAction);
        }
        finally
        {
            _settingsLock.Release();
            SettingsChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private async Task ConfigureHostAsync(HotkeySettings settings, bool allowStart, CancellationToken cancellationToken)
    {
        if (settings.Bindings.Count == 0)
        {
            StopHost();
            UnavailableReason = null;
            return;
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _stopping.Token);
        try
        {
            ObjectDisposedException.ThrowIf(_isDisposed, this);
            var configuration = JsonSerializer.Serialize(new HotkeyHostConfiguration(
                Path.GetFileNameWithoutExtension(_gameWindowService.TargetProcessName), settings));
            NamedPipeServerStream? pipe;
            lock (_stateLock) pipe = _pipe;
            if (pipe is not null)
            {
                await HotkeyHost.WriteMessageAsync(pipe, configuration, timeout.Token);
                return;
            }
            if (!allowStart) return;

            var pipeName = $"RocoPilot.Hotkeys.{Guid.NewGuid():N}";
            // 服务端由普通用户创建，仅允许该用户访问；提权客户端不使用
            // CurrentUserOnly，因为 .NET 客户端还会校验管道所有者的提权级别。
            pipe = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1,
                PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            lock (_stateLock) _pipe = pipe;
            var startInfo = new ProcessStartInfo(Environment.ProcessPath!)
            {
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden
            };
            startInfo.ArgumentList.Add(HotkeyHost.Argument);
            startInfo.ArgumentList.Add(pipeName);
            startInfo.ArgumentList.Add(Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
            using var process = _startProcess(startInfo)
                ?? throw new InvalidOperationException("无法启动热键辅助进程。");
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            await pipe.WaitForConnectionAsync(timeout.Token);
            HotkeyHost.VerifyClient(pipe, process.Id);
            var reader = new StreamReader(pipe, Encoding.UTF8, leaveOpen: true);
            await HotkeyHost.WriteMessageAsync(pipe, configuration, timeout.Token);
            if (await reader.ReadLineAsync(timeout.Token) != "ready")
            {
                throw new IOException("热键辅助进程未能安装键盘监听。");
            }

            UnavailableReason = null;
            _logger.LogInformation("热键辅助进程已连接，PID={ProcessId}，主界面权限保持不变。", process.Id);
            _ = ReadActionsAsync(pipe, reader);
        }
        catch (Exception ex)
        {
            StopHost();
            UnavailableReason = ex is Win32Exception { NativeErrorCode: 1223 }
                ? "已取消管理员授权。请重启应用。"
                : $"{ex.Message} 请重启应用。";
            throw;
        }
    }

    private async Task ReadActionsAsync(NamedPipeServerStream pipe, StreamReader reader)
    {
        try
        {
            while (await reader.ReadLineAsync(_stopping.Token).ConfigureAwait(false) is { } line)
            {
                if (int.TryParse(line, out var value) && Enum.IsDefined((HotkeyAction)value))
                {
                    var action = (HotkeyAction)value;
                    _ = App.MainWindow.DispatcherQueue.TryEnqueue(() =>
                    {
                        if (!_isDisposed) HotkeyTriggered?.Invoke(this, new HotkeyTriggeredEventArgs(action));
                    });
                }
            }
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException)
        {
            _logger.LogDebug(ex, "热键辅助进程连接结束。");
        }
        finally
        {
            using (reader)
            {
                lock (_stateLock)
                {
                    if (ReferenceEquals(_pipe, pipe))
                    {
                        _pipe = null;
                        UnavailableReason = "热键监听已断开。请重启应用。";
                        _logger.LogWarning("热键辅助进程已断开。");
                    }
                }
                pipe.Dispose();
            }
            if (!_isDisposed) SettingsChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private void StopHost()
    {
        NamedPipeServerStream? pipe;
        lock (_stateLock)
        {
            pipe = _pipe;
            _pipe = null;
        }
        pipe?.Dispose();
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;
        _stopping.Cancel();
        StopHost();
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
}
