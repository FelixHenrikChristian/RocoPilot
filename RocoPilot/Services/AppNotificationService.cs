using System.Collections.Specialized;
using System.Runtime.InteropServices;
using System.Web;

using Microsoft.Windows.AppNotifications;

using RocoPilot.Contracts.Services;

namespace RocoPilot.Notifications;

public class AppNotificationService : IAppNotificationService
{
    private bool _isRegistered;

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
        App.MainWindow.DispatcherQueue.TryEnqueue(() =>
        {
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
