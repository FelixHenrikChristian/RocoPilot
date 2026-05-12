using System.Runtime.InteropServices;

using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

using WinRT.Interop;

using WinUIEx;

using Windows.UI;
using Windows.UI.ViewManagement;

namespace RocoPilot.Helpers;

// Helper class to workaround custom title bar bugs.
// DISCLAIMER: The resource key names and color values used below are subject to change. Do not depend on them.
// https://github.com/microsoft/TemplateStudio/issues/4516
internal class TitleBarHelper
{
    private const int WAINACTIVE = 0x00;
    private const int WAACTIVE = 0x01;
    private const int WMACTIVATE = 0x0006;

    [DllImport("user32.dll")]
    private static extern IntPtr GetActiveWindow();

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, int wParam, IntPtr lParam);

    public static void UpdateTitleBar(ElementTheme theme) => UpdateTitleBar(App.MainWindow, theme);

    public static void UpdateTitleBar(WindowEx window, ElementTheme theme)
    {
        if (!window.ExtendsContentIntoTitleBar)
        {
            return;
        }

        if (theme == ElementTheme.Default)
        {
            var uiSettings = new UISettings();
            var background = uiSettings.GetColorValue(UIColorType.Background);

            theme = background == Colors.White ? ElementTheme.Light : ElementTheme.Dark;
        }

        if (theme == ElementTheme.Default)
        {
            theme = Application.Current.RequestedTheme == ApplicationTheme.Light ? ElementTheme.Light : ElementTheme.Dark;
        }

        window.AppWindow.TitleBar.ButtonForegroundColor = theme switch
        {
            ElementTheme.Dark => Colors.White,
            ElementTheme.Light => Colors.Black,
            _ => Colors.Transparent
        };

        window.AppWindow.TitleBar.ButtonHoverForegroundColor = theme switch
        {
            ElementTheme.Dark => Colors.White,
            ElementTheme.Light => Colors.Black,
            _ => Colors.Transparent
        };

        window.AppWindow.TitleBar.ButtonHoverBackgroundColor = theme switch
        {
            ElementTheme.Dark => Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF),
            ElementTheme.Light => Color.FromArgb(0x33, 0x00, 0x00, 0x00),
            _ => Colors.Transparent
        };

        window.AppWindow.TitleBar.ButtonPressedBackgroundColor = theme switch
        {
            ElementTheme.Dark => Color.FromArgb(0x66, 0xFF, 0xFF, 0xFF),
            ElementTheme.Light => Color.FromArgb(0x66, 0x00, 0x00, 0x00),
            _ => Colors.Transparent
        };

        window.AppWindow.TitleBar.BackgroundColor = Colors.Transparent;

        var hwnd = WindowNative.GetWindowHandle(window);
        if (hwnd == GetActiveWindow())
        {
            SendMessage(hwnd, WMACTIVATE, WAINACTIVE, IntPtr.Zero);
            SendMessage(hwnd, WMACTIVATE, WAACTIVE, IntPtr.Zero);
        }
        else
        {
            SendMessage(hwnd, WMACTIVATE, WAACTIVE, IntPtr.Zero);
            SendMessage(hwnd, WMACTIVATE, WAINACTIVE, IntPtr.Zero);
        }
    }

    public static void ApplySystemThemeToCaptionButtons()
    {
        var frame = App.AppTitlebar as FrameworkElement;
        if (frame != null)
        {
            UpdateTitleBar(frame.ActualTheme);
        }
    }
}
