using System.Diagnostics;

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

using RocoPilot.Controls;
using RocoPilot.Contracts.Services;
using RocoPilot.Helpers;

using Windows.Foundation;

namespace RocoPilot.Views;

public static class InterceptionDriverInstallDialog
{
    public static async Task<bool> EnsureInstalledAsync(
        XamlRoot? xamlRoot,
        IInterceptionDriverService driverService)
    {
        if (driverService.IsDriverInstalled())
        {
            return true;
        }

        if (xamlRoot is null)
        {
            return false;
        }

        var confirmResult = await ShowInstallPromptAsync(xamlRoot, driverService.ReleasePageUri);
        if (confirmResult != ContentDialogResult.Primary)
        {
            return false;
        }

        return await ShowInstallProgressAsync(xamlRoot, driverService);
    }

    private static async Task<ContentDialogResult> ShowInstallPromptAsync(XamlRoot xamlRoot, Uri releasePageUri)
    {
        var dialog = CreateInstallPromptDialog(xamlRoot);

        var result = await dialog.ShowAsync();
        if (result == ContentDialogResult.Secondary)
        {
            _ = ShellLaunchHelper.LaunchUri(releasePageUri);
        }

        return result;
    }

    private static async Task<bool> ShowInstallProgressAsync(
        XamlRoot xamlRoot,
        IInterceptionDriverService driverService)
    {
        var dialog = CreateInstallProgressDialog(xamlRoot, out var statusText, out var progressBar);

        var progress = new Progress<InterceptionDriverInstallProgress>(value =>
        {
            statusText.Text = value.Message;
            progressBar.IsIndeterminate = value.Percent is null;
            if (value.Percent is { } percent)
            {
                progressBar.Value = percent;
            }
        });

        var dialogOperation = dialog.ShowAsync();
        await Task.Yield();

        try
        {
            var result = await driverService.InstallAsync(progress);
            dialog.Hide();
            await WaitForDialogToCloseAsync(dialogOperation);

            if (result.WasAlreadyInstalled)
            {
                await ShowAlreadyInstalledDialogAsync(xamlRoot);
            }
            else
            {
                await ShowRestartPromptAsync(xamlRoot);
            }

            return true;
        }
        catch (OperationCanceledException)
        {
            dialog.Hide();
            await WaitForDialogToCloseAsync(dialogOperation);
            return false;
        }
        catch (Exception ex)
        {
            dialog.Hide();
            await WaitForDialogToCloseAsync(dialogOperation);
            await ShowInstallFailedDialogAsync(xamlRoot, driverService.ReleasePageUri, ex.Message);
            return false;
        }
    }

    private static async Task ShowAlreadyInstalledDialogAsync(XamlRoot xamlRoot)
    {
        var dialog = CreateAlreadyInstalledDialog(xamlRoot);
        await dialog.ShowAsync();
    }

    private static async Task ShowRestartPromptAsync(XamlRoot xamlRoot)
    {
        var dialog = CreateRestartPromptDialog(xamlRoot);
        if (await dialog.ShowAsync() == ContentDialogResult.Primary && !TryRestartWindows())
        {
            await ShowRestartFailedDialogAsync(xamlRoot);
        }
    }

