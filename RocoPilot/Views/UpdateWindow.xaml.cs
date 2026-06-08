using Microsoft.UI.Xaml;

using RocoPilot.Contracts.Services;
using RocoPilot.Helpers;
using RocoPilot.Models;

using Windows.Graphics;

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
    private UpdateResult _result = UpdateResult.Cancel;
    private TaskCompletionSource<UpdateResult>? _completion;
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

        RootHost.ActualThemeChanged += OnRootHostActualThemeChanged;
        RootHost.Loaded += OnRootHostLoaded;
    }

    public Task<UpdateResult> ShowAsync()
    {
        _completion = new TaskCompletionSource<UpdateResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        Closed += OnWindowClosed;

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
        _completion?.TrySetResult(_result);
        _completion = null;
    }

    private void OnRootHostLoaded(object sender, RoutedEventArgs e)
    {
        RootHost.Loaded -= OnRootHostLoaded;
        TitleBarHelper.UpdateTitleBar(this, RootHost.ActualTheme);
        RenderReleaseNotes();
    }

    private void OnRootHostActualThemeChanged(FrameworkElement sender, object args)
    {
        TitleBarHelper.UpdateTitleBar(this, RootHost.ActualTheme);
        RenderReleaseNotes();
    }

    private void RenderReleaseNotes()
    {
        var isDarkTheme = ReleaseNotesHtmlHelper.ResolveIsDarkTheme(RootHost);
        var html = ReleaseNotesHtmlHelper.GenerateReleaseNotesHtml(_release.Body, isDarkTheme);
        ReleaseNotesWebView.NavigateToString(html);
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
