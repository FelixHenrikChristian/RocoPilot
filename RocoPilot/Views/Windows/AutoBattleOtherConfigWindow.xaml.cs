using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

using RocoPilot.Contracts.Services;
using RocoPilot.Models.Runtime;
using RocoPilot.ViewModels;

using Windows.Graphics;

namespace RocoPilot.Views.Windows;

public sealed partial class AutoBattleOtherConfigWindow : WindowEx
{
    private readonly RealtimeViewModel _viewModel;
    private readonly IThemeSelectorService _themeSelectorService;

    public AutoBattleOtherConfigWindow(RealtimeViewModel viewModel)
    {
        _viewModel = viewModel;
        _themeSelectorService = App.GetService<IThemeSelectorService>();

        InitializeComponent();

        ContentRoot.RequestedTheme = _themeSelectorService.Theme;
        Title = "自动战斗其他配置";
        AppWindow.Title = Title;
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets/WindowIcon.ico"));
        AppWindow.TitleBar.PreferredTheme = TitleBarTheme.UseDefaultAppMode;
        AppWindow.Resize(new SizeInt32(720, 600));

        LoadSettings(_viewModel.AutoBattleSettings);
    }

    private void LoadSettings(AutoBattleSettings settings)
    {
        SkillSelectionActionDelayNumberBox.Value = settings.SkillSelectionActionDelayMs;
        SkillSelectionRetryDelayNumberBox.Value = settings.SkillSelectionRetryDelayMs;
        KeyboardHoldDurationNumberBox.Value = settings.KeyboardHoldDurationMs;
        KeyboardIntervalNumberBox.Value = settings.KeyboardIntervalMs;
        CaptureKeyboardIntervalNumberBox.Value = settings.CaptureKeyboardIntervalMs;
    }

    private void RestoreDefaultsButton_Click(object sender, RoutedEventArgs e)
    {
        LoadSettings(AutoBattleSettings.CreateDefault());
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        if (!TryGetMilliseconds(SkillSelectionActionDelayNumberBox, "技能选择基础等待", out var actionDelayMs)
            || !TryGetMilliseconds(SkillSelectionRetryDelayNumberBox, "技能操作重试间隔", out var retryDelayMs)
            || !TryGetMilliseconds(KeyboardHoldDurationNumberBox, "按键按下时长", out var holdDurationMs)
            || !TryGetMilliseconds(KeyboardIntervalNumberBox, "普通按键间隔", out var keyboardIntervalMs)
            || !TryGetMilliseconds(CaptureKeyboardIntervalNumberBox, "捕捉序列按键间隔", out var captureIntervalMs))
        {
            return;
        }

        var settings = _viewModel.AutoBattleSettings.Clone();
        settings.SkillSelectionActionDelayMs = actionDelayMs;
        settings.SkillSelectionRetryDelayMs = retryDelayMs;
        settings.KeyboardHoldDurationMs = holdDurationMs;
        settings.KeyboardIntervalMs = keyboardIntervalMs;
        settings.CaptureKeyboardIntervalMs = captureIntervalMs;
        _viewModel.UpdateAutoBattleSettings(settings);
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
            ShowMessage(
                "配置值无效",
                $"{name}需要填写 {numberBox.Minimum:0} 到 {numberBox.Maximum:0} 之间的整数。",
                InfoBarSeverity.Warning);
            return false;
        }

        value = (int)Math.Round(numberBox.Value, MidpointRounding.AwayFromZero);
        return true;
    }

    private void ShowMessage(string title, string message, InfoBarSeverity severity)
    {
        MessageBar.Title = title;
        MessageBar.Message = message;
        MessageBar.Severity = severity;
        MessageBar.IsOpen = false;
        MessageBar.IsOpen = true;
    }
}
