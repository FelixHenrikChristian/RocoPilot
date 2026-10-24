using System.ComponentModel;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using System.Xml.Linq;
using Microsoft.Extensions.Logging;
using RocoPilot.Configuration;
using RocoPilot.Contracts.Services;
using RocoPilot.Helpers;
using WinUIEx.Messaging;

namespace RocoPilot.Notifications;

public class AppNotificationService(ILocalSettingsService settings, ILogger<AppNotificationService> logger) : IAppNotificationService
{
    private const uint NotificationClicked = 0x8000 + 74;
    private readonly SemaphoreSlim _sendGate = new(1, 1);
    private NamedPipeServerStream? _pipe;
    private StreamReader? _reader;
    private StreamWriter? _writer;
    private WindowMessageMonitor? _monitor;

    public bool IsEnabled { get; private set; } = true;
    public bool IsSoundEnabled { get; private set; } = true;
    public string? LastError { get; private set; }

    internal async Task LoadSettingsAsync()
    {
        try
        {
            IsEnabled = await settings.ReadSettingAsync<bool?>(SettingsKeys.NotificationEnabled) ?? true;
            IsSoundEnabled = await settings.ReadSettingAsync<bool?>(SettingsKeys.NotificationSoundEnabled) ?? true;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "读取通知设置失败，使用当前设置。");
        }
    }

    public async Task InitializeAsync()
    {
        await LoadSettingsAsync();
        if (_pipe is not null) return;
        try
        {
            var window = WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow);
            if (!ChangeWindowMessageFilterEx(window, NotificationClicked, 1, 0)) throw new Win32Exception();
            _monitor = new WindowMessageMonitor(window);
            _monitor.WindowMessageReceived += (_, args) =>
            {
                if (args.Message.MessageId != NotificationClicked) return;
                App.MainWindow.DispatcherQueue.TryEnqueue(() => App.MainWindow.BringToFront());
                args.Handled = true;
            };
            await ConnectHostAsync(window);
            logger.LogInformation("Windows 图片通知已初始化，通知辅助进程以普通用户权限运行。");
        }
        catch (Exception ex)
        {
            Unregister();
            LastError = "Windows 通知初始化失败，请查看日志中的具体原因。";
            logger.LogWarning(ex, "Windows 通知初始化失败。");
        }
    }

    internal async Task ConnectHostAsync(nint window)
    {
        var pipeName = "RocoPilot.Notifications." + Guid.NewGuid().ToString("N");
        _pipe = CreateNotificationPipe(pipeName);
        NotificationHostLauncher.Start(Path.Combine(AppContext.BaseDirectory, "RocoPilot.NotificationHost.exe"), pipeName, window);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await _pipe.WaitForConnectionAsync(timeout.Token);
        _reader = new StreamReader(_pipe, leaveOpen: true);
        _writer = new StreamWriter(_pipe, leaveOpen: true) { AutoFlush = true };
        var result = await _reader.ReadLineAsync(timeout.Token);
        if (result is null || JsonSerializer.Deserialize<string>(result) != "ready")
            throw new InvalidOperationException("通知辅助进程初始化失败：" + result);
        LastError = null;
    }

    internal static NamedPipeServerStream CreateNotificationPipe(string pipeName)
    {
        using var identity = WindowsIdentity.GetCurrent();
        // CurrentUserOnly grants access to the token owner (Administrators when elevated).
        // The notification host runs unelevated, so authorize the account SID instead.
        var security = new PipeSecurity();
        security.SetOwner(identity.User!);
        security.AddAccessRule(new PipeAccessRule(identity.User!, PipeAccessRights.FullControl, AccessControlType.Allow));
        return NamedPipeServerStreamAcl.Create(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous, 0, 0, security);
    }

    public Task SetEnabledAsync(bool enabled)
    {
        IsEnabled = enabled;
        return settings.SaveSettingAsync(SettingsKeys.NotificationEnabled, enabled);
    }

    public Task SetSoundEnabledAsync(bool enabled)
    {
        IsSoundEnabled = enabled;
        return settings.SaveSettingAsync(SettingsKeys.NotificationSoundEnabled, enabled);
    }

    public async Task<bool> ShowAsync(string title, string message, string? imagePath = null)
    {
        if (!IsEnabled) return false;
        await _sendGate.WaitAsync();
        try
        {
            if (_writer is null || _reader is null) throw new InvalidOperationException("Windows 通知尚未初始化。");
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await _writer.WriteLineAsync(JsonSerializer.Serialize(BuildPayload(title, message, IsSoundEnabled, imagePath)).AsMemory(), timeout.Token);
            var result = await _reader.ReadLineAsync(timeout.Token);
            if (result is null || JsonSerializer.Deserialize<string>(result) != "ok")
                throw new InvalidOperationException("通知辅助进程发送失败：" + result);
            LastError = null;
            return true;
        }
        catch (Exception ex)
        {
            // A timed-out/broken exchange cannot be reused: its next reply could belong to the previous request.
            if (ex is IOException or OperationCanceledException) Unregister();
            LastError = "Windows 通知发送失败，请查看日志中的具体原因。";
            logger.LogWarning(ex, "发送 Windows 通知失败：{Title}", title);
            return false;
        }
        finally { _sendGate.Release(); }
    }

    internal static string BuildPayload(string title, string message, bool sound, string? imagePath = null)
    {
        var binding = new XElement("binding", new XAttribute("template", "ToastGeneric"),
            new XElement("text", title), new XElement("text", message));
        if (imagePath is not null)
            binding.Add(new XElement("image", new XAttribute("src", new Uri(Path.GetFullPath(imagePath)).AbsoluteUri),
                new XAttribute("alt", "发现异色精灵时的游戏截图")));
        var toast = new XElement("toast", new XElement("visual", binding));
        if (!sound) toast.Add(new XElement("audio", new XAttribute("silent", "true")));
        return toast.ToString(SaveOptions.DisableFormatting);
    }

    public void Unregister()
    {
        try { _writer?.Dispose(); }
        catch (IOException) { } // A disconnected host cannot flush any remaining buffered output.
        _reader?.Dispose();
        _pipe?.Dispose(); // EOF shuts down the notification host, including after a main-process crash.
        _reader = null;
        _writer = null;
        _pipe = null;
        _monitor?.Dispose();
        _monitor = null;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ChangeWindowMessageFilterEx(nint window, uint message, uint action, nint changeInfo);
}
