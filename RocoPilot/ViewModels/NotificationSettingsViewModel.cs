using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml.Controls;
using RocoPilot.Contracts.Services;

namespace RocoPilot.ViewModels;

public partial class NotificationSettingsViewModel : ObservableRecipient
{
    private readonly IAppNotificationService _notifications;
    private readonly ILogger<NotificationSettingsViewModel> _logger;
    private readonly bool _initialized;

    [ObservableProperty]
    public partial bool NotificationEnabled { get; set; }

    [ObservableProperty]
    public partial bool NotificationSoundEnabled { get; set; }

    [ObservableProperty]
    public partial bool IsStatusOpen { get; set; }

    [ObservableProperty]
    public partial string StatusMessage { get; set; }

    [ObservableProperty]
    public partial InfoBarSeverity StatusSeverity { get; set; }

    public NotificationSettingsViewModel(IAppNotificationService notifications, ILogger<NotificationSettingsViewModel> logger)
    {
        _notifications = notifications;
        _logger = logger;
        NotificationEnabled = notifications.IsEnabled;
        NotificationSoundEnabled = notifications.IsSoundEnabled;
        StatusMessage = string.Empty;
        _initialized = true;
    }

    partial void OnNotificationEnabledChanged(bool value)
    {
        if (_initialized) _ = SaveAsync(_notifications.SetEnabledAsync(value));
    }

    partial void OnNotificationSoundEnabledChanged(bool value)
    {
        if (_initialized) _ = SaveAsync(_notifications.SetSoundEnabledAsync(value));
    }

    private async Task SaveAsync(Task save)
    {
        try
        {
            await save;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "保存通知设置失败。");
            ShowStatus(InfoBarSeverity.Error, "保存失败，当前设置已生效，但重启后可能恢复原值。");
        }
    }

    [RelayCommand]
    private async Task TestNotificationAsync()
    {
        if (!NotificationEnabled) return;
        IsStatusOpen = false;
        var sent = await _notifications.ShowAsync("测试通知", "这是一条来自 RocoPilot 的测试通知。");
        if (!sent)
            ShowStatus(InfoBarSeverity.Error,
                _notifications.LastError ?? "Windows 通知发送失败，请查看日志中的具体原因。");
    }

    private void ShowStatus(InfoBarSeverity severity, string message)
    {
        StatusSeverity = severity;
        StatusMessage = message;
        IsStatusOpen = false;
        IsStatusOpen = true;
    }
}
