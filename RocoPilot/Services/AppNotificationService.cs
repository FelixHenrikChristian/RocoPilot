using System.Collections.Specialized;
using System.Runtime.InteropServices;
using System.Web;

using Microsoft.Windows.AppNotifications;

using RocoPilot.Contracts.Services;
using RocoPilot.ViewModels;

namespace RocoPilot.Notifications;

public class AppNotificationService : IAppNotificationService
{
    private readonly INavigationService _navigationService;
    private bool _isRegistered;

    public AppNotificationService(INavigationService navigationService)
    {
        _navigationService = navigationService;
    }

    ~AppNotificationService()
    {
        Unregister();
    }

    public void Initialize()
    {
        if (!IsNotificationApiSupported())
        {
            return;
        }

        try
        {
            AppNotificationManager.Default.NotificationInvoked += OnNotificationInvoked;
            AppNotificationManager.Default.Register();
            _isRegistered = true;
        }
        catch (COMException)
        {
            AppNotificationManager.Default.NotificationInvoked -= OnNotificationInvoked;
            _isRegistered = false;
        }
    }

    public void OnNotificationInvoked(AppNotificationManager sender, AppNotificationActivatedEventArgs args)
    {
        // TODO: Handle notification invocations when your app is already running.

        //// // Navigate to a specific page based on the notification arguments.
        //// if (ParseArguments(args.Argument)["action"] == "Settings")
        //// {
        ////    App.MainWindow.DispatcherQueue.TryEnqueue(() =>
        ////    {
        ////        _navigationService.NavigateTo(typeof(SettingsViewModel).FullName!);
        ////    });
        //// }

        App.MainWindow.DispatcherQueue.TryEnqueue(() =>
        {
            App.MainWindow.ShowMessageDialogAsync("TODO: Handle notification invocations when your app is already running.", "Notification Invoked");

            App.MainWindow.BringToFront();
        });
    }

    public bool Show(string payload)
    {
        if (!_isRegistered)
        {
            return false;
        }

        var appNotification = new AppNotification(payload);

        try
        {
            AppNotificationManager.Default.Show(appNotification);
        }
        catch (COMException)
        {
            return false;
        }

        return appNotification.Id != 0;
    }

    public NameValueCollection ParseArguments(string arguments)
    {
        return HttpUtility.ParseQueryString(arguments);
    }

    public void Unregister()
    {
        if (!_isRegistered)
        {
            return;
        }

        try
        {
            AppNotificationManager.Default.Unregister();
        }
        catch (COMException)
        {
        }
        finally
        {
            _isRegistered = false;
        }
    }

    private static bool IsNotificationApiSupported()
    {
        try
        {
            return AppNotificationManager.IsSupported();
        }
        catch (COMException)
        {
            return false;
        }
    }
}
