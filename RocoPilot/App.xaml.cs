using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;

using RocoPilot.Activation;
using RocoPilot.Contracts.Services;
using RocoPilot.Contracts.Services.Capture;
using RocoPilot.Contracts.Services.Encounters;
using RocoPilot.Contracts.Services.ImageMatching;
using RocoPilot.Contracts.Services.Recognition;
using RocoPilot.Contracts.Services.Spirits;
using RocoPilot.Contracts.Services.Statistics;
using RocoPilot.Contracts.Services.TextRecognition;
using RocoPilot.Core.Contracts.Services;
using RocoPilot.Core.Services;
using RocoPilot.Helpers;
using RocoPilot.Models;
using RocoPilot.Notifications;
using RocoPilot.Services;
using RocoPilot.Services.Capture;
using RocoPilot.Services.Capture.Backends;
using RocoPilot.Services.Encounters;
using RocoPilot.Services.ImageMatching;
using RocoPilot.Services.Recognition;
using RocoPilot.Services.Spirits;
using RocoPilot.Services.Statistics;
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
            services.AddSingleton<IKeyboardInputService, KeyboardInputService>();
            services.AddSingleton<IInterceptionDriverService, InterceptionDriverService>();
            services.AddSingleton<RuntimeTaskService>();
            services.AddSingleton<StatisticsUidRuntimeTaskService>();
            services.AddSingleton<IRuntimeTaskService>(
                provider => provider.GetRequiredService<StatisticsUidRuntimeTaskService>());
            services.AddSingleton<IStatisticsUidCoordinatorService>(
                provider => provider.GetRequiredService<StatisticsUidRuntimeTaskService>());
            services.AddSingleton<IIndependentTaskService, IndependentTaskService>();
            services.AddSingleton<IHotkeyService, HotkeyService>();
            services.AddSingleton<IRecognitionOverlayService, RecognitionOverlayService>();
            services.AddSingleton<InfoOverlayService>();
            services.AddSingleton<IInfoOverlayService>(provider => provider.GetRequiredService<InfoOverlayService>());
            services.AddSingleton<IInfoOverlayNotificationService>(provider => provider.GetRequiredService<InfoOverlayService>());
            services.AddSingleton<IRecognitionRegionConfigService, RecognitionRegionConfigService>();
            services.AddSingleton<IEncounterSeasonConfigService, EncounterSeasonConfigService>();
            services.AddSingleton<ISpiritCatalogService, SpiritCatalogService>();
            services.AddSingleton<IStatisticsService, StatisticsService>();
            services.AddSingleton<IStatisticsSyncService, StatisticsSyncService>();
            services.AddSingleton<IStatisticsUidDetectionService, StatisticsUidDetectionService>();
            services.AddSingleton<IUpdateService, UpdateService>();
            services.AddSingleton<IWindowEnumerationService, WindowEnumerationService>();
            services.AddSingleton<ICaptureBackend, BitBltCaptureBackend>();
            services.AddSingleton<ICaptureBackend, PrintWindowCaptureBackend>();
            services.AddSingleton<ICaptureBackend, WindowsGraphicsCaptureBackend>();
            services.AddSingleton<IScreenCaptureService, ScreenCaptureService>();
            services.AddSingleton<IImageMatchingService, ImageMatchingService>();
            services.AddSingleton<ITextRecognitionBackend, PaddleOcrV5TextRecognitionBackend>();
            services.AddSingleton<ITextRecognitionBackend, WindowsOcrTextRecognitionBackend>();
            services.AddSingleton<ITextRecognitionBackend, TesseractTextRecognitionBackend>();
            services.AddSingleton<OnnxOcrV5SingleLineTextRecognitionBackend>();
            services.AddSingleton<ITextRecognitionBackend, OnnxOcrV5TextRecognitionBackend>();
            services.AddSingleton<ITextRecognitionService, TextRecognitionService>();

            // Core Services
            services.AddSingleton<IFileService, FileService>();

            // Views and ViewModels
            services.AddTransient<SettingsViewModel>();
            services.AddTransient<SettingsPage>();
            services.AddSingleton<MainViewModel>();
            services.AddTransient<MainPage>();
            services.AddSingleton<RealtimeViewModel>();
            services.AddTransient<RealtimePage>();
            services.AddSingleton<TasksViewModel>();
            services.AddTransient<TasksPage>();
            services.AddSingleton<StatisticsViewModel>();
            services.AddTransient<StatisticsPage>();
            services.AddSingleton<LogViewModel>();
            services.AddTransient<LogPage>();
            services.AddSingleton<HotkeyViewModel>();
            services.AddTransient<HotkeyPage>();
#if DEBUG
            services.AddTransient<TestViewModel>();
            services.AddTransient<TestPage>();
#endif
            services.AddTransient<ShellPage>();
            services.AddTransient<ShellViewModel>();

            // Configuration
            services.Configure<LocalSettingsOptions>(context.Configuration.GetSection(nameof(LocalSettingsOptions)));
            services.Configure<UpdateSourceOptions>(context.Configuration.GetSection(nameof(UpdateSourceOptions)));
        }).
        Build();

        App.GetService<IAppNotificationService>().Initialize();
        _ = App.GetService<OnnxOcrV5SingleLineTextRecognitionBackend>().PrewarmAsync();

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
