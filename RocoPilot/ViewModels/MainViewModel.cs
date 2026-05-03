using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using RocoPilot.Models.Capture;

namespace RocoPilot.ViewModels;

public partial class MainViewModel : ObservableRecipient
{
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

    public string StartStopButtonText => IsRealtimeCaptureRunning ? "停止" : "启动";

    public string StartStopButtonGlyph => IsRealtimeCaptureRunning ? "\uE71A" : "\uE768";

    public MainViewModel()
    {
        SelectedCaptureMethod = CaptureMethods[0];
    }

    [RelayCommand]
    private void ToggleRealtimeCapture()
    {
        IsRealtimeCaptureRunning = !IsRealtimeCaptureRunning;
    }

    [RelayCommand]
    private void ResetInfoOverlayPosition()
    {
    }
}
