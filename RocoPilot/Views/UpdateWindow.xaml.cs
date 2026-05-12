using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;

using RocoPilot.Contracts.Services;
using RocoPilot.Helpers;
using RocoPilot.Models;

using WinUIEx;

namespace RocoPilot.Views;

public sealed partial class UpdateWindow : WindowEx
{
    public enum UpdateResult
    {
        Cancel,
        Update,
        Download,
    }

    private readonly GitHubRelease _release;
    private readonly ILogger<UpdateWindow> _logger = App.GetService<ILogger<UpdateWindow>>();
    private UpdateResult _result = UpdateResult.Cancel;
    private TaskCompletionSource<UpdateResult>? _completion;

    public UpdateWindow(GitHubRelease release)
    {
        _release = release;
        InitializeComponent();

        RootHost.RequestedTheme = App.GetService<IThemeSelectorService>().Theme;

        Title = "发现新版本";
        VersionTitleText.Text = $"发现新版本 {_release.TagName}";
        PublishDateText.Text = $"发布时间：{_release.PublishedAt:yyyy年MM月dd日}";

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(TitleBarRoot);
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets/WindowIcon.ico"));

        RootHost.ActualThemeChanged += (_, _) => TitleBarHelper.UpdateTitleBar(this, RootHost.ActualTheme);
        RootHost.Loaded += OnRootLoaded;
    }

    public Task<UpdateResult> ShowAsync()
    {
        _completion = new TaskCompletionSource<UpdateResult>();
        Closed += OnWindowClosed;

        WindowPlacementHelper.CenterOnParent(this, App.MainWindow);
        Activate();
        return _completion.Task;
    }

    private void OnRootLoaded(object sender, RoutedEventArgs e)
    {
        TitleBarHelper.UpdateTitleBar(this, RootHost.ActualTheme);
        _ = InitializeWebViewAsync();
    }

    private void OnWindowClosed(object sender, WindowEventArgs args)
    {
        Closed -= OnWindowClosed;
        _completion?.TrySetResult(_result);
        _completion = null;
    }

    private async Task InitializeWebViewAsync()
    {
        try
        {
            await ReleaseNotesWebView.EnsureCoreWebView2Async();
            var settings = ReleaseNotesWebView.CoreWebView2.Settings;
            settings.IsGeneralAutofillEnabled = false;
            settings.IsPasswordAutosaveEnabled = false;
            settings.AreDevToolsEnabled = false;
            settings.AreDefaultContextMenusEnabled = false;
            settings.AreHostObjectsAllowed = false;
            settings.IsWebMessageEnabled = false;
            await LoadReleaseNotesAsync();
        }
        catch (Exception ex)
        {
            if (string.Equals(
                    ex.GetType().FullName,
                    "Microsoft.Web.WebView2.Core.WebView2RuntimeNotFoundException",
                    StringComparison.Ordinal))
            {
                _logger.LogWarning("未检测到 WebView2 运行时，更新日志将无法显示");
                ShowReleaseNotesFallback(
                    "WebView2 运行时未安装",
                    "无法显示更新内容。你仍然可以使用下方按钮自动更新，或前往 GitHub 手动下载。");
                return;
            }

            _logger.LogWarning(ex, "WebView2 初始化失败");
            ShowReleaseNotesFallback(
                "更新内容暂时不可用",
                "无法初始化更新日志视图。你仍然可以使用下方按钮自动更新，或前往 GitHub 手动下载。");
        }
    }

    private async Task LoadReleaseNotesAsync()
    {
        try
        {
            var isDark = ReleaseNotesHtmlHelper.ResolveIsDarkTheme(RootHost);
            var html = await ReleaseNotesHtmlHelper.GenerateReleaseNotesHtmlAsync(_release.Body, isDark, _logger);
            ReleaseNotesWebView.NavigateToString(html);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "加载更新日志失败，使用降级显示");
            await LoadFallbackContentAsync();
        }
    }

    private Task LoadFallbackContentAsync()
    {
        var isDark = ReleaseNotesHtmlHelper.ResolveIsDarkTheme(RootHost);
        var html = ReleaseNotesHtmlHelper.GenerateFallbackHtml(isDark);
        ReleaseNotesWebView.NavigateToString(html);
        return Task.CompletedTask;
    }

    private void ShowReleaseNotesFallback(string title, string message)
    {
        ReleaseNotesWebView.Visibility = Visibility.Collapsed;
        ReleaseNotesFallbackTitleText.Text = title;
        ReleaseNotesFallbackMessageText.Text = message;
        ReleaseNotesFallbackHost.Visibility = Visibility.Visible;
    }

    private void UpdateButton_Click(object sender, RoutedEventArgs e)
    {
        _result = UpdateResult.Update;
        Close();
    }

    private void DownloadButton_Click(object sender, RoutedEventArgs e)
    {
        _result = UpdateResult.Download;
        Close();
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        _result = UpdateResult.Cancel;
        Close();
    }
}
