using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

using RocoPilot.Contracts.Services;
using RocoPilot.Helpers;
using RocoPilot.ViewModels;
using RocoPilot.Views.Windows;

namespace RocoPilot.Views;

public sealed partial class MainPage : Page
{
    private const double CoverAspectRatio = 5.0 / 2.0;
    private readonly IInterceptionDriverService _interceptionDriverService;
    private RuntimeRecognitionConfigWindow? _runtimeRecognitionConfigWindow;
    private KeyboardInputMethodOption? _confirmedKeyboardInputMethod;
    private bool _isKeyboardInputMethodSelectionReady;
    private bool _isRestoringKeyboardInputMethodSelection;

    public MainViewModel ViewModel
    {
        get;
    }

    public MainPage()
    {
        ViewModel = App.GetService<MainViewModel>();
        _interceptionDriverService = App.GetService<IInterceptionDriverService>();
        InitializeComponent();
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoaded;
        await ViewModel.LoadRuntimeTaskSettingsAsync();
        await ViewModel.LoadImageMatchAlgorithmAsync();
        _confirmedKeyboardInputMethod = ViewModel.SelectedKeyboardInputMethod;
        _isKeyboardInputMethodSelectionReady = true;
    }

    private async void KeyboardInputMethodComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_isKeyboardInputMethodSelectionReady
            || _isRestoringKeyboardInputMethodSelection
            || KeyboardInputMethodComboBox.SelectedItem is not KeyboardInputMethodOption selectedOption)
        {
            return;
        }

        if (selectedOption.Method != KeyboardInputMethod.Interception
            || _interceptionDriverService.IsDriverInstalled())
        {
            _confirmedKeyboardInputMethod = selectedOption;
            return;
        }

        var fallbackOption = _confirmedKeyboardInputMethod?.Method == KeyboardInputMethod.Interception
            ? ViewModel.KeyboardInputMethods.First(option => option.Method == KeyboardInputMethod.PostMessage)
            : _confirmedKeyboardInputMethod;

        KeyboardInputMethodComboBox.IsEnabled = false;
        try
        {
            var installed = await InterceptionDriverInstallDialog.EnsureInstalledAsync(
                XamlRoot,
                _interceptionDriverService);

            if (installed)
            {
                _confirmedKeyboardInputMethod = selectedOption;
                return;
            }

            _isRestoringKeyboardInputMethodSelection = true;
            try
            {
                ViewModel.SelectedKeyboardInputMethod = fallbackOption;
                _confirmedKeyboardInputMethod = fallbackOption;
            }
            finally
            {
                _isRestoringKeyboardInputMethodSelection = false;
            }
        }
        finally
        {
            KeyboardInputMethodComboBox.IsEnabled = ViewModel.IsLaunchConfigurationEnabled;
        }
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

        await ViewModel.LoadRuntimeTaskSettingsAsync();
        _runtimeRecognitionConfigWindow = new RuntimeRecognitionConfigWindow(ViewModel);
        _runtimeRecognitionConfigWindow.Closed += (_, _) => _runtimeRecognitionConfigWindow = null;
        WindowPlacementHelper.SetOwner(_runtimeRecognitionConfigWindow, App.MainWindow);
        WindowPlacementHelper.CenterOnParent(_runtimeRecognitionConfigWindow, App.MainWindow);
        _runtimeRecognitionConfigWindow.Activate();
    }
}
