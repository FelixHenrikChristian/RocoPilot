using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

using RocoPilot.Contracts.Services;
using RocoPilot.ViewModels;

namespace RocoPilot.Views;

public sealed partial class MainPage : Page
{
    private const double CoverAspectRatio = 5.0 / 2.0;
    private readonly IInterceptionDriverService _interceptionDriverService;
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

    private string GetEncounterPanelAction(bool isVisible) => isVisible ? "隐藏奇遇计数面板" : "显示奇遇计数面板";

    // x:Load 不能用 ! 取反的 x:Bind 表达式（XamlCompiler 解析报 token recognition error），改由方法返回。
    private bool IsEncounterPanelHidden(bool isVisible) => !isVisible;

    private string GetLockGlyph(bool isLocked) => isLocked ? "\uE72E" : "\uE785";

    private string GetLockAction(bool isLocked) => isLocked ? "解锁位置" : "锁定位置";

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoaded;
        await ViewModel.LoadRuntimeTaskSettingsAsync();
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
}
