using System.Diagnostics;

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

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
        var dialog = new ContentDialog
        {
            XamlRoot = xamlRoot,
            Title = "需要安装 Interception 驱动",
            Content = new StackPanel
            {
                Spacing = 10,
                Children =
                {
                    CreateText("Interception 是系统级键盘驱动。RocoPilot 可以自动下载官方安装包并执行安装命令，安装过程需要管理员权限。"),
                    CreateText("安装完成后必须重启电脑，重启后再使用 Interception 输入方式。"),
                    CreateText("如果网络下载失败，也可以打开官方下载页手动安装。")
                }
            },
            PrimaryButtonText = "下载并安装",
            SecondaryButtonText = "打开下载页",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary
        };

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
        var statusText = CreateText("正在准备安装...");
        var progressBar = new ProgressBar
        {
            IsIndeterminate = true,
            Minimum = 0,
            Maximum = 100
        };

        var dialog = new ContentDialog
        {
            XamlRoot = xamlRoot,
            Title = "正在安装 Interception 驱动",
            Content = new StackPanel
            {
                Spacing = 12,
                Children =
                {
                    statusText,
                    progressBar
                }
            }
        };

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
        var dialog = new ContentDialog
        {
            XamlRoot = xamlRoot,
            Title = "Interception 已安装",
            Content = CreateText("已检测到 Interception 驱动，可以继续使用 Interception 输入方式。"),
            CloseButtonText = "知道了",
            DefaultButton = ContentDialogButton.Close
        };

        await dialog.ShowAsync();
    }

    private static async Task ShowRestartPromptAsync(XamlRoot xamlRoot)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = xamlRoot,
            Title = "Interception 安装完成",
            Content = CreateText("驱动安装命令已完成。请重启电脑，重启后 Interception 输入方式才会生效。"),
            PrimaryButtonText = "立即重启",
            CloseButtonText = "稍后重启",
            DefaultButton = ContentDialogButton.Close
        };

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
        var dialog = new ContentDialog
        {
            XamlRoot = xamlRoot,
            Title = "Interception 安装失败",
            Content = CreateText($"自动安装没有完成：{errorMessage}"),
            PrimaryButtonText = "打开下载页",
            CloseButtonText = "关闭",
            DefaultButton = ContentDialogButton.Primary
        };

        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            _ = ShellLaunchHelper.LaunchUri(releasePageUri);
        }
    }

    private static async Task ShowRestartFailedDialogAsync(XamlRoot xamlRoot)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = xamlRoot,
            Title = "无法自动重启",
            Content = CreateText("Windows 重启命令没有启动。请手动重启电脑，让 Interception 驱动生效。"),
            CloseButtonText = "知道了",
            DefaultButton = ContentDialogButton.Close
        };

        await dialog.ShowAsync();
    }

    private static TextBlock CreateText(string text)
    {
        return new TextBlock
        {
            Text = text,
            TextWrapping = TextWrapping.Wrap
        };
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
