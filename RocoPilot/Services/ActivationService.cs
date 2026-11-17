using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

using RocoPilot.Activation;
using RocoPilot.Contracts.Services;
using RocoPilot.Contracts.Services.Statistics;
using RocoPilot.Views;

namespace RocoPilot.Services;

public class ActivationService : IActivationService
{
    private readonly ActivationHandler<LaunchActivatedEventArgs> _defaultHandler;
    private readonly IEnumerable<IActivationHandler> _activationHandlers;
    private readonly IThemeSelectorService _themeSelectorService;
    private readonly IStatisticsSyncService _statisticsSyncService;
    private readonly IAppNotificationService _notifications;
    private readonly ILogger<ActivationService> _logger;
    private UIElement? _shell = null;

    public ActivationService(
        ActivationHandler<LaunchActivatedEventArgs> defaultHandler,
        IEnumerable<IActivationHandler> activationHandlers,
        IThemeSelectorService themeSelectorService,
        IStatisticsSyncService statisticsSyncService,
        IAppNotificationService notifications,
        ILogger<ActivationService> logger)
    {
        _defaultHandler = defaultHandler;
        _activationHandlers = activationHandlers;
        _themeSelectorService = themeSelectorService;
        _statisticsSyncService = statisticsSyncService;
        _notifications = notifications;
        _logger = logger;
    }

    public async Task ActivateAsync(object activationArgs)
    {
        // Execute tasks before activation.
        await InitializeAsync();

        // Set the MainWindow Content.
        if (App.MainWindow.Content == null)
        {
            _shell = App.GetService<ShellPage>();
            App.MainWindow.Content = _shell ?? new Frame();
        }

        // Handle activation via ActivationHandlers.
        await HandleActivationAsync(activationArgs);

        // Activate the MainWindow.
        App.MainWindow.Activate();

        // Execute tasks after activation.
        await StartupAsync();
    }

    private async Task HandleActivationAsync(object activationArgs)
    {
        var activationHandler = _activationHandlers.FirstOrDefault(h => h.CanHandle(activationArgs));

        if (activationHandler != null)
        {
            await activationHandler.HandleAsync(activationArgs);
        }

        if (_defaultHandler.CanHandle(activationArgs))
        {
            await _defaultHandler.HandleAsync(activationArgs);
        }
    }

    private async Task InitializeAsync()
    {
        await _themeSelectorService.InitializeAsync();
        await _notifications.InitializeAsync();
    }

    private async Task StartupAsync()
    {
        await _themeSelectorService.SetRequestedThemeAsync();

        try
        {
            await _statisticsSyncService.DownloadRemoteChangesIfNeededAsync();
        }
        catch (Exception ex)
        {
            // 同步服务已经记录过失败原因，这里只说明后续怎么处理。
            _logger.LogDebug(ex, "启动时没有拿到云端统计，继续使用本地数据");
        }
    }
}
