using System.Xml.Linq;
using Microsoft.Extensions.Logging;
using Microsoft.Windows.AppNotifications;
using RocoPilot.Configuration;
using RocoPilot.Contracts.Services;

namespace RocoPilot.Notifications;

public class AppNotificationService(ILocalSettingsService settings, ILogger<AppNotificationService> logger) : IAppNotificationService
{
    private AppNotificationManager? _manager;

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
        if (_manager is not null) return;
        try
        {
            if (!AppNotificationManager.IsSupported())
            {
                LastError = "当前运行模式不支持 Windows 通知，请以普通用户身份启动 RocoPilot。";
                return;
            }

            _manager = AppNotificationManager.Default;
            _manager.NotificationInvoked += OnNotificationInvoked;
            _manager.Register();
            LastError = null;
        }
        catch (Exception ex)
        {
            if (_manager is not null)
            {
                _manager.NotificationInvoked -= OnNotificationInvoked;
                _manager = null;
            }
            LastError = $"Windows 通知初始化失败：{ex.Message}";
            logger.LogWarning(ex, "Windows 通知初始化失败。");
        }
    }

    private static void OnNotificationInvoked(AppNotificationManager sender, AppNotificationActivatedEventArgs args)
    {
        App.MainWindow.DispatcherQueue.TryEnqueue(() => App.MainWindow.BringToFront());
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

    public Task<bool> ShowAsync(string title, string message, string? imagePath = null)
    {
        if (!IsEnabled) return Task.FromResult(false);
        if (_manager is null)
        {
            LastError ??= "Windows 通知尚未初始化。";
            return Task.FromResult(false);
        }
        try
        {
            var setting = _manager.Setting;
            if (setting != AppNotificationSetting.Enabled)
                throw new InvalidOperationException($"Windows 通知不可用：{setting}");

            var notification = new AppNotification(BuildPayload(title, message, IsSoundEnabled, imagePath));
            _manager.Show(notification);
            if (notification.Id == 0)
                throw new InvalidOperationException("Windows 未接收此通知。");
            LastError = null;
            return Task.FromResult(true);
        }
        catch (Exception ex)
        {
            LastError = $"Windows 通知发送失败：{ex.Message}";
            logger.LogWarning(ex, "发送 Windows 通知失败：{Title}", title);
            return Task.FromResult(false);
        }
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
        if (_manager is null) return;
        _manager.NotificationInvoked -= OnNotificationInvoked;
        _manager.Unregister();
        _manager = null;
    }
}
