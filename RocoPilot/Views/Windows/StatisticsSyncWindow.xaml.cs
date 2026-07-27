using System.ComponentModel;

using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

using RocoPilot.Contracts.Services;
using RocoPilot.Helpers;
using RocoPilot.Models.Statistics;
using RocoPilot.ViewModels;

using Windows.Graphics;
using Windows.UI;

namespace RocoPilot.Views.Windows;

public sealed partial class StatisticsSyncWindow : WindowEx
{
    private readonly StatisticsViewModel _viewModel;
    private readonly IThemeSelectorService _themeSelectorService;
    private IReadOnlyList<StatisticsSyncProviderOption> _providers = [];
    private StatisticsSyncProviderOption? _selectedProvider;
    private StatisticsSyncTutorialWindow? _tutorialWindow;
    private StatisticsSyncSettings _settings = new();
    private bool _isLoaded;
    private bool _isApplyingSettings;

    public StatisticsSyncWindow(StatisticsViewModel viewModel)
    {
        _viewModel = viewModel;
        _themeSelectorService = App.GetService<IThemeSelectorService>();

        InitializeComponent();

        ContentRoot.RequestedTheme = _themeSelectorService.Theme;

        Title = "统计云同步";
        AppWindow.Title = Title;
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets/WindowIcon.ico"));
        AppWindow.TitleBar.PreferredTheme = TitleBarTheme.UseDefaultAppMode;
        AppWindow.Resize(new SizeInt32(680, 620));

        _viewModel.PropertyChanged += ViewModel_PropertyChanged;
        Closed += StatisticsSyncWindow_Closed;
        UpdateStatus();
    }

    private async void ContentRoot_Loaded(object sender, RoutedEventArgs e)
    {
        if (_isLoaded)
        {
            return;
        }

        _isLoaded = true;
        _providers = _viewModel.SyncProviders;
        ProviderComboBox.ItemsSource = _providers;

        try
        {
            _settings = await _viewModel.LoadSyncSettingsAsync();
            ApplySettings(_settings);
            UpdateStatus();
        }
        catch (Exception ex)
        {
            ShowMessage("读取云同步设置失败", ex.Message, InfoBarSeverity.Error);
        }
    }

