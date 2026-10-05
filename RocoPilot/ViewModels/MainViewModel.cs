using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Controls;

using RocoPilot.Contracts.Services;
using RocoPilot.Contracts.Services.TextRecognition;
using RocoPilot.Models.Capture;
using RocoPilot.Models.Runtime;
using RocoPilot.Models.TextRecognition;
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

    public IReadOnlyList<TextRecognitionMethodOption> TextRecognitionMethods
    {
        get;
    }

    public IReadOnlyList<KeyboardInputMethodOption> KeyboardInputMethods { get; } =
    [
        new(KeyboardInputMethod.PostMessage,
            "PostMessage（已失效）",
            "旧的后台窗口消息方式；不要求游戏前台，但可能被游戏屏蔽。"),
        new(KeyboardInputMethod.SendInput,
            "SendInput（已失效）",
            "扫描码输入，类似 pydirectinput；需要游戏窗口前台，权限不能低于游戏。"),
        new(KeyboardInputMethod.Interception,
            "Interception",
            "驱动级键盘输入；需要安装 Interception 驱动并重启，游戏窗口需处于前台。")
    ];

    [ObservableProperty]
    public partial CaptureMethodOption? SelectedCaptureMethod { get; set; }

    [ObservableProperty]
    public partial TextRecognitionMethodOption? SelectedTextRecognitionMethod { get; set; }

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
    public partial CaptureTargetWindow? TargetGameWindow { get; set; }

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

    public RuntimeRecognitionSettings RuntimeRecognitionSettings =>
        _runtimeTaskService.RuntimeRecognitionSettings;

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
        TextRecognitionMethods = _textRecognitionService.GetMethods();
        _isApplyingRuntimeTaskSettings = true;
        IsInfoOverlayEnabled = true;
        IsInfoOverlayLocked = true;
        LaunchNotificationSeverity = InfoBarSeverity.Informational;
        LaunchNotificationTitle = string.Empty;
        LaunchNotificationMessage = string.Empty;
        SelectedCaptureMethod = CaptureMethods[0];
        SelectedTextRecognitionMethod = GetInitialTextRecognitionMethod();
        SelectedKeyboardInputMethod = FindKeyboardInputMethod(_runtimeTaskService.AutoBattleSettings.KeyboardInputMethod);
        IsRealtimeCaptureRunning = _runtimeTaskService.IsRunning;
        TargetGameWindow = _runtimeTaskService.CurrentState?.TargetWindow;
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
            TargetGameWindow = null;
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

        if (SelectedTextRecognitionMethod is null)
        {
            ShowLaunchNotification(
                InfoBarSeverity.Warning,
                "缺少配置",
                "请先选择 OCR 识别方法。");
            return;
        }

        if (!SelectedTextRecognitionMethod.IsAvailable)
        {
            ShowLaunchNotification(
                InfoBarSeverity.Warning,
                "OCR 不可用",
                SelectedTextRecognitionMethod.UnavailableReason ?? "当前 OCR 识别方法不可用。");
            return;
        }

        await _runtimeTaskService.LoadSettingsAsync();

        var result = await _runtimeTaskService.StartAsync(new RuntimeTaskStartOptions
        {
            CaptureMethod = SelectedCaptureMethod.Method,
            TextRecognitionMethod = SelectedTextRecognitionMethod.Method,
            RecognitionOverlayEnabled = IsMaskOverlayEnabled,
            InfoOverlayEnabled = IsInfoOverlayEnabled,
            InfoOverlayLocked = IsInfoOverlayLocked,
            EncounterStatisticsEnabled = _runtimeTaskService.EncounterStatisticsEnabled,
            AutoBattleSettings = _runtimeTaskService.AutoBattleSettings
        });

        if (!result.Success || result.State is null)
        {
            IsRealtimeCaptureRunning = false;
            TargetGameWindow = null;
            _logger.LogWarning("启动失败：{Message}", result.Message);
            ShowLaunchNotification(
                InfoBarSeverity.Error,
                "启动失败",
                result.Message);
            return;
        }

        var gameWindow = result.State.TargetWindow;
        TargetGameWindow = gameWindow;
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

    public void UpdateRuntimeRecognitionSettings(RuntimeRecognitionSettings settings)
    {
        _runtimeTaskService.SetRuntimeRecognitionSettings(settings);
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

    private TextRecognitionMethodOption? GetInitialTextRecognitionMethod()
    {
        if (_runtimeTaskService.CurrentState is not null)
        {
            var runningMethod = TextRecognitionMethods.FirstOrDefault(
                method => method.Method == _runtimeTaskService.CurrentState.Options.TextRecognitionMethod);
            if (runningMethod is not null)
            {
                return runningMethod;
            }
        }

        return TextRecognitionMethods.FirstOrDefault(method => method.IsAvailable)
            ?? TextRecognitionMethods.FirstOrDefault();
    }

    private KeyboardInputMethodOption FindKeyboardInputMethod(KeyboardInputMethod method)
    {
        return KeyboardInputMethods.First(option => option.Method == method);
    }

}

public sealed record KeyboardInputMethodOption(KeyboardInputMethod Method, string Name, string Description);
