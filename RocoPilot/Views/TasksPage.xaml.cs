using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

using RocoPilot.Controls;
using RocoPilot.Models.Runtime;
using RocoPilot.ViewModels;
using RocoPilot.Views.Windows;
using RocoPilot.Views.Windows.AutoBattleConfigPages;

namespace RocoPilot.Views;

public sealed partial class TasksPage : Page
{
    public TasksViewModel ViewModel
    {
        get;
    }

    public TasksPage()
    {
        ViewModel = App.GetService<TasksViewModel>();
        InitializeComponent();
        Loaded += TasksPage_Loaded;
    }

    private async void TasksPage_Loaded(object sender, RoutedEventArgs e)
    {
        Loaded -= TasksPage_Loaded;
        await ViewModel.LoadAsync();
    }

    private void RootGrid_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        // 点击 NumberBox 以外的区域时把焦点移回页面，收起 Compact 步进按钮浮层；
        // 可聚焦控件（按钮、下拉框等）随后会自行接管焦点。
        if (e.OriginalSource is DependencyObject source && IsInsideNumberBox(source))
        {
            return;
        }

        Focus(FocusState.Programmatic);
    }

    private static bool IsInsideNumberBox(DependencyObject source)
    {
        for (var current = source; current is not null; current = VisualTreeHelper.GetParent(current))
        {
            if (current is NumberBox)
            {
                return true;
            }
        }

        return false;
    }

    private async void ConfigureFlowerSeedBattleButton_Click(object sender, RoutedEventArgs e)
    {
        await AutoBattleConfigWindow.ShowAsync(AutoBattleConfigSection.FlowerSeed);
    }

    private async void ToggleFlowerSeedChallengeButton_Click(object sender, RoutedEventArgs e)
    {
        if (!ViewModel.IsFlowerSeedChallengeRunning && !ViewModel.FlowerSeedAtTargetLocation
            && ViewModel.SelectedFlowerSeed is null)
        {
            await new AppContentDialog
            {
                XamlRoot = XamlRoot,
                Title = "无法启动花种挑战",
                TitleGlyph = DialogGlyphs.Warning,
                Tone = AppDialogTone.Caution,
                Content = new TextBlock
                {
                    Text = "请先点击目标花种右侧的刷新按钮扫描花种，并选择要挑战的目标。",
                    TextWrapping = TextWrapping.Wrap,
                    MaxWidth = 360
                },
                CloseButtonText = "确定",
                DefaultButton = ContentDialogButton.Close
            }.ShowAsync();
            return;
        }

        await ViewModel.ToggleFlowerSeedChallengeCommand.ExecuteAsync(null);
    }

    private async void ConfigureLegendaryBattleButton_Click(object sender, RoutedEventArgs e)
    {
        await AutoBattleConfigWindow.ShowAsync(AutoBattleConfigSection.Legendary);
    }

    private async void ConfigureThrowBallButton_Click(object sender, RoutedEventArgs e)
    {
        NumberBox CreateMsBox(string header, double value, int maximum) => new()
        {
            Header = header,
            Width = 110,
            Minimum = IndependentTaskSettings.MinimumThrowBallMs,
            Maximum = maximum,
            SmallChange = 100,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact,
            Value = value
        };

        var holdMin = CreateMsBox("最短", ViewModel.ThrowBallHoldMinMs, IndependentTaskSettings.MaximumThrowBallHoldMs);
        var holdMax = CreateMsBox("最长", ViewModel.ThrowBallHoldMaxMs, IndependentTaskSettings.MaximumThrowBallHoldMs);
        var intervalMin = CreateMsBox("最短", ViewModel.ThrowBallIntervalMinMs, IndependentTaskSettings.MaximumThrowBallIntervalMs);
        var intervalMax = CreateMsBox("最长", ViewModel.ThrowBallIntervalMaxMs, IndependentTaskSettings.MaximumThrowBallIntervalMs);

        static StackPanel RangeRow(NumberBox min, NumberBox max) => new()
        {
            Orientation = Orientation.Horizontal,
            Spacing = 12,
            Children = { min, max }
        };

        var content = new StackPanel
        {
            Width = Math.Min(360, Math.Max(280, XamlRoot.Size.Width - 96)),
            Spacing = 12,
            Children =
            {
                new TextBlock { Text = "按住左键时长（毫秒）", FontWeight = FontWeights.SemiBold },
                RangeRow(holdMin, holdMax),
                new TextBlock { Text = "丢球间隔（毫秒）", FontWeight = FontWeights.SemiBold },
                RangeRow(intervalMin, intervalMax)
            }
        };

        // 与主页面同构：内容外包一层可聚焦容器充当“页面”，
        // 让空白点击的焦点落到它上面而不是被弹窗转交给首个 NumberBox，
        // NumberBox 失去焦点后箭头浮层随之收起。
        var focusSink = new ContentControl
        {
            IsTabStop = true,
            Content = content
        };

        var dialog = new AppContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "自动丢球配置",
            TitleGlyph = DialogGlyphs.Edit,
            Content = focusSink,
            PrimaryButtonText = "确定",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary
        };

        // 点击 NumberBox 以外的区域时把焦点移到 focusSink，收起 Compact 步进按钮浮层；
        // 可聚焦控件（按钮等）随后会自行接管焦点。
        dialog.PointerPressed += (sender, e) =>
        {
            if (e.OriginalSource is DependencyObject source && IsInsideNumberBox(source))
            {
                return;
            }

            focusSink.Focus(FocusState.Programmatic);
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        var holdMinValue = (int)Math.Round(holdMin.Value);
        var holdMaxValue = (int)Math.Round(holdMax.Value);
        var intervalMinValue = (int)Math.Round(intervalMin.Value);
        var intervalMaxValue = (int)Math.Round(intervalMax.Value);
        if (holdMinValue > holdMaxValue) (holdMinValue, holdMaxValue) = (holdMaxValue, holdMinValue);
        if (intervalMinValue > intervalMaxValue) (intervalMinValue, intervalMaxValue) = (intervalMaxValue, intervalMinValue);

        ViewModel.ThrowBallHoldMinMs = holdMinValue;
        ViewModel.ThrowBallHoldMaxMs = holdMaxValue;
        ViewModel.ThrowBallIntervalMinMs = intervalMinValue;
        ViewModel.ThrowBallIntervalMaxMs = intervalMaxValue;
    }
}
