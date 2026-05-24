using Microsoft.UI.Xaml;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

using RocoPilot.Contracts.Services;
using RocoPilot.Helpers;
using RocoPilot.Models;
using RocoPilot.ViewModels;

using Windows.System;

namespace RocoPilot.Views;

// TODO: Update NavigationViewItem titles and icons in ShellPage.xaml.
public sealed partial class ShellPage : Page
{
    public ShellViewModel ViewModel
    {
        get;
    }

    private readonly IUpdateService _updateService;
    private readonly ILogger<ShellPage> _logger;
    private bool _isStartupUpdateCheckStarted;

    public ShellPage(
        ShellViewModel viewModel,
        IUpdateService updateService,
        ILogger<ShellPage> logger)
    {
        ViewModel = viewModel;
        _updateService = updateService;
        _logger = logger;
        InitializeComponent();
#if DEBUG
        AddDebugNavigationItems();
#endif

        ViewModel.NavigationService.Frame = NavigationFrame;
        ViewModel.NavigationViewService.Initialize(NavigationViewControl);

        // TODO: Set the title bar icon by updating /Assets/WindowIcon.ico.
        // A custom title bar is required for full window theme and Mica support.
        // https://docs.microsoft.com/windows/apps/develop/title-bar?tabs=winui3#full-customization
        App.MainWindow.ExtendsContentIntoTitleBar = true;
        App.MainWindow.SetTitleBar(AppTitleBar);
        App.MainWindow.Activated += MainWindow_Activated;
        AppTitleBarText.Text = "AppDisplayName".GetLocalized();

        NavigationViewControl.Loaded += NavigationViewControl_Loaded;
    }

#if DEBUG
    private void AddDebugNavigationItems()
    {
        var testItem = new NavigationViewItem
        {
            Content = "测试",
            Icon = new FontIcon { Glyph = "\uF196" }
        };
        NavigationHelper.SetNavigateTo(testItem, typeof(TestViewModel).FullName!);

        NavigationViewControl.MenuItems.Add(testItem);
    }
#endif

    private void NavigationViewControl_Loaded(object sender, RoutedEventArgs e)
    {
        NavigationViewControl.Loaded -= NavigationViewControl_Loaded;

        if (NavigationViewControl.SettingsItem is NavigationViewItem settingsItem)
        {
            settingsItem.Content = "设置";
            AutomationProperties.SetName(settingsItem, "设置");
        }

        _ = CheckForUpdatesOnStartupAsync();
    }

    private void OnLoaded(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        TitleBarHelper.UpdateTitleBar(RequestedTheme);

        KeyboardAccelerators.Add(BuildKeyboardAccelerator(VirtualKey.Left, VirtualKeyModifiers.Menu));
        KeyboardAccelerators.Add(BuildKeyboardAccelerator(VirtualKey.GoBack));
    }

    private void MainWindow_Activated(object sender, WindowActivatedEventArgs args)
    {
        App.AppTitlebar = AppTitleBarText as UIElement;
    }

    private void NavigationViewControl_DisplayModeChanged(NavigationView sender, NavigationViewDisplayModeChangedEventArgs args)
    {
        AppTitleBar.Margin = new Thickness()
        {
            Left = sender.CompactPaneLength * (sender.DisplayMode == NavigationViewDisplayMode.Minimal ? 2 : 1),
            Top = AppTitleBar.Margin.Top,
            Right = AppTitleBar.Margin.Right,
            Bottom = AppTitleBar.Margin.Bottom
        };
    }

    private static KeyboardAccelerator BuildKeyboardAccelerator(VirtualKey key, VirtualKeyModifiers? modifiers = null)
    {
        var keyboardAccelerator = new KeyboardAccelerator() { Key = key };

        if (modifiers.HasValue)
        {
            keyboardAccelerator.Modifiers = modifiers.Value;
        }

        keyboardAccelerator.Invoked += OnKeyboardAcceleratorInvoked;

        return keyboardAccelerator;
    }

    private static void OnKeyboardAcceleratorInvoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        var navigationService = App.GetService<INavigationService>();

        var result = navigationService.GoBack();

        args.Handled = result;
    }

    private async Task CheckForUpdatesOnStartupAsync()
    {
        if (_isStartupUpdateCheckStarted)
        {
            return;
        }

        _isStartupUpdateCheckStarted = true;

        try
        {
            var result = await _updateService.CheckUpdateAsync(new UpdateOption { Trigger = UpdateTrigger.Auto });
            switch (result.Status)
            {
                case UpdateCheckStatus.UpToDate:
                    ShowStartupUpdateInfoBar(
                        InfoBarSeverity.Success,
                        "已是最新版本",
                        string.IsNullOrWhiteSpace(result.Message) ? "当前已是最新版本。" : result.Message);
                    break;

                case UpdateCheckStatus.Failed:
                    ShowStartupUpdateInfoBar(
                        InfoBarSeverity.Warning,
                        "检查更新失败",
                        string.IsNullOrWhiteSpace(result.Message) ? "请检查网络连接后重试。" : result.Message);
                    break;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "启动时自动检查更新失败");
            ShowStartupUpdateInfoBar(InfoBarSeverity.Warning, "检查更新失败", "请检查网络连接后重试。");
        }
    }

    private void ShowStartupUpdateInfoBar(InfoBarSeverity severity, string title, string message)
    {
        StartupUpdateInfoBar.Severity = severity;
        StartupUpdateInfoBar.Title = title;
        StartupUpdateInfoBar.Message = message;
        StartupUpdateInfoBar.IsOpen = false;
        StartupUpdateInfoBar.IsOpen = true;
    }
}
