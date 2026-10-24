using Microsoft.UI.Xaml.Controls;
using RocoPilot.ViewModels;

namespace RocoPilot.Views;

public sealed partial class NotificationSettingsPage : Page
{
    public NotificationSettingsViewModel ViewModel { get; }

    public NotificationSettingsPage()
    {
        ViewModel = App.GetService<NotificationSettingsViewModel>();
        InitializeComponent();
    }
}
