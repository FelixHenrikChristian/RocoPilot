using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

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
        void FitContent()
        {
            // Measure the content directly: the window root and ScrollViewer can
            // report a desired size constrained by the existing client area.
            if (content is FlipView flipView &&
                flipView.ContainerFromIndex(flipView.SelectedIndex) is FlipViewItem container &&
                container.ContentTemplateRoot is Border { Child: FrameworkElement body } card)
            {
                var border = card.BorderThickness;
                var padding = card.Padding;
                var width = card.Width - border.Left - border.Right - padding.Left - padding.Right;
                body.Measure(new global::Windows.Foundation.Size(width, double.PositiveInfinity));
                flipView.Height = Math.Ceiling(body.DesiredSize.Height + border.Top + border.Bottom
                    + padding.Top + padding.Bottom + card.Margin.Top + card.Margin.Bottom);
            }

            content.Measure(new global::Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
            var inset = ContentRoot.Padding;
            var size = new global::Windows.Foundation.Size(
                content.DesiredSize.Width + inset.Left + inset.Right,
                content.DesiredSize.Height + inset.Top + inset.Bottom);
            WindowPlacementHelper.ResizeToContent(this, owner, size, MinWidth, MinHeight);
            WindowPlacementHelper.CenterOnParent(this, owner);
        }

        FitContent();
        ContentRoot.Loaded += (_, _) => DispatcherQueue.TryEnqueue(FitContent);

        if (content is FlipView pages)
        {
            // Containers can be realized after the window's Loaded event.
            var awaitingInitialLayout = true;
            pages.LayoutUpdated += OnPagesLayoutUpdated;
            void OnPagesLayoutUpdated(object? sender, object args)
            {
                if (!awaitingInitialLayout ||
                    pages.ContainerFromIndex(pages.SelectedIndex) is not FlipViewItem { ContentTemplateRoot: Border })
                {
                    return;
                }

                awaitingInitialLayout = false;
                pages.LayoutUpdated -= OnPagesLayoutUpdated;
                DispatcherQueue.TryEnqueue(FitContent);
            }
            pages.SelectionChanged += (_, _) => DispatcherQueue.TryEnqueue(FitContent);
        }

        Closed += (_, _) => _closedCompletionSource.TrySetResult();
    }

    public Task ShowAsync()
    {
        Activate();
        return _closedCompletionSource.Task;
    }
}
