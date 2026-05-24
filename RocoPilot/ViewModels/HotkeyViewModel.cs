using System.Collections.ObjectModel;

using CommunityToolkit.Mvvm.ComponentModel;

using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Controls;

using RocoPilot.Contracts.Services;
using RocoPilot.Models.Hotkeys;

namespace RocoPilot.ViewModels;

public class HotkeyViewModel : ObservableRecipient
{
    private readonly IHotkeyService _hotkeyService;
    private readonly DispatcherQueue? _dispatcherQueue;
    private HotkeyBindingItemViewModel? _capturingItem;
    private bool _isNotificationOpen;
    private InfoBarSeverity _notificationSeverity = InfoBarSeverity.Informational;
    private string _notificationTitle = string.Empty;
    private string _notificationMessage = string.Empty;
    private bool _hasLoadedSettings;

    public ObservableCollection<HotkeyBindingItemViewModel> Items
    {
        get;
    }

    public IReadOnlyList<HotkeyGroupViewModel> Groups
    {
        get;
    }

    public bool IsNotificationOpen
    {
        get => _isNotificationOpen;
        set => SetProperty(ref _isNotificationOpen, value);
    }

    public InfoBarSeverity NotificationSeverity
    {
        get => _notificationSeverity;
        set => SetProperty(ref _notificationSeverity, value);
    }

    public string NotificationTitle
    {
        get => _notificationTitle;
        set => SetProperty(ref _notificationTitle, value);
    }

    public string NotificationMessage
    {
        get => _notificationMessage;
        set => SetProperty(ref _notificationMessage, value);
    }

    public bool IsCapturing => _capturingItem is not null;

    public HotkeyViewModel(IHotkeyService hotkeyService)
    {
        var items = HotkeyActionDescriptor.CreateDefault()
            .Select(descriptor => new HotkeyBindingItemViewModel(descriptor))
            .ToList();

        Items = new(items);
        Groups =
        [
            new(
                "遮罩窗口",
                "\uEABC",
                items.Where(item => item.Action
                    is HotkeyAction.ToggleInfoOverlay).ToList()),
            new(
                "实时任务",
                "\uE916",
                items.Where(item => item.Action
                    is HotkeyAction.ToggleEncounterStatistics
                    or HotkeyAction.ToggleAutoBattle).ToList())
        ];

        _hotkeyService = hotkeyService;
        _dispatcherQueue = DispatcherQueue.GetForCurrentThread();
        _hotkeyService.SettingsChanged += HotkeyService_SettingsChanged;
    }

    public async Task LoadAsync()
    {
        if (_hasLoadedSettings)
        {
            return;
        }

        await _hotkeyService.LoadSettingsAsync();
        ApplySettings(_hotkeyService.Settings);
        _hasLoadedSettings = true;
    }

    public void BeginCapture(HotkeyBindingItemViewModel item)
    {
        if (_capturingItem is not null)
        {
            _capturingItem.IsCapturing = false;
        }

        _capturingItem = item;
        item.IsCapturing = true;
        OnPropertyChanged(nameof(IsCapturing));
    }

    public async Task ClearCapturingBindingAsync()
    {
        var item = _capturingItem;
        if (item is null)
        {
            return;
        }

        var result = await TrySetBindingAsync(item, null);
        if (result is null)
        {
            FinishCapture();
            return;
        }

        FinishCapture();
        ShowNotification(InfoBarSeverity.Informational, "已清除热键", $"{item.Name} 不再绑定快捷键。");
    }

    public async Task SetCapturingBindingAsync(HotkeyBinding binding)
    {
        var item = _capturingItem;
        if (item is null)
        {
            return;
        }

        var result = await TrySetBindingAsync(item, binding);
        if (result is null)
        {
            FinishCapture();
            return;
        }

        FinishCapture();

        var message = result.ReplacedAction is { } replacedAction
            ? $"已绑定 {binding.DisplayText}，并移除 {GetActionName(replacedAction)} 的重复绑定。"
            : $"已绑定 {binding.DisplayText}。";
        ShowNotification(InfoBarSeverity.Success, "热键已更新", message);
    }

    public void ShowCaptureError(string message)
    {
        ShowNotification(InfoBarSeverity.Warning, "无法绑定热键", message);
    }

    private async Task<HotkeyBindingUpdateResult?> TrySetBindingAsync(
        HotkeyBindingItemViewModel item,
        HotkeyBinding? binding)
    {
        try
        {
            var result = await _hotkeyService.SetBindingAsync(item.Action, binding);
            ApplySettings(_hotkeyService.Settings);
            return result;
        }
        catch (Exception ex)
        {
            ShowNotification(InfoBarSeverity.Error, "保存热键失败", ex.Message);
            return null;
        }
    }

    private void FinishCapture()
    {
        if (_capturingItem is not null)
        {
            _capturingItem.IsCapturing = false;
            _capturingItem = null;
            OnPropertyChanged(nameof(IsCapturing));
        }
    }

    private void ApplySettings(HotkeySettings settings)
    {
        foreach (var item in Items)
        {
            item.Binding = settings.GetBinding(item.Action);
        }
    }

    private string GetActionName(HotkeyAction action)
    {
        return Items.FirstOrDefault(item => item.Action == action)?.Name ?? "其他功能";
    }

    private void ShowNotification(InfoBarSeverity severity, string title, string message)
    {
        NotificationSeverity = severity;
        NotificationTitle = title;
        NotificationMessage = message;
        IsNotificationOpen = false;
        IsNotificationOpen = true;
    }

    private void HotkeyService_SettingsChanged(object? sender, EventArgs e)
    {
        if (_dispatcherQueue is null || _dispatcherQueue.HasThreadAccess)
        {
            ApplySettings(_hotkeyService.Settings);
            return;
        }

        _dispatcherQueue.TryEnqueue(() => ApplySettings(_hotkeyService.Settings));
    }
}
