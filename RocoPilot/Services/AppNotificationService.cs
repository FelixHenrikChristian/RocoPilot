using System.Xml.Linq;
using Microsoft.Extensions.Logging;
using CommunityToolkit.WinUI.Notifications;
using Windows.Data.Xml.Dom;
using Windows.UI.Notifications;
using RocoPilot.Configuration;
using RocoPilot.Contracts.Services;

namespace RocoPilot.Notifications;

public class AppNotificationService(ILocalSettingsService settings, ILogger<AppNotificationService> logger) : IAppNotificationService
{
    private ToastNotifierCompat? _notifier;

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
        if (_notifier is not null) return;
        try
        {
            var notifier = ToastNotificationManagerCompat.CreateToastNotifier();
            ToastNotificationManagerCompat.OnActivated += OnNotificationInvoked;
            _notifier = notifier;
            LastError = null;
        }
        catch (Exception ex)
        {
            LastError = $"Windows 通知初始化失败：{ex.Message}";
            logger.LogWarning(ex, "Windows 通知初始化失败。");
        }
    }

    private static void OnNotificationInvoked(ToastNotificationActivatedEventArgsCompat args)
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
        try
        {
            var notifier = _notifier ?? throw new InvalidOperationException("Windows 通知尚未初始化。");
            if (notifier.Setting != NotificationSetting.Enabled)
                throw new InvalidOperationException($"Windows 通知不可用：{notifier.Setting}");

            var xml = new XmlDocument();
            xml.LoadXml(BuildPayload(title, message, IsSoundEnabled, imagePath));
            notifier.Show(new ToastNotification(xml));
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
        if (_notifier is null) return;
        ToastNotificationManagerCompat.OnActivated -= OnNotificationInvoked;
        _notifier = null;
    }
}
