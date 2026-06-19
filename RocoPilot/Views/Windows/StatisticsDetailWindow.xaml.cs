using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;

using RocoPilot.Contracts.Services;
using RocoPilot.Helpers;

namespace RocoPilot.Views.Windows;

public sealed partial class StatisticsDetailWindow : WindowEx
{
    private readonly TaskCompletionSource _closedCompletionSource = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public StatisticsDetailWindow(
        string title,
        UIElement content,
        WindowEx owner)
    {
        InitializeComponent();

        ContentRoot.RequestedTheme = App.GetService<IThemeSelectorService>().Theme;
        DetailContentPresenter.Content = content;

        Title = title;
        AppWindow.Title = title;
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets/WindowIcon.ico"));
        AppWindow.TitleBar.PreferredTheme = TitleBarTheme.UseDefaultAppMode;
        WindowPlacementHelper.SetOwner(this, owner);
        WindowPlacementHelper.ResizeToContent(this, owner, ContentRoot, MinWidth, MinHeight);
        WindowPlacementHelper.CenterOnParent(this, owner);

        Closed += (_, _) => _closedCompletionSource.TrySetResult();
    }

    public Task ShowAsync()
    {
        Activate();
        return _closedCompletionSource.Task;
    }
}