    private static async Task ShowInstallFailedDialogAsync(
        XamlRoot xamlRoot,
        Uri releasePageUri,
        string errorMessage)
    {
        var dialog = CreateInstallFailedDialog(xamlRoot, errorMessage);
        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            _ = ShellLaunchHelper.LaunchUri(releasePageUri);
        }
    }

    private static async Task ShowRestartFailedDialogAsync(XamlRoot xamlRoot)
    {
        var dialog = CreateRestartFailedDialog(xamlRoot);
        await dialog.ShowAsync();
    }

    internal static AppContentDialog CreateInstallPromptDialog(XamlRoot xamlRoot)
    {
        var dialog = CreateDialog(xamlRoot, "安装键盘驱动", "Interception", "系统级键盘输入驱动",
            "RocoPilot 将下载官方安装包并执行安装命令，安装过程需要管理员权限。",
            "\uE765", "AccentTextFillColorPrimaryBrush",
            "安装完成后需要重启电脑，驱动才会生效。", "\uE783", "SystemFillColorCautionBrush");
        dialog.PrimaryButtonText = "下载并安装";
        dialog.SecondaryButtonText = "打开下载页";
        dialog.CloseButtonText = "取消";
        dialog.DefaultButton = ContentDialogButton.Primary;
        return dialog;
    }

    internal static AppContentDialog CreateInstallProgressDialog(
        XamlRoot xamlRoot, out TextBlock statusText, out ProgressBar progressBar)
    {
        statusText = CreateText("正在准备安装...");
        progressBar = new ProgressBar { IsIndeterminate = true, Minimum = 0, Maximum = 100 };
        var progressPanel = new StackPanel { Spacing = 10 };
        progressPanel.Children.Add(statusText);
        progressPanel.Children.Add(progressBar);
        return CreateDialog(xamlRoot, "正在安装键盘驱动", "Interception", "下载与安装",
            null, "\uE896", "AccentTextFillColorPrimaryBrush",
            "安装完成后需要重启电脑，驱动才会生效。", "\uE783", "SystemFillColorCautionBrush",
            progressPanel);
    }

    internal static AppContentDialog CreateAlreadyInstalledDialog(XamlRoot xamlRoot)
    {
        var dialog = CreateDialog(xamlRoot, "驱动已安装", "Interception", "已检测到驱动",
            "可以继续使用 Interception 输入方式。", "\uE73E", "SystemFillColorSuccessBrush");
        dialog.CloseButtonText = "知道了";
        dialog.DefaultButton = ContentDialogButton.Close;
        return dialog;
    }

    internal static AppContentDialog CreateRestartPromptDialog(XamlRoot xamlRoot)
    {
        var dialog = CreateDialog(xamlRoot, "驱动安装完成", "Interception", "安装命令已完成",
            "重启电脑后，Interception 输入方式才会生效。", "\uE73E", "SystemFillColorSuccessBrush",
            "重启前请保存正在进行的工作。", "\uE783", "SystemFillColorCautionBrush");
        dialog.PrimaryButtonText = "立即重启";
        dialog.PrimaryButtonStyle = Resource<Style>("DialogButtonStyle");
        dialog.CloseButtonText = "稍后重启";
        dialog.CloseButtonStyle = Resource<Style>("DialogPrimaryButtonStyle");
        dialog.DefaultButton = ContentDialogButton.Close;
        return dialog;
    }

    internal static AppContentDialog CreateInstallFailedDialog(XamlRoot xamlRoot, string errorMessage)
    {
        var errorText = CreateText(errorMessage);
        errorText.IsTextSelectionEnabled = true;
        var dialog = CreateDialog(xamlRoot, "驱动安装失败", "Interception", "自动安装未完成",
            null, "\uE783", "SystemFillColorCriticalBrush",
            "可以前往官方下载页，手动下载并安装驱动。", "\uE774", "TextFillColorSecondaryBrush",
            errorText);
        dialog.PrimaryButtonText = "打开下载页";
        dialog.CloseButtonText = "关闭";
        dialog.DefaultButton = ContentDialogButton.Primary;
        return dialog;
    }

    internal static AppContentDialog CreateRestartFailedDialog(XamlRoot xamlRoot)
    {
        var dialog = CreateDialog(xamlRoot, "无法自动重启", "需要手动重启", "Windows 重启命令未启动",
            "请保存正在进行的工作，然后手动重启电脑，让 Interception 驱动生效。",
            "\uE783", "SystemFillColorCautionBrush");
        dialog.CloseButtonText = "知道了";
        dialog.DefaultButton = ContentDialogButton.Close;
        return dialog;
    }

    private static AppContentDialog CreateDialog(
        XamlRoot xamlRoot, string title, string heading, string subtitle, string? description,
        string glyph, string iconBrush, string? hint = null, string hintGlyph = "\uE946",
        string hintBrush = "TextFillColorSecondaryBrush", UIElement? detail = null)
    {
        var header = new Grid { ColumnSpacing = 12 };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.Children.Add(new Border
        {
            Width = 36, Height = 36, CornerRadius = new CornerRadius(8),
            VerticalAlignment = VerticalAlignment.Center,
            Background = Resource<Brush>("ControlFillColorDefaultBrush"),
            Child = CreateIcon(glyph, iconBrush, 18)
        });
        var summary = new StackPanel { Spacing = 3, VerticalAlignment = VerticalAlignment.Center };
        summary.Children.Add(new TextBlock { Text = heading, Style = Resource<Style>("DialogSectionTitleStyle") });
        summary.Children.Add(CreateText(subtitle, secondary: true));
        Grid.SetColumn(summary, 1);
        header.Children.Add(summary);

        var cardContent = new StackPanel { Spacing = 14 };
        cardContent.Children.Add(header);
        if (description is not null || detail is not null)
        {
            cardContent.Children.Add(new Border { Height = 1, Background = Resource<Brush>("DividerStrokeColorDefaultBrush") });
            if (description is not null) cardContent.Children.Add(CreateText(description));
            if (detail is not null) cardContent.Children.Add(detail);
        }
        var content = new StackPanel { Spacing = 12 };
        content.Children.Add(new Border { Style = Resource<Style>("DialogCardStyle"), Padding = new Thickness(16), Child = cardContent });
        if (hint is not null)
        {
            var hintContent = new Grid { ColumnSpacing = 10 };
            hintContent.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            hintContent.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            hintContent.Children.Add(CreateIcon(hintGlyph, hintBrush, 16));
            var hintText = CreateText(hint, secondary: true);
            Grid.SetColumn(hintText, 1);
            hintContent.Children.Add(hintText);
            content.Children.Add(new Border { Style = Resource<Style>("ConfigHintBarStyle"), Child = hintContent });
        }
        var scrollViewer = new ScrollViewer
        {
            Content = content,
            HorizontalScrollMode = ScrollMode.Disabled,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto
        };
        var dialog = new DriverContentDialog { XamlRoot = xamlRoot, Title = title, Content = scrollViewer };
        dialog.Loaded += (_, _) =>
        {
            content.Width = Math.Min(400, Math.Max(240, xamlRoot.Size.Width - 96));
            scrollViewer.MaxHeight = Math.Min(440, Math.Max(120, xamlRoot.Size.Height - 200));
        };
        return dialog;
    }

    private static T Resource<T>(string key) => (T)Application.Current.Resources[key];

    private static FontIcon CreateIcon(string glyph, string brush, double size) => new()
    {
        Glyph = glyph, FontSize = size,
        FontFamily = Resource<FontFamily>("SymbolThemeFontFamily"),
        Foreground = Resource<Brush>(brush), VerticalAlignment = VerticalAlignment.Center
    };

    private static TextBlock CreateText(string text, bool secondary = false)
    {
        return new TextBlock
        {
            Text = text,
            TextWrapping = TextWrapping.Wrap,
            FontSize = secondary ? 12 : 13,
            LineHeight = secondary ? 18 : 20,
            Foreground = Resource<Brush>(secondary ? "TextFillColorSecondaryBrush" : "TextFillColorPrimaryBrush")
        };
    }

    private sealed class DriverContentDialog : AppContentDialog
    {
        protected override void OnApplyTemplate()
        {
            base.OnApplyTemplate();
            // Default-button visual states replace Style, so preserve geometry with local values.
            foreach (var name in new[] { "PrimaryButton", "SecondaryButton", "CloseButton" })
            {
                if (GetTemplateChild(name) is not Button button) continue;
                button.FontSize = 13;
                button.MinHeight = 34;
                button.MinWidth = 96;
                button.Padding = new Thickness(14, 6, 14, 6);
                button.CornerRadius = new CornerRadius(8);
            }
        }
    }

    private static async Task WaitForDialogToCloseAsync(IAsyncOperation<ContentDialogResult> dialogOperation)
    {
        try
        {
            _ = await dialogOperation;
        }
        catch
        {
        }
    }

    private static bool TryRestartWindows()
    {
        try
        {
            return Process.Start(new ProcessStartInfo
            {
                FileName = "shutdown.exe",
                Arguments = "/r /t 0",
                UseShellExecute = true
            }) is not null;
        }
        catch
        {
            return false;
        }
    }
}
