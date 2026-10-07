using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

using RocoPilot.Contracts.Services;
using RocoPilot.Helpers;
using RocoPilot.Views.Windows.AutoBattleConfigPages;

namespace RocoPilot.Views.Windows;

public sealed partial class AutoBattleConfigWindow : WindowEx
{
    private static AutoBattleConfigWindow? _currentWindow;
    private readonly IRuntimeTaskService _runtime;
    private readonly AutoBattleConfigEditor _editor;
    private readonly IReadOnlyDictionary<AutoBattleConfigSection, Page> _pages;

    internal static async Task ShowAsync(AutoBattleConfigSection section)
    {
        var runtime = App.GetService<IRuntimeTaskService>();
        await runtime.LoadSettingsAsync();
        if (_currentWindow is null)
        {
            _currentWindow = new AutoBattleConfigWindow(runtime);
            _currentWindow.Closed += (_, _) => _currentWindow = null;
            WindowPlacementHelper.SetOwner(_currentWindow, App.MainWindow);
            WindowPlacementHelper.CenterOnParent(_currentWindow, App.MainWindow);
        }

        _currentWindow.NavigateTo(section);
        _currentWindow.Activate();
    }

    private AutoBattleConfigWindow(IRuntimeTaskService runtime)
    {
        _runtime = runtime;

        InitializeComponent();

        var themeSelectorService = App.GetService<IThemeSelectorService>();
        ContentRoot.RequestedTheme = themeSelectorService.Theme;

        Title = "战斗配置";
        AppWindow.Title = Title;
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets/WindowIcon.ico"));
        AppWindow.TitleBar.PreferredTheme = TitleBarTheme.UseDefaultAppMode;

        _editor = new AutoBattleConfigEditor(
            _runtime.AutoBattleSettings,
            App.GetService<IKeyboardInputService>());
        _pages = new Dictionary<AutoBattleConfigSection, Page>
        {
            [AutoBattleConfigSection.Normal] = new AutoBattleReleaseConfigPage(_editor, this, AutoBattleConfigSection.Normal),
            [AutoBattleConfigSection.FlowerSeed] = new AutoBattleReleaseConfigPage(_editor, this, AutoBattleConfigSection.FlowerSeed),
            [AutoBattleConfigSection.SharedSequences] = new AutoBattleSharedSequencesPage(_editor),
            [AutoBattleConfigSection.BloodlineCapture] = new AutoBattleBloodlineCaptureConfigPage(_editor)
        };
    }

    internal void ShowMessage(
        string title,
        string message,
        InfoBarSeverity severity = InfoBarSeverity.Warning)
    {
        MessageBar.Title = title;
        MessageBar.Message = message;
        MessageBar.Severity = severity;
        MessageBar.IsOpen = false;
        MessageBar.IsOpen = true;
    }

    private void BattleNavigationView_SelectionChanged(
        NavigationView sender,
        NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItemContainer?.Tag is string tag
            && Enum.TryParse<AutoBattleConfigSection>(tag, out var section))
        {
            NavigateTo(section);
        }
    }

    private void NavigateTo(AutoBattleConfigSection section)
    {
        if (_pages.TryGetValue(section, out var page))
        {
            ConfigPageHost.Content = page;
        }

        BattleNavigationView.SelectedItem = section switch
        {
            AutoBattleConfigSection.Normal => NormalBattleNavigationItem,
            AutoBattleConfigSection.FlowerSeed => FlowerSeedBattleNavigationItem,
            AutoBattleConfigSection.SharedSequences => SharedSequencesNavigationItem,
            AutoBattleConfigSection.BloodlineCapture => BloodlineCaptureNavigationItem,
            _ => NormalBattleNavigationItem
        };
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_editor.TryBuildSettings(
                _runtime.AutoBattleSettings,
                out var settings,
                out var error))
        {
            NavigateTo(error.Section);
            ShowMessage(error.Title, error.Message);
            return;
        }

        _runtime.SetAutoBattleSettings(settings);
        Close();
    }
}
