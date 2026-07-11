using Microsoft.UI.Dispatching;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

using RocoPilot.Helpers;
using RocoPilot.Models.Overlay;

using Windows.Graphics;
using Windows.UI;

namespace RocoPilot.Views.Windows;

public sealed partial class InfoOverlayWindow
{
    private const double TopNoticeHeight = 64d;
    private const double TopNoticeSpacing = 8d;

    private bool _isTopNoticeLayoutInitialized;
    private int _lastTopNoticeExtraPixelHeight;
    private StackPanel? _topNoticePanel;
    private Border? _uidNoticeAlert;
    private TextBlock? _uidNoticeTitleText;
    private TextBlock? _uidNoticeMessageText;

    public void InitializeTopNoticeLayout()
    {
        if (_isTopNoticeLayoutInitialized)
        {
            return;
        }

        _isTopNoticeLayoutInitialized = true;

        if (InfoPanel.Child is Grid infoPanelContent)
        {
            infoPanelContent.Children.Remove(PendingShinyAlert);
        }

        OverlayRoot.Children.Remove(InfoPanel);

        PendingShinyAlert.Height = TopNoticeHeight;
        PendingShinyAlert.Margin = new Thickness(0);
        PendingShinyAlert.Padding = new Thickness(12, 8, 12, 8);
        PendingShinyAlert.Background = CreateNoticeBrush(0xE6, 0x2B, 0x24, 0x16);
        PendingShinyAlert.BorderBrush = CreateNoticeBrush(0xCC, 0xF5, 0x9E, 0x0B);

        _uidNoticeAlert = CreateUidNoticeAlert();
        _topNoticePanel = new StackPanel
        {
            Margin = new Thickness(0, 0, 0, TopNoticeSpacing),
            Spacing = TopNoticeSpacing,
            Visibility = Visibility.Collapsed
        };
        _topNoticePanel.Children.Add(_uidNoticeAlert);
        _topNoticePanel.Children.Add(PendingShinyAlert);

        var layout = new Grid();
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        layout.Children.Add(_topNoticePanel);
        Grid.SetRow(InfoPanel, 1);
        layout.Children.Add(InfoPanel);
        OverlayRoot.Children.Add(layout);

        _followTimer.Tick -= FollowTimer_Tick;
        _followTimer.Tick += TopNoticeFollowTimer_Tick;
        UpdateTopNoticePanelVisibility();
    }

    public void UpdateSnapshotWithTopNotices(InfoOverlaySnapshot snapshot)
    {
        InitializeTopNoticeLayout();
        UpdateSnapshot(snapshot);
        RefreshTopNoticeLayout();
    }

    public void UpdateUidNotice(InfoOverlayNotice? notice)
    {
        InitializeTopNoticeLayout();
        if (_uidNoticeAlert is null
            || _uidNoticeTitleText is null
            || _uidNoticeMessageText is null)
        {
            return;
        }

        if (notice is null)
        {
            _uidNoticeAlert.Visibility = Visibility.Collapsed;
            _uidNoticeTitleText.Text = string.Empty;
            _uidNoticeMessageText.Text = string.Empty;
        }
        else
        {
            _uidNoticeTitleText.Text = notice.Title;
            _uidNoticeMessageText.Text = notice.Message;
            _uidNoticeAlert.Visibility = Visibility.Visible;
        }

        RefreshTopNoticeLayout();
    }

    public void RefreshTopNoticeLayout()
    {
        if (!_isTopNoticeLayoutInitialized)
        {
            return;
        }

        UpdateTopNoticePanelVisibility();
        if (_hasActivated)
        {
            UpdateOverlayStateWithTopNotices(forceMove: true);
        }
    }

    private Border CreateUidNoticeAlert()
    {
        var icon = new FontIcon
        {
            Width = 26,
            Height = 26,
            VerticalAlignment = VerticalAlignment.Center,
            FontSize = 15,
            Foreground = CreateNoticeBrush(0xFF, 0x93, 0xC5, 0xFD),
            Glyph = "\uE946"
        };

        _uidNoticeTitleText = new TextBlock
        {
            FontSize = 11,
            FontWeight = FontWeights.SemiBold,
            Foreground = CreateNoticeBrush(0xFF, 0xBF, 0xDB, 0xFE),
            Text = "确认统计账号"
        };
        _uidNoticeMessageText = new TextBlock
        {
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            Foreground = CreateNoticeBrush(0xFF, 0xEF, 0xF6, 0xFF),
            MaxLines = 1,
            TextTrimming = TextTrimming.CharacterEllipsis,
            TextWrapping = TextWrapping.NoWrap
        };

        var text = new StackPanel
        {
            VerticalAlignment = VerticalAlignment.Center,
            Spacing = 2
        };
        text.Children.Add(_uidNoticeTitleText);
        text.Children.Add(_uidNoticeMessageText);

        var content = new Grid
        {
            ColumnSpacing = 10
        };
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        content.Children.Add(icon);
        Grid.SetColumn(text, 1);
        content.Children.Add(text);

        return new Border
        {
            Height = TopNoticeHeight,
            Padding = new Thickness(12, 8, 12, 8),
            Background = CreateNoticeBrush(0xE6, 0x1D, 0x27, 0x33),
            BorderBrush = CreateNoticeBrush(0xCC, 0x60, 0xA5, 0xFA),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Visibility = Visibility.Collapsed,
            Child = content
        };
    }

