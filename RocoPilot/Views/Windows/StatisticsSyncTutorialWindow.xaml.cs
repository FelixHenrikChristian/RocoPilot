using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

using RocoPilot.Contracts.Services;

using Windows.Graphics;

namespace RocoPilot.Views.Windows;

public sealed partial class StatisticsSyncTutorialWindow : WindowEx
{
    private readonly IThemeSelectorService _themeSelectorService;

    public StatisticsSyncTutorialWindow()
    {
        _themeSelectorService = App.GetService<IThemeSelectorService>();

        InitializeComponent();

        ContentRoot.RequestedTheme = _themeSelectorService.Theme;

        Title = "Cloudflare R2 同步教程";
        AppWindow.Title = Title;
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets/WindowIcon.ico"));
        AppWindow.TitleBar.PreferredTheme = TitleBarTheme.UseDefaultAppMode;
        AppWindow.Resize(new SizeInt32(680, 620));
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }
}
