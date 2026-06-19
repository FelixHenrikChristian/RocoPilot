using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

using RocoPilot.Helpers;
using RocoPilot.ViewModels;
using RocoPilot.Views.Windows;

namespace RocoPilot.Views;

public sealed partial class MainPage : Page
{
    private const double CoverAspectRatio = 5.0 / 2.0;
    private RuntimeRecognitionConfigWindow? _runtimeRecognitionConfigWindow;

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

    private async void ConfigureRuntimeRecognitionButton_Click(object sender, RoutedEventArgs e)
    {
        if (_runtimeRecognitionConfigWindow is not null)
        {
            _runtimeRecognitionConfigWindow.Activate();
            return;
        }

        await ViewModel.LoadRuntimeRecognitionSettingsAsync();
        _runtimeRecognitionConfigWindow = new RuntimeRecognitionConfigWindow(ViewModel);
        _runtimeRecognitionConfigWindow.Closed += (_, _) => _runtimeRecognitionConfigWindow = null;
        WindowPlacementHelper.SetOwner(_runtimeRecognitionConfigWindow, App.MainWindow);
        WindowPlacementHelper.CenterOnParent(_runtimeRecognitionConfigWindow, App.MainWindow);
        _runtimeRecognitionConfigWindow.Activate();
    }
}
