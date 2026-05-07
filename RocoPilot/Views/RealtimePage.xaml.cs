using Microsoft.UI.Xaml;
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
        Loaded += RealtimePage_Loaded;
    }

    private async void RealtimePage_Loaded(object sender, RoutedEventArgs e)
    {
        Loaded -= RealtimePage_Loaded;
        await ViewModel.LoadAsync();
    }
}
