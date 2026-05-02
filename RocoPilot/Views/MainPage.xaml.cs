using Microsoft.UI.Xaml.Controls;

using RocoPilot.ViewModels;

namespace RocoPilot.Views;

public sealed partial class MainPage : Page
{
    public MainViewModel ViewModel
    {
        get;
    }

    public MainPage()
    {
        ViewModel = App.GetService<MainViewModel>();
        InitializeComponent();
    }
}
