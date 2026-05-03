using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml.Controls;

using RocoPilot.Contracts.Services;
using RocoPilot.Models.Capture;

namespace RocoPilot.ViewModels;

public partial class MainViewModel : ObservableRecipient
{
    private const int LaunchNotificationAutoCloseDelayMilliseconds = 4500;

    private readonly IGameWindowService _gameWindowService;
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

    [ObservableProperty]
    private CaptureMethodOption? _selectedCaptureMethod;

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
        IGameWindowService gameWindowService,
        ILogger<MainViewModel> logger)
    {
        _gameWindowService = gameWindowService;
        _logger = logger;
        SelectedCaptureMethod = CaptureMethods[0];
    }

    [RelayCommand]
    private void ToggleRealtimeCapture()
    {
        if (IsRealtimeCaptureRunning)
        {
            IsRealtimeCaptureRunning = false;
            TargetGameWindow = null;
            _logger.LogInformation("实时任务已停止");
            ShowLaunchNotification(InfoBarSeverity.Success, "任务已停止", "实时任务已停止。");
            return;
        }

        var gameWindow = _gameWindowService.FindGameWindow();
        if (gameWindow == null)
        {
            _logger.LogWarning("启动失败：未找到游戏窗口。目标进程：{TargetProcessName}", _gameWindowService.TargetProcessName);
            ShowLaunchNotification(
                InfoBarSeverity.Error,
                "启动失败",
                "没有找到游戏窗口。请先启动游戏，并确认窗口未最小化。");
            return;
        }

        TargetGameWindow = gameWindow;
        IsRealtimeCaptureRunning = true;
        _logger.LogInformation(
            "启动成功：已找到游戏窗口。标题：{WindowTitle}  进程：{ProcessName}  PID：{ProcessId}  HWND：{WindowHandle}  尺寸：{WindowWidth}x{WindowHeight}",
            gameWindow.Title,
            gameWindow.ProcessName,
            gameWindow.ProcessId,
            gameWindow.HandleText,
            gameWindow.Width,
            gameWindow.Height);
        ShowLaunchNotification(
            InfoBarSeverity.Success,
            "启动成功",
            "实时任务已启动。");
    }

    [RelayCommand]
    private void ResetInfoOverlayPosition()
    {
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
