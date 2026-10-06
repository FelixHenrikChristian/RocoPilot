using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Controls;

using RocoPilot.Contracts.Services;
using RocoPilot.Contracts.Services.TextRecognition;
using RocoPilot.Models.Capture;
using RocoPilot.Models.Runtime;
using RocoPilot.Settings;

namespace RocoPilot.ViewModels;

public partial class MainViewModel : ObservableRecipient
{
    private readonly IRuntimeTaskService _runtimeTaskService;
    private readonly IInfoOverlayService _infoOverlayService;
    private readonly ITextRecognitionService _textRecognitionService;
    private readonly ILogger<MainViewModel> _logger;
    private readonly DispatcherQueue? _dispatcherQueue;
    private bool _isApplyingRuntimeTaskSettings;

    public IReadOnlyList<CaptureMethodOption> CaptureMethods
    {
        get;
    } =
    [
        new(CaptureMethod.WindowsGraphicsCapture, "Windows Graphics Capture", "高性能实时截图"),
        new(CaptureMethod.BitBlt, "BitBlt", "传统 GDI 兼容模式"),
        new(CaptureMethod.PrintWindow, "PrintWindow", "窗口后台截图尝试")
    ];

    public IReadOnlyList<KeyboardInputMethodOption> KeyboardInputMethods { get; } =
    [
        new(KeyboardInputMethod.PostMessage,
            "PostMessage（已失效）",
            "后台窗口消息，可能被游戏屏蔽。"),
        new(KeyboardInputMethod.SendInput,
            "SendInput（已失效）",
            "前台输入，程序权限不能低于游戏。"),
        new(KeyboardInputMethod.Interception,
            "Interception",
            "需安装驱动并重启，游戏需保持前台。")
    ];

