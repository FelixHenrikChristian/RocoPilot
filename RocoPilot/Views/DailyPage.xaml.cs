using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

using RocoPilot.ViewModels;

namespace RocoPilot.Views;

public sealed partial class DailyPage : Page
{
    public DailyViewModel ViewModel { get; } = App.GetService<DailyViewModel>();

    public TasksViewModel Challenges { get; } = App.GetService<TasksViewModel>();

    public DailyPage()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += (_, _) => ViewModel.NoticeRequested -= ShowNotice;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        ViewModel.NoticeRequested += ShowNotice;
        await Challenges.LoadAsync();
        await ViewModel.LoadAsync();
    }

    private void ShowNotice(object? sender, string message)
    {
        DailyNotice.IsOpen = false;
        DailyNotice.Message = message;
        DailyNotice.IsOpen = true;
    }
}
