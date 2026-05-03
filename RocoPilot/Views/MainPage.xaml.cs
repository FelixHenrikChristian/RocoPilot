using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

using RocoPilot.ViewModels;

namespace RocoPilot.Views;

public sealed partial class MainPage : Page
{
    private const double CoverAspectRatio = 5.0 / 2.0;

    public MainViewModel ViewModel
    {
        get;
    }

    public MainPage()
    {
        ViewModel = App.GetService<MainViewModel>();
        InitializeComponent();
    }

    private void CoverContainer_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (e.NewSize.Width <= 0)
        {
            return;
        }

        var targetHeight = e.NewSize.Width / CoverAspectRatio;
        if (double.IsNaN(CoverContainer.Height) || Math.Abs(CoverContainer.Height - targetHeight) > 0.5)
        {
            CoverContainer.Height = targetHeight;
        }
    }
}
