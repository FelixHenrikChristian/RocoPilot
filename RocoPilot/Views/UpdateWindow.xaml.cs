using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

using Microsoft.Web.WebView2.Core;

using RocoPilot.Contracts.Services;
using RocoPilot.Helpers;
using RocoPilot.Models;

using Serilog;

using Windows.Graphics;

namespace RocoPilot.Views;

public sealed partial class UpdateWindow : WindowEx
{
    private const string WebView2UserDataFolderEnvironmentVariable = "WEBVIEW2_USER_DATA_FOLDER";

    public enum UpdateResult
    {
        Cancel,
        Update,
        Download,
    }

    private readonly GitHubRelease _release;
    private UpdateResult _result = UpdateResult.Cancel;
    private TaskCompletionSource<UpdateResult>? _completion;
    private Task? _initializeWebViewTask;
    private bool _isClosed;

    public UpdateWindow(GitHubRelease release)
    {
        _release = release;
        InitializeComponent();

        RootHost.RequestedTheme = App.GetService<IThemeSelectorService>().Theme;

        Title = "发现新版本";
        AppWindow.Title = Title;
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets/WindowIcon.ico"));
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(TitleBarRoot);
        AppWindow.Resize(new SizeInt32(800, 800));

        VersionTitleText.Text = $"发现新版本 {_release.TagName}";
        PublishDateText.Text = $"发布时间：{_release.PublishedAt:yyyy年MM月dd日}";

        ReleaseNotesWebView.CoreWebView2Initialized += OnReleaseNotesWebViewCoreWebView2Initialized;
        ReleaseNotesWebView.CoreProcessFailed += OnReleaseNotesWebViewCoreProcessFailed;
        RootHost.ActualThemeChanged += OnRootHostActualThemeChanged;
        RootHost.Loaded += OnRootHostLoaded;
    }

    public Task<UpdateResult> ShowAsync()
    {
        _completion = new TaskCompletionSource<UpdateResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        Closed += OnWindowClosed;

        WindowPlacementHelper.SetOwner(this, App.MainWindow);
        WindowPlacementHelper.CenterOnParent(this, App.MainWindow);
        Activate();
        return _completion.Task;
    }

    private void OnWindowClosed(object sender, WindowEventArgs args)
    {
        _isClosed = true;
        Closed -= OnWindowClosed;
        RootHost.ActualThemeChanged -= OnRootHostActualThemeChanged;
        RootHost.Loaded -= OnRootHostLoaded;
        ReleaseNotesWebView.CoreWebView2Initialized -= OnReleaseNotesWebViewCoreWebView2Initialized;
        ReleaseNotesWebView.CoreProcessFailed -= OnReleaseNotesWebViewCoreProcessFailed;
        CloseReleaseNotesWebView();
        _completion?.TrySetResult(_result);
        _completion = null;
    }

    private async void OnRootHostLoaded(object sender, RoutedEventArgs e)
    {
        RootHost.Loaded -= OnRootHostLoaded;
        TitleBarHelper.UpdateTitleBar(this, RootHost.ActualTheme);
        await RenderReleaseNotesAsync();
    }

    private async void OnRootHostActualThemeChanged(FrameworkElement sender, object args)
    {
        TitleBarHelper.UpdateTitleBar(this, RootHost.ActualTheme);
        await RenderReleaseNotesAsync();
    }

    private async Task RenderReleaseNotesAsync()
    {
        var isDarkTheme = ReleaseNotesHtmlHelper.ResolveIsDarkTheme(RootHost);
        var html = ReleaseNotesHtmlHelper.GenerateReleaseNotesHtml(_release.Body, isDarkTheme);

        try
        {
            await EnsureReleaseNotesWebViewAsync();
            if (_isClosed)
            {
                return;
            }

            ReleaseNotesFallbackHost.Visibility = Visibility.Collapsed;
            ReleaseNotesWebView.Visibility = Visibility.Visible;
            ReleaseNotesWebView.NavigateToString(html);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "更新日志 WebView2 初始化或导航失败，改用纯文本回退。");
            ShowReleaseNotesFallback();
        }
    }

    private Task EnsureReleaseNotesWebViewAsync()
    {
        _initializeWebViewTask ??= InitializeReleaseNotesWebViewAsync();
        return _initializeWebViewTask;
    }

    private async Task InitializeReleaseNotesWebViewAsync()
    {
        var userDataFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "RocoPilot",
            "WebView2");

        Directory.CreateDirectory(userDataFolder);
        Environment.SetEnvironmentVariable(
            WebView2UserDataFolderEnvironmentVariable,
            userDataFolder,
            EnvironmentVariableTarget.Process);

        await ReleaseNotesWebView.EnsureCoreWebView2Async();

        if (ReleaseNotesWebView.CoreWebView2 != null)
        {
            ReleaseNotesWebView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
            ReleaseNotesWebView.CoreWebView2.Settings.AreDevToolsEnabled = false;
        }
    }

    private void ShowReleaseNotesFallback()
    {
        if (_isClosed)
        {
            return;
        }

        ReleaseNotesWebView.Visibility = Visibility.Collapsed;
        ReleaseNotesFallbackHost.Visibility = Visibility.Visible;
        ReleaseNotesFallbackText.Text = string.IsNullOrWhiteSpace(_release.Body)
            ? "此版本没有提供更新日志。"
            : _release.Body.Trim();
    }

    private void OnReleaseNotesWebViewCoreWebView2Initialized(WebView2 sender, CoreWebView2InitializedEventArgs args)
    {
        if (args.Exception != null)
        {
            Log.Warning(args.Exception, "更新日志 WebView2 Core 初始化失败。");
        }
    }

    private void OnReleaseNotesWebViewCoreProcessFailed(WebView2 sender, CoreWebView2ProcessFailedEventArgs args)
    {
        Log.Warning("更新日志 WebView2 Core 进程异常退出。Kind={Kind}, Reason={Reason}, ExitCode={ExitCode}",
            args.ProcessFailedKind,
            args.Reason,
            args.ExitCode);

        ShowReleaseNotesFallback();
    }

    private void CloseReleaseNotesWebView()
    {
        try
        {
            ReleaseNotesWebView.Close();
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "关闭更新日志 WebView2 时发生异常。");
        }
    }

    private void Complete(UpdateResult result)
    {
        if (_isClosed)
        {
            return;
        }

        _result = result;
        Close();
    }

    private void UpdateButton_Click(object sender, RoutedEventArgs e)
    {
        Complete(UpdateResult.Update);
    }

    private void DownloadButton_Click(object sender, RoutedEventArgs e)
    {
        Complete(UpdateResult.Download);
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        Complete(UpdateResult.Cancel);
    }
}
