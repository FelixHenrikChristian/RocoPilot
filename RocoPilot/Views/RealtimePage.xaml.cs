using Microsoft.UI.Xaml.Controls;

using RocoPilot.ViewModels;

namespace RocoPilot.Views;

public sealed partial class RealtimePage : Page
{
    public RealtimeViewModel ViewModel
    {
        get;
    }

    public RealtimePage()
    {
        ViewModel = App.GetService<RealtimeViewModel>();
        InitializeComponent();
    }
}