    private void StatisticsSyncWindow_Closed(object sender, WindowEventArgs args)
    {
        _viewModel.PropertyChanged -= ViewModel_PropertyChanged;
        Closed -= StatisticsSyncWindow_Closed;
        _tutorialWindow?.Close();
        _tutorialWindow = null;
    }

    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(StatisticsViewModel.SyncStatusSummary)
            or nameof(StatisticsViewModel.IsSyncBusy))
        {
            UpdateStatus();
        }
    }

    private void ApplySettings(StatisticsSyncSettings settings)
    {
        _isApplyingSettings = true;
        try
        {
            _selectedProvider = ResolveSettingsProvider(settings);
            ProviderComboBox.SelectedItem = _selectedProvider;
            EnableSyncSwitch.IsOn = settings.IsEnabled;
            R2AccountIdTextBox.Text = settings.Endpoint;
            R2BucketNameTextBox.Text = settings.BucketName;
            UserNameTextBox.Text = settings.UserName;
        }
        finally
        {
            _isApplyingSettings = false;
        }
    }

    private void ProviderComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isApplyingSettings)
        {
            return;
        }

        _selectedProvider = ProviderComboBox.SelectedItem as StatisticsSyncProviderOption;
    }

    private async void SaveSettingsButton_Click(object sender, RoutedEventArgs e)
    {
        await RunOperationAsync("保存云同步设置失败", async () =>
        {
            var settings = BuildSettingsFromForm();
            var password = string.IsNullOrWhiteSpace(PasswordBox.Password) ? null : PasswordBox.Password;
            await _viewModel.SaveSyncSettingsAsync(settings, password);
            PasswordBox.Password = string.Empty;
            ShowMessage("云同步设置已保存", _viewModel.SyncStatusSummary, InfoBarSeverity.Success);
        });
    }

    private async void TestConnectionButton_Click(object sender, RoutedEventArgs e)
    {
        await RunOperationAsync("测试云同步连接失败", async () =>
        {
            await _viewModel.TestSyncConnectionAsync();
            ShowMessage("云同步连接成功", _viewModel.SyncStatusSummary, InfoBarSeverity.Success);
        });
    }

    private async void RefreshRemoteButton_Click(object sender, RoutedEventArgs e)
    {
        await RunOperationAsync("刷新云端时间失败", async () =>
        {
            await _viewModel.RefreshSyncRemoteInfoAsync();
            ShowMessage("已刷新云端时间", _viewModel.SyncStatusSummary, InfoBarSeverity.Success);
        });
    }

    private async void UploadButton_Click(object sender, RoutedEventArgs e)
    {
        await RunOperationAsync("上传到云端失败", async () =>
        {
            await _viewModel.UploadStatisticsToCloudAsync();
            ShowMessage("上传完成", _viewModel.SyncStatusSummary, InfoBarSeverity.Success);
        });
    }

    private async void DownloadButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = ContentRoot.XamlRoot,
            Title = "合并云端数据",
            Content = "将根据上次同步状态合并云端数据：仅本机变化的账号保留本机版本，仅云端变化的账号采用云端版本。若同一账号在两台设备同时修改，将采用云端版本。",
            PrimaryButtonText = "合并",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        await RunOperationAsync("合并云端数据失败", async () =>
        {
            await _viewModel.DownloadStatisticsFromCloudAsync();
            ShowMessage("合并完成", "已将云端统计数据合并到本地记录。", InfoBarSeverity.Success);
        });
    }

    private void TutorialButton_Click(object sender, RoutedEventArgs e)
    {
        if (_tutorialWindow is not null)
        {
            _tutorialWindow.Activate();
            return;
        }

        _tutorialWindow = new StatisticsSyncTutorialWindow();
        _tutorialWindow.Closed += TutorialWindow_Closed;
        WindowPlacementHelper.SetOwner(_tutorialWindow, this);
        WindowPlacementHelper.CenterOnParent(_tutorialWindow, this);
        _tutorialWindow.Activate();
    }

    private void TutorialWindow_Closed(object sender, WindowEventArgs args)
    {
        if (_tutorialWindow is not null)
        {
            _tutorialWindow.Closed -= TutorialWindow_Closed;
            _tutorialWindow = null;
        }
    }

    private void EnableSyncSwitch_Toggled(object sender, RoutedEventArgs e)
    {
        UpdateStatus();
    }

    private StatisticsSyncSettings BuildSettingsFromForm()
    {
        var provider = ResolveFormProvider();
        var isEnabled = EnableSyncSwitch.IsOn;
        var isSameProvider = string.Equals(provider.Id, _settings.ProviderId, StringComparison.OrdinalIgnoreCase);

        return new StatisticsSyncSettings
        {
            IsEnabled = isEnabled,
            ProviderId = provider.Id,
            ProviderKind = provider.Kind,
            Endpoint = R2AccountIdTextBox.Text,
            RemotePath = isSameProvider && !string.IsNullOrWhiteSpace(_settings.RemotePath)
                ? _settings.RemotePath
                : provider.DefaultRemotePath,
            BucketName = R2BucketNameTextBox.Text,
            UserName = UserNameTextBox.Text,
            LastUploadedAt = _settings.LastUploadedAt,
            LastDownloadedAt = _settings.LastDownloadedAt,
            LastRemoteCheckedAt = _settings.LastRemoteCheckedAt,
            LastRemoteModifiedAt = _settings.LastRemoteModifiedAt,
            LastRemoteEntityTag = _settings.LastRemoteEntityTag,
            LastSyncedRemoteModifiedAt = _settings.LastSyncedRemoteModifiedAt,
            LastSyncedRemoteEntityTag = _settings.LastSyncedRemoteEntityTag
        };
    }

    private StatisticsSyncProviderOption ResolveFormProvider()
    {
        return ProviderComboBox.SelectedItem as StatisticsSyncProviderOption
            ?? _selectedProvider
            ?? ResolveSettingsProvider(_settings);
    }

    private StatisticsSyncProviderOption ResolveSettingsProvider(StatisticsSyncSettings settings)
    {
        return _providers.FirstOrDefault(provider =>
            string.Equals(provider.Id, settings.ProviderId, StringComparison.OrdinalIgnoreCase))
            ?? _providers.FirstOrDefault()
            ?? new StatisticsSyncProviderOption();
    }

    private async Task RunOperationAsync(string failureTitle, Func<Task> operation)
    {
        try
        {
            SetActionsEnabled(false);
            await operation();
            _settings = await _viewModel.LoadSyncSettingsAsync();
            ApplySettings(_settings);
        }
        catch (Exception ex)
        {
            ShowMessage(failureTitle, ex.Message, InfoBarSeverity.Error);
            _settings = await _viewModel.LoadSyncSettingsAsync();
            ApplySettings(_settings);
        }
        finally
        {
            SetActionsEnabled(true);
            UpdateStatus();
        }
    }

    private void SetActionsEnabled(bool isEnabled)
    {
        SaveSettingsButton.IsEnabled = isEnabled;
        TestConnectionButton.IsEnabled = isEnabled;
        RefreshRemoteButton.IsEnabled = isEnabled;
        UploadButton.IsEnabled = isEnabled;
        DownloadButton.IsEnabled = isEnabled;
    }

    private void UpdateStatus()
    {
        StatusSummaryText.Text = _viewModel.SyncStatusSummary;
        StatusBadgeText.Text = BuildStatusBadgeText();
        UpdateStatusBadgeBrushes();
        SetActionsEnabled(!_viewModel.IsSyncBusy);
    }

    private string BuildStatusBadgeText()
    {
        if (_viewModel.IsSyncBusy)
        {
            return "同步中";
        }

        return EnableSyncSwitch.IsOn ? "已启用" : "未启用";
    }

    private void UpdateStatusBadgeBrushes()
    {
        if (_viewModel.IsSyncBusy)
        {
            StatusBadge.Background = new SolidColorBrush(Color.FromArgb(0x22, 0x00, 0x78, 0xD4));
            StatusBadgeText.Foreground = new SolidColorBrush(Color.FromArgb(0xFF, 0x00, 0x78, 0xD4));
            return;
        }

        if (EnableSyncSwitch.IsOn)
        {
            StatusBadge.Background = new SolidColorBrush(Color.FromArgb(0x22, 0x10, 0x7C, 0x10));
            StatusBadgeText.Foreground = new SolidColorBrush(Color.FromArgb(0xFF, 0x10, 0x7C, 0x10));
            return;
        }

        StatusBadge.Background = (Brush)Application.Current.Resources["ControlFillColorDefaultBrush"];
        StatusBadgeText.Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"];
    }

    private void ShowMessage(string title, string message, InfoBarSeverity severity)
    {
        MessageBar.Severity = severity;
        MessageBar.Title = title;
        MessageBar.Message = message;
        MessageBar.IsOpen = false;
        MessageBar.IsOpen = true;
    }
}
