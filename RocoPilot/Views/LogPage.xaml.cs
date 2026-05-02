using Microsoft.UI.Xaml.Controls;

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
}