    private void TopNoticeFollowTimer_Tick(DispatcherQueueTimer sender, object args)
    {
        if (!_isDragging)
        {
            UpdateOverlayStateWithTopNotices();
        }
    }

    private bool UpdateOverlayStateWithTopNotices(bool forceMove = false)
    {
        if (_isClosed
            || !TransparentOverlayWindowHelper.IsForegroundWindow(_targetWindow.Hwnd)
            || !TransparentOverlayWindowHelper.TryGetClientScreenBounds(_targetWindow.Hwnd, out var clientBounds)
            || clientBounds.Width <= 0
            || clientBounds.Height <= 0)
        {
            HideOverlay();
            return false;
        }

        _currentClientBounds = clientBounds;
        var baseOverlaySize = GetOverlayPixelSize(clientBounds);
        var topNoticeExtraHeight = GetTopNoticeExtraPixelHeight(clientBounds, baseOverlaySize.Height);
        if (_hasUserPositioned && topNoticeExtraHeight != _lastTopNoticeExtraPixelHeight)
        {
            _overlayOffsetY -= topNoticeExtraHeight - _lastTopNoticeExtraPixelHeight;
        }

        _lastTopNoticeExtraPixelHeight = topNoticeExtraHeight;
        var overlaySize = new SizeInt32(
            baseOverlaySize.Width,
            baseOverlaySize.Height + topNoticeExtraHeight);
        var nextBounds = _hasUserPositioned
            ? ClampToClient(
                clientBounds,
                clientBounds.X + _overlayOffsetX,
                clientBounds.Y + _overlayOffsetY,
                overlaySize.Width,
                overlaySize.Height)
            : GetDefaultBoundsWithTopNotices(
                clientBounds,
                baseOverlaySize,
                topNoticeExtraHeight);

        if (!_hasActivated)
        {
            AppWindow.MoveAndResize(nextBounds);
            Activate();
            _hasActivated = true;
            TransparentOverlayWindowHelper.ApplyTransparentOverlayStyles(_hwnd, topMost: true, passThrough: _isLocked);
        }

        if (forceMove || !SameBounds(_currentOverlayBounds, nextBounds))
        {
            AppWindow.MoveAndResize(nextBounds);
            _currentOverlayBounds = nextBounds;
        }

        TransparentOverlayWindowHelper.MoveTopMostNoActivate(_hwnd, nextBounds);
        _currentOverlayBounds = nextBounds;
        _isOverlayVisible = true;
        return true;
    }

    private RectInt32 GetDefaultBoundsWithTopNotices(
        RectInt32 clientBounds,
        SizeInt32 baseOverlaySize,
        int topNoticeExtraHeight)
    {
        var baseBounds = GetDefaultOverlayBounds(clientBounds, baseOverlaySize);
        return ClampToClient(
            clientBounds,
            baseBounds.X,
            baseBounds.Y - topNoticeExtraHeight,
            baseBounds.Width,
            baseBounds.Height + topNoticeExtraHeight);
    }

    private int GetTopNoticeExtraPixelHeight(RectInt32 clientBounds, int baseOverlayHeight)
    {
        var visibleNoticeCount = GetVisibleTopNoticeCount();
        if (visibleNoticeCount == 0)
        {
            return 0;
        }

        var rasterizationScale = OverlayRoot.XamlRoot?.RasterizationScale ?? 1d;
        if (rasterizationScale <= 0)
        {
            rasterizationScale = 1d;
        }

        var extraHeightInDips = visibleNoticeCount * TopNoticeHeight
            + visibleNoticeCount * TopNoticeSpacing;
        var desiredExtraHeight = (int)Math.Ceiling(extraHeightInDips * rasterizationScale);
        var availableHeight = Math.Max(120, clientBounds.Height - DefaultMargin * 2);
        return Math.Min(desiredExtraHeight, Math.Max(0, availableHeight - baseOverlayHeight));
    }

    private void UpdateTopNoticePanelVisibility()
    {
        if (_topNoticePanel is not null)
        {
            _topNoticePanel.Visibility = GetVisibleTopNoticeCount() > 0
                ? Visibility.Visible
                : Visibility.Collapsed;
        }
    }

    private int GetVisibleTopNoticeCount()
    {
        var count = _uidNoticeAlert?.Visibility == Visibility.Visible ? 1 : 0;
        if (PendingShinyAlert.Visibility == Visibility.Visible)
        {
            count++;
        }

        return count;
    }

    private static SolidColorBrush CreateNoticeBrush(byte alpha, byte red, byte green, byte blue)
    {
        return new SolidColorBrush(Color.FromArgb(alpha, red, green, blue));
    }
}
