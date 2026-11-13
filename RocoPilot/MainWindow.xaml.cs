using RocoPilot.Contracts.Services.Statistics;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;

using RocoPilot.Contracts.Services;
using RocoPilot.Helpers;

using Serilog;

using Windows.UI.ViewManagement;

namespace RocoPilot;

public sealed partial class MainWindow : WindowEx
{
    private readonly WindowShutdownCoordinator _shutdownCoordinator;

    private readonly Microsoft.UI.Dispatching.DispatcherQueue dispatcherQueue;

    private UISettings? settings;

    public MainWindow()
    {
        InitializeComponent();

        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets/WindowIcon.ico"));
        Content = null;
        Title = "AppDisplayName".GetLocalized();
        _shutdownCoordinator = new WindowShutdownCoordinator(
            ShutdownAsync,
            callback => DispatcherQueue.TryEnqueue(() => callback()),
            Close,
            exception => Log.ForContext(typeof(MainWindow)).Error(exception, "关闭主窗口时发生异常"));

        // Theme change code picked from https://github.com/microsoft/WinUI-Gallery/pull/1239
        dispatcherQueue = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
        settings = new UISettings();
        settings.ColorValuesChanged += Settings_ColorValuesChanged; // cannot use FrameworkElement.ActualThemeChanged event
        AppWindow.Closing += AppWindow_Closing;
        Closed += MainWindow_Closed;
    }

    private void AppWindow_Closing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        args.Cancel = _shutdownCoordinator.HandleCloseRequest();
    }

    private async Task ShutdownAsync()
    {
        App.GetService<IHotkeyService>().Dispose();

        try
        {
            var runtimeTaskService = App.GetService<IRuntimeTaskService>();
            await runtimeTaskService.StopAsync();
        }
        catch (Exception ex)
        {
            Log.ForContext(typeof(MainWindow)).Warning(ex, "关闭应用时停止实时任务失败");
        }

        try
        {
            await App.GetService<IStatisticsSyncService>().DisposeAsync();
        }
        catch (Exception ex)
        {
            Log.ForContext(typeof(MainWindow)).Warning(ex, "关闭云同步任务时发生异常");
        }

        try
        {
            App.GetService<IAppNotificationService>().Unregister();
        }
        catch (Exception ex)
        {
            Log.ForContext(typeof(MainWindow)).Warning(ex, "注销 Windows 通知失败");
        }
    }

    private void MainWindow_Closed(object sender, WindowEventArgs args)
    {
        AppWindow.Closing -= AppWindow_Closing;
        Closed -= MainWindow_Closed;
        settings!.ColorValuesChanged -= Settings_ColorValuesChanged;
        settings = null;
        Log.ForContext(typeof(MainWindow)).Information("RocoPilot 已关闭");
        LoggingHelper.CloseAndFlush();
    }

    private void Settings_ColorValuesChanged(UISettings sender, object args)
    {
        _ = dispatcherQueue.TryEnqueue(() =>
        {
            if (settings is not null && !_shutdownCoordinator.IsShutdownStarted)
            {
                TitleBarHelper.ApplySystemThemeToCaptionButtons();
            }
        });
    }

}
