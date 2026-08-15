using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

using RocoPilot.Contracts.Services;
using RocoPilot.ViewModels;
using RocoPilot.Views.Windows.AutoBattleConfigPages;

using Windows.Graphics;

namespace RocoPilot.Views.Windows;

public sealed partial class AutoBattleConfigWindow : WindowEx
{
    private readonly RealtimeViewModel _viewModel;
    private readonly AutoBattleConfigEditor _editor;
    private readonly IReadOnlyDictionary<AutoBattleConfigSection, Page> _pages;

    public AutoBattleConfigWindow(RealtimeViewModel viewModel)
    {
        _viewModel = viewModel;

        InitializeComponent();

        var themeSelectorService = App.GetService<IThemeSelectorService>();
        ContentRoot.RequestedTheme = themeSelectorService.Theme;

        Title = "自动战斗配置";
        AppWindow.Title = Title;
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets/WindowIcon.ico"));
        AppWindow.TitleBar.PreferredTheme = TitleBarTheme.UseDefaultAppMode;
        AppWindow.Resize(new SizeInt32(1040, 760));

        _editor = new AutoBattleConfigEditor(
            _viewModel.AutoBattleSettings,
            App.GetService<IKeyboardInputService>());
        _pages = new Dictionary<AutoBattleConfigSection, Page>
        {
            [AutoBattleConfigSection.Normal] = new AutoBattleNormalConfigPage(_editor, this),
            [AutoBattleConfigSection.SharedSequences] = new AutoBattleSharedSequencesPage(_editor),
            [AutoBattleConfigSection.BloodlineCapture] = new AutoBattleBloodlineCaptureConfigPage(_editor)
        };

        BattleNavigationView.SelectedItem = SharedSequencesNavigationItem;
        NavigateTo(AutoBattleConfigSection.SharedSequences);
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
                _viewModel.AutoBattleSettings,
                out var settings,
                out var error))
        {
            NavigateTo(error.Section);
            ShowMessage(error.Title, error.Message);
            return;
        }

        _viewModel.UpdateAutoBattleSettings(settings);
        Close();
    }
}
