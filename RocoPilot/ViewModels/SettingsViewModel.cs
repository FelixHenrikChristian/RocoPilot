using System.Diagnostics;
using System.IO;
using System.Reflection;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

using RocoPilot.Configuration;
using RocoPilot.Contracts.Services;
using RocoPilot.Helpers;
using RocoPilot.Settings;

using Windows.ApplicationModel;

namespace RocoPilot.ViewModels;

public partial class SettingsViewModel : ObservableRecipient
{
    private readonly IThemeSelectorService _themeSelectorService;
    private readonly ILocalSettingsService _localSettingsService;
    private readonly ILogger<SettingsViewModel> _logger;

    private bool _suppressThemeChange;
    private bool _suppressDiagnosticModePersist;

    public ThemeOption[] ThemeOptions { get; } =
    {
        new ThemeOption { ThemeKey = "System", Name = "跟随系统" },
        new ThemeOption { ThemeKey = "Light", Name = "浅色" },
        new ThemeOption { ThemeKey = "Dark", Name = "深色" },
    };

    [ObservableProperty]
    private ThemeOption? _selectedThemeOption;

    [ObservableProperty]
    private bool _diagnosticMode;

    [ObservableProperty]
    private string _versionDescription;

    public SettingsViewModel(
        IThemeSelectorService themeSelectorService,
        ILocalSettingsService localSettingsService,
        ILogger<SettingsViewModel> logger)
    {
        _themeSelectorService = themeSelectorService;
        _localSettingsService = localSettingsService;
        _logger = logger;
        _versionDescription = GetVersionDescription();
    }

    public async Task LoadAsync()
    {
        _suppressDiagnosticModePersist = true;
        try
        {
            var savedDiag = await _localSettingsService.ReadSettingAsync<bool?>(SettingsKeys.DiagnosticMode);
            DiagnosticMode = savedDiag ?? false;
        }
        finally
        {
            _suppressDiagnosticModePersist = false;
        }

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

    partial void OnDiagnosticModeChanged(bool value)
    {
        LoggingHelper.SetDiagnosticMode(value);

        if (_suppressDiagnosticModePersist)
        {
            return;
        }

        _ = _localSettingsService.SaveSettingAsync(SettingsKeys.DiagnosticMode, value);
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
    private async Task ResetSettingsAsync()
    {
        var xamlRoot = (App.MainWindow.Content as FrameworkElement)?.XamlRoot;
        if (xamlRoot == null)
        {
            return;
        }

        var dialog = new ContentDialog
        {
            XamlRoot = xamlRoot,
            Title = "重置设置",
            Content = "将清除所有用户偏好，并立即关闭应用。是否继续？",
            PrimaryButtonText = "重置并退出",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
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

    private static string GetVersionDescription()
    {
        Version version;

        if (RuntimeHelper.IsMSIX)
        {
            var packageVersion = Package.Current.Id.Version;

            version = new(packageVersion.Major, packageVersion.Minor, packageVersion.Build, packageVersion.Revision);
        }
        else
        {
            version = Assembly.GetExecutingAssembly().GetName().Version!;
        }

        return $"{"AppDisplayName".GetLocalized()} - {version.Major}.{version.Minor}.{version.Build}.{version.Revision}";
    }
}
