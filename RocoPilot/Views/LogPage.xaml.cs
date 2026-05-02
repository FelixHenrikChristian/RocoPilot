using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

using RocoPilot.ViewModels;

namespace RocoPilot.Views;

public sealed partial class LogPage : Page
{
    public LogViewModel ViewModel
    {
        get;
    }

    public LogPage()
    {
        ViewModel = App.GetService<LogViewModel>();
        InitializeComponent();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        ViewModel.Attach(DispatcherQueue);
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        ViewModel.Detach();
        base.OnNavigatedFrom(e);
    }
}
