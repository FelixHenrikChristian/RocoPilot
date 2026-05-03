using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;

using RocoPilot.Activation;
using RocoPilot.Contracts.Services;
using RocoPilot.Contracts.Services.Capture;
using RocoPilot.Contracts.Services.TextRecognition;
using RocoPilot.Core.Contracts.Services;
using RocoPilot.Core.Services;
using RocoPilot.Helpers;
using RocoPilot.Models;
using RocoPilot.Notifications;
using RocoPilot.Services;
using RocoPilot.Services.Capture;
using RocoPilot.Services.Capture.Backends;
using RocoPilot.Services.TextRecognition;
using RocoPilot.Services.TextRecognition.Backends;
using RocoPilot.ViewModels;
using RocoPilot.Views;

using Serilog;

namespace RocoPilot;

// To learn more about WinUI 3, see https://docs.microsoft.com/windows/apps/winui/winui3/.
public partial class App : Application
{
    // The .NET Generic Host provides dependency injection, configuration, logging, and other services.
    // https://docs.microsoft.com/dotnet/core/extensions/generic-host
    // https://docs.microsoft.com/dotnet/core/extensions/dependency-injection
    // https://docs.microsoft.com/dotnet/core/extensions/configuration
    // https://docs.microsoft.com/dotnet/core/extensions/logging
    public IHost Host
    {
        get;
    }

    public static T GetService<T>()
        where T : class
    {
        if ((App.Current as App)!.Host.Services.GetService(typeof(T)) is not T service)
        {
            throw new ArgumentException($"{typeof(T)} needs to be registered in ConfigureServices within App.xaml.cs.");
        }

        return service;
    }

    public static WindowEx MainWindow { get; } = new MainWindow();

    public static UIElement? AppTitlebar { get; set; }

    public App()
    {
        InitializeComponent();

        LoggingHelper.ConfigureSerilog();
        LoggingHelper.LogStartupBanner();

        Host = Microsoft.Extensions.Hosting.Host.
        CreateDefaultBuilder().
        UseContentRoot(AppContext.BaseDirectory).
        ConfigureLogging(logging =>
        {
            logging.ClearProviders();
            logging.AddSerilog(dispose: true);
        }).
        ConfigureServices((context, services) =>
        {
            // Default Activation Handler
            services.AddTransient<ActivationHandler<LaunchActivatedEventArgs>, DefaultActivationHandler>();

            // Other Activation Handlers
            services.AddTransient<IActivationHandler, AppNotificationActivationHandler>();

            // Services
            services.AddSingleton<IAppNotificationService, AppNotificationService>();
            services.AddSingleton<ILocalSettingsService, LocalSettingsService>();
            services.AddSingleton<IThemeSelectorService, ThemeSelectorService>();
            services.AddTransient<INavigationViewService, NavigationViewService>();

            services.AddSingleton<IActivationService, ActivationService>();
            services.AddSingleton<IPageService, PageService>();
            services.AddSingleton<INavigationService, NavigationService>();
            services.AddSingleton<IGameWindowService, GameWindowService>();
            services.AddSingleton<IRuntimeTaskService, RuntimeTaskService>();
            services.AddSingleton<IWindowEnumerationService, WindowEnumerationService>();
            services.AddSingleton<ICaptureBackend, BitBltCaptureBackend>();
            services.AddSingleton<ICaptureBackend, PrintWindowCaptureBackend>();
            services.AddSingleton<ICaptureBackend, WindowsGraphicsCaptureBackend>();
            services.AddSingleton<IScreenCaptureService, ScreenCaptureService>();
            services.AddSingleton<ITextRecognitionBackend, WindowsOcrTextRecognitionBackend>();
            services.AddSingleton<ITextRecognitionService, TextRecognitionService>();

            // Core Services
            services.AddSingleton<IFileService, FileService>();

            // Views and ViewModels
            services.AddTransient<SettingsViewModel>();
            services.AddTransient<SettingsPage>();
            services.AddSingleton<MainViewModel>();
            services.AddTransient<MainPage>();
            services.AddTransient<RealtimeViewModel>();
            services.AddTransient<RealtimePage>();
            services.AddTransient<StatisticsViewModel>();
            services.AddTransient<StatisticsPage>();
            services.AddTransient<LogViewModel>();
            services.AddTransient<LogPage>();
            services.AddTransient<TestViewModel>();
            services.AddTransient<TestPage>();
            services.AddTransient<ShellPage>();
            services.AddTransient<ShellViewModel>();

            // Configuration
            services.Configure<LocalSettingsOptions>(context.Configuration.GetSection(nameof(LocalSettingsOptions)));
        }).
        Build();

        App.GetService<IAppNotificationService>().Initialize();

        UnhandledException += App_UnhandledException;
    }

    private void App_UnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
    {
        Log.Fatal(e.Exception, "未处理异常: {Message}", e.Message);
        LoggingHelper.CloseAndFlush();
    }

    protected async override void OnLaunched(LaunchActivatedEventArgs args)
    {
        base.OnLaunched(args);

        App.GetService<IAppNotificationService>().Show(string.Format("AppNotificationSamplePayload".GetLocalized(), AppContext.BaseDirectory));

        await App.GetService<IActivationService>().ActivateAsync(args);
    }
}