    [ObservableProperty]
    public partial CaptureMethodOption? SelectedCaptureMethod { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(KeyboardInputMethodDescription))]
    public partial KeyboardInputMethodOption? SelectedKeyboardInputMethod { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StartStopButtonText))]
    [NotifyPropertyChangedFor(nameof(StartStopButtonGlyph))]
    [NotifyPropertyChangedFor(nameof(IsLaunchConfigurationEnabled))]
    public partial bool IsRealtimeCaptureRunning { get; set; }

    [ObservableProperty]
    public partial bool IsMaskOverlayEnabled { get; set; }

    [ObservableProperty]
    public partial bool IsInfoOverlayEnabled { get; set; }

    [ObservableProperty]
    public partial bool IsInfoOverlayLocked { get; set; }

    [ObservableProperty]
    public partial bool IsLaunchNotificationOpen { get; set; }

    [ObservableProperty]
    public partial InfoBarSeverity LaunchNotificationSeverity { get; set; }

    [ObservableProperty]
    public partial string LaunchNotificationTitle { get; set; }

    [ObservableProperty]
    public partial string LaunchNotificationMessage { get; set; }

    public string StartStopButtonText => IsRealtimeCaptureRunning ? "停止" : "启动";

    public string StartStopButtonGlyph => IsRealtimeCaptureRunning ? "\uF2D9" : "\uE768";

    public bool IsLaunchConfigurationEnabled => !IsRealtimeCaptureRunning;

    public string KeyboardInputMethodDescription => SelectedKeyboardInputMethod?.Description ?? string.Empty;

    public MainViewModel(
        IRuntimeTaskService runtimeTaskService,
        IInfoOverlayService infoOverlayService,
        ITextRecognitionService textRecognitionService,
        ILogger<MainViewModel> logger)
    {
        _runtimeTaskService = runtimeTaskService;
        _infoOverlayService = infoOverlayService;
        _textRecognitionService = textRecognitionService;
        _logger = logger;
        _dispatcherQueue = DispatcherQueue.GetForCurrentThread();
        _runtimeTaskService.SettingsChanged += RuntimeTaskService_SettingsChanged;
        _isApplyingRuntimeTaskSettings = true;
        IsInfoOverlayEnabled = true;
        IsInfoOverlayLocked = true;
        LaunchNotificationSeverity = InfoBarSeverity.Informational;
        LaunchNotificationTitle = string.Empty;
        LaunchNotificationMessage = string.Empty;
        SelectedCaptureMethod = CaptureMethods[0];
        SelectedKeyboardInputMethod = FindKeyboardInputMethod(_runtimeTaskService.AutoBattleSettings.KeyboardInputMethod);
        IsRealtimeCaptureRunning = _runtimeTaskService.IsRunning;
        if (_runtimeTaskService.CurrentState is { } currentState)
        {
            IsMaskOverlayEnabled = currentState.Options.RecognitionOverlayEnabled;
            IsInfoOverlayEnabled = currentState.Options.InfoOverlayEnabled;
            IsInfoOverlayLocked = currentState.Options.InfoOverlayLocked;
        }

        _isApplyingRuntimeTaskSettings = false;
    }

    [RelayCommand]
    private async Task ToggleRealtimeCaptureAsync()
    {
        if (_runtimeTaskService.IsRunning)
        {
            await _runtimeTaskService.StopAsync();
            IsRealtimeCaptureRunning = false;
            _logger.LogDebug("实时任务停止命令已完成");
            ShowLaunchNotification(InfoBarSeverity.Success, "任务已停止", "场景识别和实时任务已停止。");
            return;
        }

        if (SelectedCaptureMethod is null)
        {
            ShowLaunchNotification(
                InfoBarSeverity.Warning,
                "缺少配置",
                "请先选择截图方式。");
            return;
        }

        var recognitionMethod = _textRecognitionService.GetDefaultMethod();
        if (recognitionMethod is not { IsAvailable: true })
        {
            ShowLaunchNotification(
                InfoBarSeverity.Error,
                "OCR 不可用",
                recognitionMethod?.UnavailableReason ?? "默认 OCR 引擎未能加载，请检查应用文件是否完整。");
            return;
        }

        await _runtimeTaskService.LoadSettingsAsync();

        var result = await _runtimeTaskService.StartAsync(new RuntimeTaskStartOptions
        {
            CaptureMethod = SelectedCaptureMethod.Method,
            RecognitionOverlayEnabled = IsMaskOverlayEnabled,
            InfoOverlayEnabled = IsInfoOverlayEnabled,
            InfoOverlayLocked = IsInfoOverlayLocked,
            EncounterStatisticsEnabled = _runtimeTaskService.EncounterStatisticsEnabled,
            AutoBattleSettings = _runtimeTaskService.AutoBattleSettings
        });

        if (!result.Success || result.State is null)
        {
            IsRealtimeCaptureRunning = false;
            _logger.LogWarning("启动失败：{Message}", result.Message);
            ShowLaunchNotification(
                InfoBarSeverity.Error,
                "启动失败",
                result.Message);
            return;
        }

        var gameWindow = result.State.TargetWindow;
        IsRealtimeCaptureRunning = true;
        _logger.LogDebug(
            "启动成功：已找到游戏窗口。标题：{WindowTitle}  进程：{ProcessName}  PID：{ProcessId}  HWND：{WindowHandle}  窗口尺寸：{WindowWidth}x{WindowHeight}  客户区：{ClientWidth}x{ClientHeight}",
            gameWindow.Title,
            gameWindow.ProcessName,
            gameWindow.ProcessId,
            gameWindow.HandleText,
            gameWindow.Width,
            gameWindow.Height,
            gameWindow.ClientWidth,
            gameWindow.ClientHeight);
        ShowLaunchNotification(
            InfoBarSeverity.Success,
            "启动成功",
            result.Message);
    }

    public async Task LoadRuntimeTaskSettingsAsync()
    {
        await _runtimeTaskService.LoadSettingsAsync();
        ApplyRuntimeTaskSettings();
    }

    [RelayCommand]
    private void ResetInfoOverlayPosition()
    {
        _infoOverlayService.ResetPosition();
        ShowLaunchNotification(InfoBarSeverity.Informational, "位置已重置", "信息遮罩窗口将回到默认位置。");
    }

    partial void OnIsMaskOverlayEnabledChanged(bool value)
    {
        if (!_isApplyingRuntimeTaskSettings)
        {
            _runtimeTaskService.SetRecognitionOverlayEnabled(value);
        }
    }

    partial void OnIsInfoOverlayEnabledChanged(bool value)
    {
        if (!_isApplyingRuntimeTaskSettings)
        {
            _runtimeTaskService.SetInfoOverlayEnabled(value);
        }
    }

    partial void OnIsInfoOverlayLockedChanged(bool value)
    {
        if (!_isApplyingRuntimeTaskSettings)
        {
            _runtimeTaskService.SetInfoOverlayLocked(value);
        }
    }

    partial void OnSelectedKeyboardInputMethodChanged(KeyboardInputMethodOption? value)
    {
        if (_isApplyingRuntimeTaskSettings || value is null)
        {
            return;
        }

        var settings = _runtimeTaskService.AutoBattleSettings;
        settings.KeyboardInputMethod = value.Method;
        _runtimeTaskService.SetAutoBattleSettings(settings);
    }

    private void RuntimeTaskService_SettingsChanged(object? sender, EventArgs e)
    {
        if (_dispatcherQueue is null || _dispatcherQueue.HasThreadAccess)
        {
            ApplyRuntimeTaskSettings();
            return;
        }

        _dispatcherQueue.TryEnqueue(ApplyRuntimeTaskSettings);
    }

    private void ApplyRuntimeTaskSettings()
    {
        _isApplyingRuntimeTaskSettings = true;
        try
        {
            SelectedKeyboardInputMethod = FindKeyboardInputMethod(_runtimeTaskService.AutoBattleSettings.KeyboardInputMethod);
            if (_runtimeTaskService.CurrentState is { } state)
            {
                IsMaskOverlayEnabled = state.Options.RecognitionOverlayEnabled;
                IsInfoOverlayEnabled = state.Options.InfoOverlayEnabled;
                IsInfoOverlayLocked = state.Options.InfoOverlayLocked;
            }
        }
        finally
        {
            _isApplyingRuntimeTaskSettings = false;
        }
    }

    private void ShowLaunchNotification(InfoBarSeverity severity, string title, string message)
    {
        LaunchNotificationSeverity = severity;
        LaunchNotificationTitle = title;
        LaunchNotificationMessage = message;
        IsLaunchNotificationOpen = false;
        IsLaunchNotificationOpen = true;
    }

    private KeyboardInputMethodOption FindKeyboardInputMethod(KeyboardInputMethod method)
    {
        return KeyboardInputMethods.First(option => option.Method == method);
    }

}

public sealed record KeyboardInputMethodOption(KeyboardInputMethod Method, string Name, string Description);
