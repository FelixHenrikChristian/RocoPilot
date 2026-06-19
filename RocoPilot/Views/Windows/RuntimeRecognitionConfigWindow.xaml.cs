using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

using RocoPilot.Contracts.Services;
using RocoPilot.Models.Runtime;
using RocoPilot.ViewModels;

using Windows.Graphics;

namespace RocoPilot.Views.Windows;

public sealed partial class RuntimeRecognitionConfigWindow : WindowEx
{
    private readonly MainViewModel _viewModel;

    public RuntimeRecognitionConfigWindow(MainViewModel viewModel)
    {
        _viewModel = viewModel;
        InitializeComponent();

        ContentRoot.RequestedTheme = App.GetService<IThemeSelectorService>().Theme;
        Title = "运行频率配置";
        AppWindow.Title = Title;
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets/WindowIcon.ico"));
        AppWindow.TitleBar.PreferredTheme = TitleBarTheme.UseDefaultAppMode;
        AppWindow.Resize(new SizeInt32(720, 520));

        LoadSettings(_viewModel.RuntimeRecognitionSettings);
    }

    private void LoadSettings(RuntimeRecognitionSettings settings)
    {
        FrameCaptureIntervalNumberBox.Value = settings.FrameCaptureIntervalMs;
        GameStateScanIntervalNumberBox.Value = settings.GameStateScanIntervalMs;
        OcrScanIntervalNumberBox.Value = settings.OcrScanIntervalMs;
    }

    private void RestoreDefaultsButton_Click(object sender, RoutedEventArgs e)
    {
        LoadSettings(RuntimeRecognitionSettings.CreateDefault());
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        if (!TryGetMilliseconds(FrameCaptureIntervalNumberBox, "画面捕获间隔", out var frameCaptureIntervalMs)
            || !TryGetMilliseconds(GameStateScanIntervalNumberBox, "状态识别间隔", out var gameStateScanIntervalMs)
            || !TryGetMilliseconds(OcrScanIntervalNumberBox, "OCR 识别间隔", out var ocrScanIntervalMs))
        {
            return;
        }

        _viewModel.UpdateRuntimeRecognitionSettings(new RuntimeRecognitionSettings
        {
            FrameCaptureIntervalMs = frameCaptureIntervalMs,
            GameStateScanIntervalMs = gameStateScanIntervalMs,
            OcrScanIntervalMs = ocrScanIntervalMs
        });
        Close();
    }

    private bool TryGetMilliseconds(NumberBox numberBox, string name, out int value)
    {
        value = 0;
        if (double.IsNaN(numberBox.Value)
            || numberBox.Value < numberBox.Minimum
            || numberBox.Value > numberBox.Maximum
            || numberBox.Value != Math.Truncate(numberBox.Value))
        {
            MessageBar.Title = "配置值无效";
            MessageBar.Message = $"{name}需要填写 {numberBox.Minimum:0} 到 {numberBox.Maximum:0} 之间的整数。";
            MessageBar.Severity = InfoBarSeverity.Warning;
            MessageBar.IsOpen = false;
            MessageBar.IsOpen = true;
            return false;
        }

        value = (int)Math.Round(numberBox.Value, MidpointRounding.AwayFromZero);
        return true;
    }
}
