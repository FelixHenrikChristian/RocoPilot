using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

using RocoPilot.Controls;
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

    private async void ConfigureFlowerSeedBattleButton_Click(object sender, RoutedEventArgs e)
    {
        await AutoBattleConfigWindow.ShowAsync(AutoBattleConfigSection.FlowerSeed);
    }

    private async void ToggleFlowerSeedChallengeButton_Click(object sender, RoutedEventArgs e)
    {
        if (!ViewModel.IsFlowerSeedChallengeRunning && ViewModel.SelectedFlowerSeed is null)
        {
            await new AppContentDialog
            {
                XamlRoot = XamlRoot,
                Title = "无法启动花种挑战",
                Content = "请先点击目标花种右侧的刷新按钮扫描花种，并选择要挑战的目标。",
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
}
