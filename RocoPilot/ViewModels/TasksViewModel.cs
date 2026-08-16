using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Controls;

using RocoPilot.Contracts.Services;
using RocoPilot.Models.Runtime;
using RocoPilot.Services;

namespace RocoPilot.ViewModels;

public partial class TasksViewModel : ObservableRecipient
{
    private const string StopButtonText = "停止";
    private const string StartButtonText = "启动";

    // Segoe Fluent Icons：Stop（U+F2D9）与 Play（U+E768），与首页启动按钮一致。
    private static readonly string StopButtonGlyph = char.ConvertFromUtf32(0xF2D9);
    private static readonly string StartButtonGlyph = char.ConvertFromUtf32(0xE768);

    private readonly IIndependentTaskService _independentTaskService;
    private readonly ILogger<TasksViewModel> _logger;
    private readonly DispatcherQueue? _dispatcherQueue;
    private bool _hasLoadedSettings;
    private bool _isApplyingSettings;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(BossBattleButtonText))]
    [NotifyPropertyChangedFor(nameof(BossBattleButtonGlyph))]
    [NotifyPropertyChangedFor(nameof(IsBossBattleConfigurationEnabled))]
    public partial bool IsBossBattleRunning { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LegendaryChallengeButtonText))]
    [NotifyPropertyChangedFor(nameof(LegendaryChallengeButtonGlyph))]
    [NotifyPropertyChangedFor(nameof(IsLegendaryChallengeConfigurationEnabled))]
    public partial bool IsLegendaryChallengeRunning { get; set; }

    [ObservableProperty]
    public partial double BossBattleRunCount { get; set; }

    [ObservableProperty]
    public partial double LegendaryChallengeRunCount { get; set; }

    [ObservableProperty]
    public partial bool IsTaskNotificationOpen { get; set; }

    [ObservableProperty]
    public partial InfoBarSeverity TaskNotificationSeverity { get; set; }

    [ObservableProperty]
    public partial string TaskNotificationTitle { get; set; }

    [ObservableProperty]
    public partial string TaskNotificationMessage { get; set; }

    public string BossBattleButtonText => IsBossBattleRunning ? StopButtonText : StartButtonText;

    public string BossBattleButtonGlyph => IsBossBattleRunning ? StopButtonGlyph : StartButtonGlyph;

    public bool IsBossBattleConfigurationEnabled => !IsBossBattleRunning;

    public string LegendaryChallengeButtonText => IsLegendaryChallengeRunning ? StopButtonText : StartButtonText;

    public string LegendaryChallengeButtonGlyph => IsLegendaryChallengeRunning ? StopButtonGlyph : StartButtonGlyph;

    public bool IsLegendaryChallengeConfigurationEnabled => !IsLegendaryChallengeRunning;

    public TasksViewModel(
        IIndependentTaskService independentTaskService,
        ILogger<TasksViewModel> logger)
    {
        _independentTaskService = independentTaskService;
        _logger = logger;
        _dispatcherQueue = DispatcherQueue.GetForCurrentThread();
        TaskNotificationSeverity = InfoBarSeverity.Informational;
        TaskNotificationTitle = string.Empty;
        TaskNotificationMessage = string.Empty;
        BossBattleRunCount = IndependentTaskSettings.DefaultBossBattleRunCount;
        LegendaryChallengeRunCount = IndependentTaskSettings.DefaultLegendaryChallengeRunCount;
        _independentTaskService.StateChanged += IndependentTaskService_StateChanged;
        SyncRunningState();
    }

    public async Task LoadAsync()
    {
        await _independentTaskService.LoadSettingsAsync();
        ApplySettings(_independentTaskService.Settings);
        _hasLoadedSettings = true;
        SyncRunningState();
    }

    [RelayCommand]
    private Task ToggleBossBattleAsync()
    {
        return ToggleTaskAsync(IndependentTaskKind.BossBattle);
    }

    [RelayCommand]
    private Task ToggleLegendaryChallengeAsync()
    {
        return ToggleTaskAsync(IndependentTaskKind.LegendaryChallenge);
    }

    private async Task ToggleTaskAsync(IndependentTaskKind kind)
    {
        var taskDisplayName = IndependentTaskService.GetTaskDisplayName(kind);
        if (_independentTaskService.RunningTaskKind == kind)
        {
            await _independentTaskService.StopAsync();
            SyncRunningState();
            ShowTaskNotification(
                InfoBarSeverity.Success,
                "任务已停止",
                $"{taskDisplayName}任务已停止。");
            return;
        }

        SaveSettings();
        var result = await _independentTaskService.StartAsync(kind);
        SyncRunningState();
        if (!result.Success)
        {
            _logger.LogWarning("独立任务启动失败：{Message}", result.Message);
            ShowTaskNotification(InfoBarSeverity.Error, "启动失败", result.Message);
            return;
        }

        ShowTaskNotification(InfoBarSeverity.Success, "启动成功", result.Message);
    }

    partial void OnBossBattleRunCountChanged(double value)
    {
        SaveSettingsIfLoaded();
    }

    partial void OnLegendaryChallengeRunCountChanged(double value)
    {
        SaveSettingsIfLoaded();
    }

    private void SaveSettingsIfLoaded()
    {
        if (_hasLoadedSettings && !_isApplyingSettings)
        {
            SaveSettings();
        }
    }

    private void SaveSettings()
    {
        _independentTaskService.SetSettings(BuildSettings());
    }

    private IndependentTaskSettings BuildSettings()
    {
        return new IndependentTaskSettings
        {
            BossBattleRunCount = ToRunCount(
                BossBattleRunCount,
                IndependentTaskSettings.DefaultBossBattleRunCount),
            LegendaryChallengeRunCount = ToRunCount(
                LegendaryChallengeRunCount,
                IndependentTaskSettings.DefaultLegendaryChallengeRunCount)
        }.Normalize();
    }

    private void ApplySettings(IndependentTaskSettings settings)
    {
        _isApplyingSettings = true;
        try
        {
            BossBattleRunCount = settings.BossBattleRunCount;
            LegendaryChallengeRunCount = settings.LegendaryChallengeRunCount;
        }
        finally
        {
            _isApplyingSettings = false;
        }
    }

    private void IndependentTaskService_StateChanged(object? sender, EventArgs e)
    {
        if (_dispatcherQueue is null || _dispatcherQueue.HasThreadAccess)
        {
            SyncRunningState();
            return;
        }

        _dispatcherQueue.TryEnqueue(SyncRunningState);
    }

    private void SyncRunningState()
    {
        var runningTaskKind = _independentTaskService.RunningTaskKind;
        IsBossBattleRunning = runningTaskKind == IndependentTaskKind.BossBattle;
        IsLegendaryChallengeRunning = runningTaskKind == IndependentTaskKind.LegendaryChallenge;
    }

    private void ShowTaskNotification(InfoBarSeverity severity, string title, string message)
    {
        TaskNotificationSeverity = severity;
        TaskNotificationTitle = title;
        TaskNotificationMessage = message;
        IsTaskNotificationOpen = false;
        IsTaskNotificationOpen = true;
    }

    private static int ToRunCount(double value, int fallback)
    {
        return double.IsNaN(value) ? fallback : (int)Math.Round(value);
    }
}
