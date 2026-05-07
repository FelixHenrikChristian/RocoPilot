using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml.Controls;

using RocoPilot.Contracts.Services;
using RocoPilot.Contracts.Services.TextRecognition;
using RocoPilot.Models.Capture;
using RocoPilot.Models.Runtime;
using RocoPilot.Models.TextRecognition;

namespace RocoPilot.ViewModels;

public partial class MainViewModel : ObservableRecipient
{
    private const int LaunchNotificationAutoCloseDelayMilliseconds = 4500;

    private readonly IRuntimeTaskService _runtimeTaskService;
    private readonly IInfoOverlayService _infoOverlayService;
    private readonly ITextRecognitionService _textRecognitionService;
    private readonly ILogger<MainViewModel> _logger;
    private CancellationTokenSource? _launchNotificationAutoCloseCts;

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

    [ObservableProperty]
    private CaptureMethodOption? _selectedCaptureMethod;

    [ObservableProperty]
    private TextRecognitionMethodOption? _selectedTextRecognitionMethod;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StartStopButtonText))]
    [NotifyPropertyChangedFor(nameof(StartStopButtonGlyph))]
    private bool _isRealtimeCaptureRunning;

    [ObservableProperty]
    private bool _isMaskOverlayEnabled = true;

    [ObservableProperty]
    private bool _isInfoOverlayEnabled = true;

    [ObservableProperty]
    private bool _isInfoOverlayLocked = true;

    [ObservableProperty]
    private CaptureTargetWindow? _targetGameWindow;

    [ObservableProperty]
    private bool _isLaunchNotificationOpen;

    [ObservableProperty]
    private InfoBarSeverity _launchNotificationSeverity = InfoBarSeverity.Informational;

    [ObservableProperty]
    private string _launchNotificationTitle = string.Empty;

    [ObservableProperty]
    private string _launchNotificationMessage = string.Empty;

    public string StartStopButtonText => IsRealtimeCaptureRunning ? "停止" : "启动";

    public string StartStopButtonGlyph => IsRealtimeCaptureRunning ? "\uE71A" : "\uE768";

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
        TextRecognitionMethods = _textRecognitionService.GetMethods();
        SelectedCaptureMethod = CaptureMethods[0];
        SelectedTextRecognitionMethod = GetInitialTextRecognitionMethod();
        IsRealtimeCaptureRunning = _runtimeTaskService.IsRunning;
        TargetGameWindow = _runtimeTaskService.CurrentState?.TargetWindow;
    }

    [RelayCommand]
    private async Task ToggleRealtimeCaptureAsync()
    {
        if (_runtimeTaskService.IsRunning)
        {
            await _runtimeTaskService.StopAsync();
            IsRealtimeCaptureRunning = false;
            TargetGameWindow = null;
            _logger.LogInformation("实时任务已停止");
            ShowLaunchNotification(InfoBarSeverity.Success, "任务已停止", "实时任务已停止。");
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
            PollutionCounterEnabled = true,
            EncounterStatisticsEnabled = _runtimeTaskService.EncounterStatisticsEnabled,
            AutoBattleEnabled = false
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
        _logger.LogInformation(
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

    [RelayCommand]
    private void ResetInfoOverlayPosition()
    {
        _infoOverlayService.ResetPosition();
        ShowLaunchNotification(InfoBarSeverity.Informational, "位置已重置", "信息遮罩窗口将回到默认位置。");
    }

    partial void OnIsInfoOverlayLockedChanged(bool value)
    {
        _infoOverlayService.SetLocked(value);
    }

    private void ShowLaunchNotification(InfoBarSeverity severity, string title, string message)
    {
        _launchNotificationAutoCloseCts?.Cancel();
        _launchNotificationAutoCloseCts?.Dispose();
        _launchNotificationAutoCloseCts = new CancellationTokenSource();

        LaunchNotificationSeverity = severity;
        LaunchNotificationTitle = title;
        LaunchNotificationMessage = message;
        IsLaunchNotificationOpen = false;
        IsLaunchNotificationOpen = true;

        _ = AutoCloseLaunchNotificationAsync(_launchNotificationAutoCloseCts.Token);
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

    private async Task AutoCloseLaunchNotificationAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(LaunchNotificationAutoCloseDelayMilliseconds, cancellationToken);
            IsLaunchNotificationOpen = false;
        }
        catch (OperationCanceledException)
        {
        }
    }
}
