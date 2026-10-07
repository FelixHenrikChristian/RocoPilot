using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

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
}
