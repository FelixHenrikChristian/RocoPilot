using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using RocoPilot.Models.Overlay;
using Windows.UI;

namespace RocoPilot.Views.Windows;

public sealed partial class InfoOverlayWindow
{
    private DispatcherQueueTimer? _islandAnimationTimer;
    private InfoOverlaySnapshot _islandSnapshot = InfoOverlaySnapshot.CreateInitial(DateTimeOffset.Now);
    private InfoOverlayNotice? _uidNotice;
    private InfoOverlayIslandPresentation? _islandPresentation;
    private const double CollapsedIslandWidth = 278, CollapsedIslandHeight = 46, ExpandedIslandWidth = 360;
    private double _islandWidth = CollapsedIslandWidth, _islandHeight = CollapsedIslandHeight;
    private double _animationFromWidth, _animationFromHeight, _animationToWidth = CollapsedIslandWidth, _animationToHeight = CollapsedIslandHeight;
    private DateTimeOffset _islandAnimationStartedAt;
    private bool _islandExpanding;

    private void InitializeIsland()
    {
        _islandAnimationTimer = DispatcherQueue.GetForCurrentThread().CreateTimer();
        _islandAnimationTimer.Interval = TimeSpan.FromMilliseconds(16);
        _islandAnimationTimer.Tick += (_, _) =>
        {
            var t = Math.Clamp((DateTimeOffset.UtcNow - _islandAnimationStartedAt).TotalMilliseconds / 280, 0, 1);
            var eased = 1 - Math.Pow(1 - t, 3);
            _islandWidth = _animationFromWidth + (_animationToWidth - _animationFromWidth) * eased;
            _islandHeight = _animationFromHeight + (_animationToHeight - _animationFromHeight) * eased;
            IslandDetails.Opacity = _islandExpanding ? Math.Clamp((t - 0.25) / 0.75, 0, 1) : 1 - t;
            if (t >= 1)
            {
                _islandAnimationTimer.Stop();
                if (!_islandExpanding) IslandDetails.Visibility = Visibility.Collapsed;
            }
            if (!_isDragging) UpdateOverlayState(forceMove: true);
        };
    }

    private void UpdateIslandSnapshot(InfoOverlaySnapshot snapshot)
    {
        _islandSnapshot = snapshot;
        IslandStatusText.Text = snapshot.MainStatusText;
        ToolTipService.SetToolTip(IslandStatusText, snapshot.MainStatusText);
        if (snapshot.MagicPointCount.HasValue)
        {
            _lastMagicPointMaximum = Math.Max(1, snapshot.MagicPointMaximum);
            _lastMagicPointCount = Math.Clamp(snapshot.MagicPointCount.Value, 0, _lastMagicPointMaximum);
            IslandMagicText.Text = $"{_lastMagicPointCount} / {_lastMagicPointMaximum}";
        }
        RefreshIslandPresentation();
    }

    public void UpdateUidNotice(InfoOverlayNotice? notice)
    {
        _uidNotice = notice;
        if (_isIsland && !_isClosed) RefreshIslandPresentation();
    }

    private void RefreshIslandPresentation()
    {
        var presentation = InfoOverlayIslandPresentation.Resolve(_islandSnapshot, _uidNotice, DateTimeOffset.Now);
        if (presentation == _islandPresentation) return;
        var wasExpanded = _islandPresentation is not null;
        _islandPresentation = presentation;
        var warning = presentation?.IsWarning == true;
        var error = presentation?.IsError == true;
        var tint = warning ? Color.FromArgb(255, 251, 191, 36)
            : error ? Color.FromArgb(255, 255, 145, 160) : IslandStatusForeground;
        IslandStatusDot.Background = new SolidColorBrush(tint);
        if (presentation is not null)
        {
            IslandCategoryText.Text = presentation.Category;
            IslandCategoryText.Foreground = new SolidColorBrush(warning || error ? tint : CounterSecondaryForeground);
            IslandTitleText.Text = presentation.Title;
            IslandDescriptionText.Text = presentation.Description;
            IslandDescriptionText.Visibility = string.IsNullOrWhiteSpace(presentation.Description) ? Visibility.Collapsed : Visibility.Visible;
            ToolTipService.SetToolTip(IslandDetails, string.Join("\n", new[] { presentation.Category, presentation.Title, presentation.Description }
                .Where(text => !string.IsNullOrWhiteSpace(text))));
            RefreshIslandAvatar();
            IslandDetails.Visibility = Visibility.Visible;
        }
        else ToolTipService.SetToolTip(IslandDetails, null);
        // 恢复原来的展开尺寸，为精灵名与两行保护提醒保留空间。
        if (wasExpanded == (presentation is not null)) return;
        _islandExpanding = presentation is not null;
        _animationFromWidth = _islandWidth; _animationFromHeight = _islandHeight;
        _animationToWidth = _islandExpanding ? ExpandedIslandWidth : CollapsedIslandWidth;
        _animationToHeight = _islandExpanding ? 132 : CollapsedIslandHeight;
        IslandPanel.CornerRadius = new CornerRadius(_islandExpanding ? 22 : 23);
        _islandAnimationStartedAt = DateTimeOffset.UtcNow;
        _islandAnimationTimer?.Start();
    }

    private void RefreshIslandAvatar()
    {
        if (_islandPresentation is not { } presentation) return;
        IslandAvatar.Child = string.IsNullOrWhiteSpace(presentation.CreatureName)
            ? new FontIcon { FontFamily = new FontFamily("Segoe Fluent Icons"), FontSize = 23,
                Glyph = presentation.IsWarning || presentation.IsError ? "\uE7BA" : "\uF272",
                Foreground = new SolidColorBrush(CounterAccentForeground) }
            : CreateCounterAvatar(presentation.CreatureName, 40);
    }
}
