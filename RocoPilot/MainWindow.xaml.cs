using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;

using RocoPilot.Contracts.Services;
using RocoPilot.Helpers;

using Windows.UI.ViewManagement;

namespace RocoPilot;

public sealed partial class MainWindow : WindowEx
{
    private Microsoft.UI.Dispatching.DispatcherQueue? dispatcherQueue;

    private UISettings? settings;
    private bool _isShutdownStarted;
    private bool _isShutdownComplete;

    public MainWindow()
    {
        InitializeComponent();

        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets/WindowIcon.ico"));
        Content = null;
        Title = "AppDisplayName".GetLocalized();

        // Theme change code picked from https://github.com/microsoft/WinUI-Gallery/pull/1239
        dispatcherQueue = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
        settings = new UISettings();
        settings.ColorValuesChanged += Settings_ColorValuesChanged; // cannot use FrameworkElement.ActualThemeChanged event
        AppWindow.Closing += AppWindow_Closing;
        Closed += MainWindow_Closed;
    }

    private async void AppWindow_Closing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_isShutdownComplete)
        {
            return;
        }

        args.Cancel = true;
        if (_isShutdownStarted)
        {
            return;
        }

        _isShutdownStarted = true;
        await ShutdownAsync();
        _isShutdownComplete = true;
        Close();
    }

    private async Task ShutdownAsync()
    {
        UnsubscribeThemeEvents();

        try
        {
            var runtimeTaskService = App.GetService<IRuntimeTaskService>();
            if (runtimeTaskService.IsRunning)
            {
                await runtimeTaskService.StopAsync();
            }
        }
        catch
        {
        }

        try
        {
            App.GetService<IAppNotificationService>().Unregister();
        }
        catch
        {
        }

        LoggingHelper.CloseAndFlush();
    }

    private void MainWindow_Closed(object sender, WindowEventArgs args)
    {
        UnsubscribeWindowEvents();
        UnsubscribeThemeEvents();
    }

    // this handles updating the caption button colors correctly when indows system theme is changed
    // while the app is open
    private void Settings_ColorValuesChanged(UISettings sender, object args)
    {
        var queue = dispatcherQueue;
        if (_isShutdownStarted || queue is null)
        {
            return;
        }

        // This calls comes off-thread, hence we will need to dispatch it to current app's thread
        _ = queue.TryEnqueue(() =>
        {
            if (!_isShutdownStarted)
            {
                TitleBarHelper.ApplySystemThemeToCaptionButtons();
            }
        });
    }

    private void UnsubscribeWindowEvents()
    {
        AppWindow.Closing -= AppWindow_Closing;
        Closed -= MainWindow_Closed;
    }

    private void UnsubscribeThemeEvents()
    {
        if (settings is not null)
        {
            settings.ColorValuesChanged -= Settings_ColorValuesChanged;
            settings = null;
        }

        dispatcherQueue = null;
    }
}
