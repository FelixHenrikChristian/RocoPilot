using System.Diagnostics;
using System.IO;
using System.Reflection;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

using RocoPilot.Contracts.Services;
using RocoPilot.Helpers;
using RocoPilot.Models;
using RocoPilot.Settings;
using RocoPilot.Views;

using Windows.ApplicationModel;

namespace RocoPilot.ViewModels;

public partial class SettingsViewModel : ObservableRecipient
{
    private readonly IThemeSelectorService _themeSelectorService;
    private readonly ILocalSettingsService _localSettingsService;
    private readonly IUpdateService _updateService;
    private readonly ILogger<SettingsViewModel> _logger;

    private bool _suppressThemeChange;

    public ThemeOption[] ThemeOptions { get; } =
    {
        new ThemeOption { ThemeKey = "System", Name = "跟随系统" },
        new ThemeOption { ThemeKey = "Light", Name = "浅色" },
        new ThemeOption { ThemeKey = "Dark", Name = "深色" },
    };

    [ObservableProperty]
    public partial ThemeOption? SelectedThemeOption { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsUpdateCheckEnabled))]
    [NotifyPropertyChangedFor(nameof(UpdateButtonText))]
    public partial bool IsCheckingUpdate { get; set; }

    [ObservableProperty]
    public partial string UpdateMessage { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsUpdateMessageOpen { get; set; }

    [ObservableProperty]
    public partial InfoBarSeverity UpdateMessageSeverity { get; set; }

    public bool IsUpdateCheckEnabled => !IsCheckingUpdate;

    public string UpdateButtonText => IsCheckingUpdate ? "正在检查" : "检查更新";

    public string AppVersion { get; }

    public SettingsViewModel(
        IThemeSelectorService themeSelectorService,
        ILocalSettingsService localSettingsService,
        IUpdateService updateService,
        ILogger<SettingsViewModel> logger)
    {
        _themeSelectorService = themeSelectorService;
        _localSettingsService = localSettingsService;
        _updateService = updateService;
        _logger = logger;
        AppVersion = GetAppVersionText();
    }

    public async Task LoadAsync()
    {
        _suppressThemeChange = true;
        try
        {
            var key = KeyFromElementTheme(_themeSelectorService.Theme);
            SelectedThemeOption = ThemeOptions.FirstOrDefault(t => t.ThemeKey == key) ?? ThemeOptions[0];
        }
        finally
        {
            _suppressThemeChange = false;
        }
    }

    partial void OnSelectedThemeOptionChanged(ThemeOption? value)
    {
        if (_suppressThemeChange || value == null)
        {
            return;
        }

        _ = ApplyThemeAsync(value);
    }

    private async Task ApplyThemeAsync(ThemeOption option)
    {
        await _themeSelectorService.SetThemeAsync(ElementThemeFromKey(option.ThemeKey));
    }

    [RelayCommand]
    private void OpenLogFolder()
    {
        try
        {
            Directory.CreateDirectory(LoggingHelper.LogDirectory);
            Process.Start(new ProcessStartInfo
            {
                FileName = LoggingHelper.LogDirectory,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "打开日志目录失败");
        }
    }

    [RelayCommand]
    private async Task CheckForUpdatesAsync()
    {
        if (IsCheckingUpdate)
        {
            return;
        }

        IsCheckingUpdate = true;
        IsUpdateMessageOpen = false;

        try
        {
            var result = await _updateService.CheckUpdateAsync(new UpdateOption { Trigger = UpdateTrigger.Manual });
            if (result.Status == UpdateCheckStatus.UpToDate)
            {
                ShowUpdateMessage("当前已是最新版本。", InfoBarSeverity.Success);
            }
            else if (result.Status == UpdateCheckStatus.Failed)
            {
                ShowUpdateMessage(
                    string.IsNullOrWhiteSpace(result.Message) ? "检查更新失败，请稍后重试。" : result.Message,
                    InfoBarSeverity.Error);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "检查更新失败");
            ShowUpdateMessage("检查更新失败，请检查网络连接后重试。", InfoBarSeverity.Error);
        }
        finally
        {
            IsCheckingUpdate = false;
        }
    }

    [RelayCommand]
    private async Task ResetSettingsAsync()
    {
        var xamlRoot = (App.MainWindow.Content as FrameworkElement)?.XamlRoot;
        if (xamlRoot == null)
        {
            return;
        }

        var dialog = new SettingsResetDialog
        {
            XamlRoot = xamlRoot
        };

        var result = await dialog.ShowAsync();
        if (result != ContentDialogResult.Primary)
        {
            return;
        }

        await _localSettingsService.ResetAllAsync();
        Microsoft.UI.Xaml.Application.Current.Exit();
    }

    private static ElementTheme ElementThemeFromKey(string themeKey) => themeKey switch
    {
        "Light" => ElementTheme.Light,
        "Dark" => ElementTheme.Dark,
        _ => ElementTheme.Default,
    };

    private static string KeyFromElementTheme(ElementTheme theme) => theme switch
    {
        ElementTheme.Light => "Light",
        ElementTheme.Dark => "Dark",
        _ => "System",
    };

    private void ShowUpdateMessage(string message, InfoBarSeverity severity)
    {
        IsUpdateMessageOpen = false;
        UpdateMessage = message;
        UpdateMessageSeverity = severity;
        IsUpdateMessageOpen = true;
    }

    private static string GetAppVersionText()
    {
        Version version;

        if (RuntimeHelper.IsMSIX)
        {
            var packageVersion = Package.Current.Id.Version;

            version = new(packageVersion.Major, packageVersion.Minor, packageVersion.Build, packageVersion.Revision);
        }
        else
        {
            version = Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0, 0);
        }

        return FormatAppVersion(version);
    }

    internal static string FormatAppVersion(Version version)
    {
        return $"v{version.Major}.{version.Minor}.{Math.Max(0, version.Build)}.{Math.Max(0, version.Revision)}";
    }
}
