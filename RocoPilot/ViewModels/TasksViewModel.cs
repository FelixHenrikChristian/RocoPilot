using System.Collections.ObjectModel;

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
    [NotifyPropertyChangedFor(nameof(LegendaryChallengeButtonText))]
    [NotifyPropertyChangedFor(nameof(LegendaryChallengeButtonGlyph))]
    [NotifyPropertyChangedFor(nameof(IsLegendaryChallengeConfigurationEnabled))]
    [NotifyPropertyChangedFor(nameof(IsLegendaryChallengeButtonEnabled))]
    public partial bool IsLegendaryChallengeRunning { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FlowerSeedChallengeButtonText))]
    [NotifyPropertyChangedFor(nameof(FlowerSeedChallengeButtonGlyph))]
    [NotifyPropertyChangedFor(nameof(IsFlowerSeedChallengeButtonEnabled))]
    public partial bool IsFlowerSeedChallengeRunning { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FlowerSeedScanButtonLabel))]
    [NotifyPropertyChangedFor(nameof(FlowerSeedScanButtonGlyph))]
    [NotifyPropertyChangedFor(nameof(IsFlowerSeedScanButtonEnabled))]
    public partial bool IsFlowerSeedScanning { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LegendaryScanButtonLabel))]
    [NotifyPropertyChangedFor(nameof(LegendaryScanButtonGlyph))]
    [NotifyPropertyChangedFor(nameof(IsLegendaryScanButtonEnabled))]
    public partial bool IsLegendaryScanning { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ThrowBallButtonText))]
    [NotifyPropertyChangedFor(nameof(ThrowBallButtonGlyph))]
    [NotifyPropertyChangedFor(nameof(IsThrowBallButtonEnabled))]
    public partial bool IsThrowBallRunning { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsFlowerSeedConfigurationEnabled))]
    [NotifyPropertyChangedFor(nameof(IsFlowerSeedTargetSelectionEnabled))]
    [NotifyPropertyChangedFor(nameof(IsFlowerSeedChallengeButtonEnabled))]
    [NotifyPropertyChangedFor(nameof(IsFlowerSeedScanButtonEnabled))]
    [NotifyPropertyChangedFor(nameof(IsLegendaryChallengeConfigurationEnabled))]
    [NotifyPropertyChangedFor(nameof(IsLegendaryTargetSelectionEnabled))]
    [NotifyPropertyChangedFor(nameof(IsLegendaryChallengeButtonEnabled))]
    [NotifyPropertyChangedFor(nameof(IsLegendaryScanButtonEnabled))]
    [NotifyPropertyChangedFor(nameof(IsThrowBallConfigurationEnabled))]
    [NotifyPropertyChangedFor(nameof(IsThrowBallButtonEnabled))]
    public partial bool IsIndependentTaskRunning { get; set; }

    [ObservableProperty]
    public partial string SelectedLegendaryTarget { get; set; }

    [ObservableProperty]
    public partial bool UseLegendaryMarkWhenInsufficient { get; set; }

    public ObservableCollection<string> LegendaryTargets { get; } = new(IndependentTaskSettings.LegendaryTargets);
    public IReadOnlyList<string> LegendaryDifficulties { get; } = ["难度I", "难度II", "难度III"];

    [ObservableProperty]
    public partial int SelectedLegendaryDifficultyIndex { get; set; }

    [ObservableProperty]
    public partial FlowerSeedOption? SelectedFlowerSeed { get; set; }

    public ObservableCollection<FlowerSeedOption> FlowerSeedOptions { get; } = [];

    [ObservableProperty]
    public partial double LegendaryChallengeRunCount { get; set; }

    [ObservableProperty]
    public partial double FlowerSeedRunCount { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsFlowerSeedTargetSelectionEnabled))]
    [NotifyPropertyChangedFor(nameof(IsFlowerSeedScanButtonEnabled))]
    public partial bool FlowerSeedAtTargetLocation { get; set; }

    [ObservableProperty]
    public partial bool FlowerSeedPreferCondensedMark { get; set; } = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsLegendaryTargetSelectionEnabled))]
    [NotifyPropertyChangedFor(nameof(IsLegendaryScanButtonEnabled))]
    public partial bool LegendaryAtTargetLocation { get; set; }

    [ObservableProperty]
    public partial double ThrowBallHoldMinMs { get; set; }

    [ObservableProperty]
    public partial double ThrowBallHoldMaxMs { get; set; }

    [ObservableProperty]
    public partial double ThrowBallIntervalMinMs { get; set; }

    [ObservableProperty]
    public partial double ThrowBallIntervalMaxMs { get; set; }

    [ObservableProperty]
    public partial double ThrowBallDurationMinutes { get; set; }

    [ObservableProperty]
    public partial bool IsTaskNotificationOpen { get; set; }

    [ObservableProperty]
    public partial InfoBarSeverity TaskNotificationSeverity { get; set; }

    [ObservableProperty]
    public partial string TaskNotificationTitle { get; set; }

    [ObservableProperty]
    public partial string TaskNotificationMessage { get; set; }

    public string LegendaryChallengeButtonText => IsLegendaryChallengeRunning ? StopButtonText : StartButtonText;

    public string LegendaryChallengeButtonGlyph => IsLegendaryChallengeRunning ? StopButtonGlyph : StartButtonGlyph;

    public bool IsLegendaryChallengeConfigurationEnabled => !IsIndependentTaskRunning;

    public bool IsLegendaryTargetSelectionEnabled => IsLegendaryChallengeConfigurationEnabled && !LegendaryAtTargetLocation;

    public bool IsLegendaryChallengeButtonEnabled => IsLegendaryChallengeRunning || !IsIndependentTaskRunning;

    public string FlowerSeedChallengeButtonText => IsFlowerSeedChallengeRunning ? StopButtonText : StartButtonText;

    public string FlowerSeedChallengeButtonGlyph => IsFlowerSeedChallengeRunning ? StopButtonGlyph : StartButtonGlyph;

    public string FlowerSeedScanButtonLabel => IsFlowerSeedScanning ? "停止扫描" : "扫描花种";

    public string FlowerSeedScanButtonGlyph => IsFlowerSeedScanning ? StopButtonGlyph : "\uE72C";

    public bool IsFlowerSeedConfigurationEnabled => !IsIndependentTaskRunning;

    public bool IsFlowerSeedTargetSelectionEnabled => IsFlowerSeedConfigurationEnabled && !FlowerSeedAtTargetLocation;

    public bool IsFlowerSeedChallengeButtonEnabled => IsFlowerSeedChallengeRunning
        || !IsIndependentTaskRunning;

    public bool IsFlowerSeedScanButtonEnabled => IsFlowerSeedScanning || IsFlowerSeedTargetSelectionEnabled;

    public string LegendaryScanButtonLabel => IsLegendaryScanning ? "停止扫描" : "刷新传说精灵（仅本次启动有效）";

    public string LegendaryScanButtonGlyph => IsLegendaryScanning ? StopButtonGlyph : "\uE72C";

    public bool IsLegendaryScanButtonEnabled => IsLegendaryScanning || IsLegendaryTargetSelectionEnabled;

    public string ThrowBallButtonText => IsThrowBallRunning ? StopButtonText : StartButtonText;

    public string ThrowBallButtonGlyph => IsThrowBallRunning ? StopButtonGlyph : StartButtonGlyph;

    public bool IsThrowBallConfigurationEnabled => !IsIndependentTaskRunning;

    public bool IsThrowBallButtonEnabled => IsThrowBallRunning || !IsIndependentTaskRunning;

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
        LegendaryChallengeRunCount = IndependentTaskSettings.DefaultLegendaryChallengeRunCount;
        SelectedLegendaryTarget = LegendaryTargets[0];
        SelectedLegendaryDifficultyIndex = IndependentTaskSettings.DefaultLegendaryDifficulty - 1;
        UseLegendaryMarkWhenInsufficient = true;
        FlowerSeedRunCount = IndependentTaskSettings.DefaultFlowerSeedRunCount;
        ThrowBallHoldMinMs = IndependentTaskSettings.DefaultThrowBallHoldMinMs;
        ThrowBallHoldMaxMs = IndependentTaskSettings.DefaultThrowBallHoldMaxMs;
        ThrowBallIntervalMinMs = IndependentTaskSettings.DefaultThrowBallIntervalMinMs;
        ThrowBallIntervalMaxMs = IndependentTaskSettings.DefaultThrowBallIntervalMaxMs;
        ThrowBallDurationMinutes = IndependentTaskSettings.DefaultThrowBallDurationMinutes;
        _independentTaskService.StateChanged += IndependentTaskService_StateChanged;
        SyncRunningState();
    }

    public async Task LoadAsync()
    {
        await _independentTaskService.LoadSettingsAsync();
        _hasLoadedSettings = true;
        SyncRunningState();
    }

    [RelayCommand]
    private Task ToggleLegendaryChallengeAsync()
    {
        return ToggleTaskAsync(IndependentTaskKind.LegendaryChallenge);
    }

    [RelayCommand]
    private Task ToggleFlowerSeedChallengeAsync()
    {
        return ToggleTaskAsync(IndependentTaskKind.FlowerSeedChallenge);
    }

    [RelayCommand]
    private Task ToggleFlowerSeedScanAsync()
    {
        return ToggleTaskAsync(IndependentTaskKind.FlowerSeedScan);
    }

    [RelayCommand]
    private Task ToggleLegendaryScanAsync()
    {
        return ToggleTaskAsync(IndependentTaskKind.LegendaryScan);
    }

    [RelayCommand]
    private Task ToggleThrowBallAsync()
    {
        return ToggleTaskAsync(IndependentTaskKind.ThrowBall);
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
            _logger.LogError("任务启动失败：{Message}", result.Message);
            ShowTaskNotification(InfoBarSeverity.Error, "启动失败", result.Message);
            return;
        }

        ShowTaskNotification(InfoBarSeverity.Success, "启动成功", result.Message);
    }

    partial void OnLegendaryChallengeRunCountChanged(double value)
    {
        SaveSettingsIfLoaded();
    }

    partial void OnSelectedLegendaryTargetChanged(string value) => SaveSettingsIfLoaded();
    partial void OnSelectedLegendaryDifficultyIndexChanged(int value) => SaveSettingsIfLoaded();
    partial void OnUseLegendaryMarkWhenInsufficientChanged(bool value) => SaveSettingsIfLoaded();
    partial void OnFlowerSeedAtTargetLocationChanged(bool value) => SaveSettingsIfLoaded();
    partial void OnFlowerSeedPreferCondensedMarkChanged(bool value) => SaveSettingsIfLoaded();
    partial void OnLegendaryAtTargetLocationChanged(bool value) => SaveSettingsIfLoaded();

    partial void OnThrowBallHoldMinMsChanged(double value) => SaveSettingsIfLoaded();
    partial void OnThrowBallHoldMaxMsChanged(double value) => SaveSettingsIfLoaded();
    partial void OnThrowBallIntervalMinMsChanged(double value) => SaveSettingsIfLoaded();
    partial void OnThrowBallIntervalMaxMsChanged(double value) => SaveSettingsIfLoaded();
    partial void OnThrowBallDurationMinutesChanged(double value) => SaveSettingsIfLoaded();

    partial void OnFlowerSeedRunCountChanged(double value)
    {
        SaveSettingsIfLoaded();
    }

    partial void OnSelectedFlowerSeedChanged(FlowerSeedOption? value)
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
            LegendaryChallengeRunCount = ToRunCount(
                LegendaryChallengeRunCount,
                IndependentTaskSettings.DefaultLegendaryChallengeRunCount),
            LegendaryTargetName = SelectedLegendaryTarget,
            LegendaryDifficulty = SelectedLegendaryDifficultyIndex + 1,
            UseLegendaryMarkWhenInsufficient = UseLegendaryMarkWhenInsufficient,
            FlowerSeedRunCount = ToRunCount(
                FlowerSeedRunCount,
                IndependentTaskSettings.DefaultFlowerSeedRunCount),
            FlowerSeedTargetNumber = SelectedFlowerSeed?.Number ?? 0,
            FlowerSeedAtTargetLocation = FlowerSeedAtTargetLocation,
            FlowerSeedPreferCondensedMark = FlowerSeedPreferCondensedMark,
            LegendaryAtTargetLocation = LegendaryAtTargetLocation,
            ThrowBallHoldMinMs = ToThrowBallMs(ThrowBallHoldMinMs, IndependentTaskSettings.DefaultThrowBallHoldMinMs),
            ThrowBallHoldMaxMs = ToThrowBallMs(ThrowBallHoldMaxMs, IndependentTaskSettings.DefaultThrowBallHoldMaxMs),
            ThrowBallIntervalMinMs = ToThrowBallMs(ThrowBallIntervalMinMs, IndependentTaskSettings.DefaultThrowBallIntervalMinMs),
            ThrowBallIntervalMaxMs = ToThrowBallMs(ThrowBallIntervalMaxMs, IndependentTaskSettings.DefaultThrowBallIntervalMaxMs),
            ThrowBallDurationMinutes = ToRunCount(ThrowBallDurationMinutes, IndependentTaskSettings.DefaultThrowBallDurationMinutes),
            FlowerSeedOptions = [.. FlowerSeedOptions]
        };
    }

    private void ApplySettings(IndependentTaskSettings settings)
    {
        _isApplyingSettings = true;
        try
        {
            LegendaryChallengeRunCount = settings.LegendaryChallengeRunCount;
            if (!LegendaryTargets.SequenceEqual(_independentTaskService.LegendaryTargets))
            {
                LegendaryTargets.Clear();
                foreach (var name in _independentTaskService.LegendaryTargets) LegendaryTargets.Add(name);
            }
            SelectedLegendaryTarget = settings.LegendaryTargetName;
            SelectedLegendaryDifficultyIndex = settings.LegendaryDifficulty - 1;
            UseLegendaryMarkWhenInsufficient = settings.UseLegendaryMarkWhenInsufficient;
            FlowerSeedRunCount = settings.FlowerSeedRunCount;
            FlowerSeedAtTargetLocation = settings.FlowerSeedAtTargetLocation;
            FlowerSeedPreferCondensedMark = settings.FlowerSeedPreferCondensedMark;
            LegendaryAtTargetLocation = settings.LegendaryAtTargetLocation;
            ThrowBallHoldMinMs = settings.ThrowBallHoldMinMs;
            ThrowBallHoldMaxMs = settings.ThrowBallHoldMaxMs;
            ThrowBallIntervalMinMs = settings.ThrowBallIntervalMinMs;
            ThrowBallIntervalMaxMs = settings.ThrowBallIntervalMaxMs;
            ThrowBallDurationMinutes = settings.ThrowBallDurationMinutes;
            if (!FlowerSeedOptions.SequenceEqual(settings.FlowerSeedOptions))
            {
                FlowerSeedOptions.Clear();
                foreach (var option in settings.FlowerSeedOptions)
                {
                    FlowerSeedOptions.Add(option);
                }
            }

            SelectedFlowerSeed = FlowerSeedOptions.FirstOrDefault(
                option => option.Number == settings.FlowerSeedTargetNumber);
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
        IsLegendaryChallengeRunning = runningTaskKind == IndependentTaskKind.LegendaryChallenge;
        IsFlowerSeedChallengeRunning = runningTaskKind == IndependentTaskKind.FlowerSeedChallenge;
        IsFlowerSeedScanning = runningTaskKind == IndependentTaskKind.FlowerSeedScan;
        IsLegendaryScanning = runningTaskKind == IndependentTaskKind.LegendaryScan;
        IsThrowBallRunning = runningTaskKind == IndependentTaskKind.ThrowBall;
        IsIndependentTaskRunning = _independentTaskService.IsRunning;
        ApplySettings(_independentTaskService.Settings);
    }

    private void ShowTaskNotification(InfoBarSeverity severity, string title, string message)
    {
        TaskNotificationSeverity = severity;
        TaskNotificationTitle = title;
        TaskNotificationMessage = message;
        IsTaskNotificationOpen = false;
        IsTaskNotificationOpen = true;
    }

    internal static int ToRunCount(double value, int fallback)
    {
        return double.IsNaN(value) ? fallback : (int)Math.Round(value);
    }

    private static int ToThrowBallMs(double value, int fallback)
    {
        // 数值越界交给 IndependentTaskSettings.Normalize 统一钳制与排序。
        return double.IsNaN(value) ? fallback : Math.Max(0, (int)Math.Round(value));
    }
}
