using System.Runtime.InteropServices;

using Microsoft.UI.Dispatching;
using Microsoft.UI.Text;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

using RocoPilot.Helpers;
using RocoPilot.Models.Capture;
using RocoPilot.Models.Overlay;

using Windows.Graphics;
using Windows.UI;

namespace RocoPilot.Views.Windows;

public sealed partial class InfoOverlayWindow : WindowEx
{
    private const int OverlayWidth = 320;
    private const int OverlayHeight = 288;
    private const int MinOverlayWidth = 260;
    private const int MinOverlayHeight = 240;
    private const int DefaultMargin = 16;
    private const int MaxVisibleCounters = 5;

    private static readonly TimeSpan FollowInterval = TimeSpan.FromMilliseconds(250);

    private readonly CaptureTargetWindow _targetWindow;
    private readonly DispatcherQueueTimer _followTimer;
    private readonly IntPtr _hwnd;

    private IDisposable? _messageHook;
    private RectInt32 _currentClientBounds;
    private RectInt32 _currentOverlayBounds;
    private WindowPoint _dragStartCursorPosition;
    private RectInt32 _dragStartOverlayBounds;
    private int _overlayOffsetX;
    private int _overlayOffsetY;
    private bool _hasActivated;
    private bool _hasUserPositioned;
    private bool _isLocked;
    private bool _isClosed;
    private bool _isDragging;
    private bool _isOverlayVisible;

    public InfoOverlayWindow(
        CaptureTargetWindow targetWindow,
        bool isLocked,
        bool isPollutionCounterEnabled,
        bool isAutoBattleEnabled)
    {
        _targetWindow = targetWindow;
        _isLocked = isLocked;

        InitializeComponent();
        ConfigureTaskIndicators(isPollutionCounterEnabled, isAutoBattleEnabled);

        SystemBackdrop = new TransparentTintBackdrop(Color.FromArgb(0, 0, 0, 0));
        Title = "RocoPilot Info Overlay";
        AppWindow.Title = Title;
        ConfigurePresenter();

        _hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        ApplyLockState(show: false);

        _followTimer = DispatcherQueue.GetForCurrentThread().CreateTimer();
        _followTimer.Interval = FollowInterval;
        _followTimer.Tick += FollowTimer_Tick;

        Closed += InfoOverlayWindow_Closed;
    }

    public void ShowOverlay()
    {
        if (_isClosed)
        {
            return;
        }

        if (UpdateOverlayState(forceMove: true))
        {
            ApplyLockState();
        }

        _followTimer.Start();
    }

    public void ResetPosition()
    {
        _hasUserPositioned = false;
        UpdateOverlayState(forceMove: true);
    }

    public void SetLocked(bool isLocked)
    {
        if (_isClosed || _isLocked == isLocked)
        {
            return;
        }

        _isLocked = isLocked;

        if (_isDragging)
        {
            _isDragging = false;
            OverlayRoot.ReleasePointerCaptures();
        }

        ApplyLockState(show: _isOverlayVisible);
        UpdateOverlayState(forceMove: true);
    }

    public void UpdateSnapshot(InfoOverlaySnapshot snapshot)
    {
        StatusText.Text = string.IsNullOrWhiteSpace(snapshot.StatusText)
            ? "状态待识别"
            : snapshot.StatusText;
        UpdatedAtText.Text = snapshot.UpdatedAt.ToLocalTime().ToString("HH:mm:ss");

        var visibleCounters = snapshot.Counters
            .OrderByDescending(counter => counter.LastCountedAt)
            .Take(MaxVisibleCounters)
            .ToList();

        var latestCounter = visibleCounters.FirstOrDefault();
        if (latestCounter is null)
        {
            LatestPollutionCountText.Text = "0";
            LatestCreatureNameText.Text = "暂无目标";
            RenderCounters([]);
            return;
        }

        LatestPollutionCountText.Text = latestCounter.PollutionCount.ToString();
        LatestCreatureNameText.Text = latestCounter.CreatureName;
        RenderCounters(visibleCounters.Skip(1).ToList());
    }

    private void ConfigureTaskIndicators(bool isPollutionCounterEnabled, bool isAutoBattleEnabled)
    {
        SetTaskIndicator(
            PollutionCounterIndicator,
            PollutionCounterIcon,
            isPollutionCounterEnabled,
            Color.FromArgb(0xFF, 0x10, 0xB9, 0x81),
            Color.FromArgb(0xFF, 0xE0, 0xF7, 0xF2));

        SetTaskIndicator(
            AutoBattleIndicator,
            AutoBattleIcon,
            isAutoBattleEnabled,
            Color.FromArgb(0xFF, 0x3B, 0x82, 0xF6),
            Color.FromArgb(0xFF, 0xE4, 0xEE, 0xFF));
    }

    private static void SetTaskIndicator(
        Border indicator,
        FontIcon icon,
        bool isEnabled,
        Color activeForeground,
        Color activeBackground)
    {
        indicator.Opacity = isEnabled ? 1d : 0.52d;
        indicator.Background = new SolidColorBrush(isEnabled
            ? activeBackground
            : Color.FromArgb(0xFF, 0xEE, 0xF1, 0xF5));
        icon.Foreground = new SolidColorBrush(isEnabled
            ? activeForeground
            : Color.FromArgb(0xFF, 0x8A, 0x95, 0xA3));
    }

    private void ConfigurePresenter()
    {
        if (AppWindow.Presenter is not OverlappedPresenter presenter)
        {
            presenter = OverlappedPresenter.Create();
            AppWindow.SetPresenter(presenter);
        }

        presenter.IsResizable = false;
        presenter.IsMaximizable = false;
        presenter.IsMinimizable = false;
        presenter.SetBorderAndTitleBar(false, false);
        AppWindow.IsShownInSwitchers = false;
    }

    private void ApplyLockState(bool show = true)
    {
        if (_isLocked && _messageHook is null)
        {
            _messageHook = TransparentOverlayWindowHelper.InstallMessageHook(_hwnd);
        }
        else if (!_isLocked && _messageHook is not null)
        {
            _messageHook.Dispose();
            _messageHook = null;
        }

        TransparentOverlayWindowHelper.ApplyTransparentOverlayStyles(_hwnd, topMost: true, passThrough: _isLocked, show);
    }

    private void FollowTimer_Tick(DispatcherQueueTimer sender, object args)
    {
        if (!_isDragging)
        {
            UpdateOverlayState();
        }
    }

    private bool UpdateOverlayState(bool forceMove = false)
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
        var overlaySize = GetOverlayPixelSize(clientBounds);
        var nextBounds = _hasUserPositioned
            ? ClampToClient(
                clientBounds,
                clientBounds.X + _overlayOffsetX,
                clientBounds.Y + _overlayOffsetY,
                overlaySize.Width,
                overlaySize.Height)
            : GetDefaultOverlayBounds(clientBounds, overlaySize);

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

    private SizeInt32 GetOverlayPixelSize(RectInt32 clientBounds)
    {
        var rasterizationScale = OverlayRoot.XamlRoot?.RasterizationScale ?? 1d;
        if (rasterizationScale <= 0)
        {
            rasterizationScale = 1d;
        }

        var width = (int)Math.Ceiling(OverlayWidth * rasterizationScale);
        var height = (int)Math.Ceiling(OverlayHeight * rasterizationScale);
        var availableWidth = Math.Max(120, clientBounds.Width - DefaultMargin * 2);
        var availableHeight = Math.Max(120, clientBounds.Height - DefaultMargin * 2);

        width = Math.Min(width, Math.Max(Math.Min(MinOverlayWidth, availableWidth), availableWidth));
        height = Math.Min(height, Math.Max(Math.Min(MinOverlayHeight, availableHeight), availableHeight));

        return new SizeInt32(width, height);
    }

    private static RectInt32 GetDefaultOverlayBounds(RectInt32 clientBounds, SizeInt32 overlaySize)
    {
        var x = clientBounds.X + clientBounds.Width - overlaySize.Width - DefaultMargin;
        var y = clientBounds.Y + (clientBounds.Height - overlaySize.Height) / 2;
        return ClampToClient(clientBounds, x, y, overlaySize.Width, overlaySize.Height);
    }

    private static RectInt32 ClampToClient(RectInt32 clientBounds, int x, int y, int width, int height)
    {
        var maxX = clientBounds.X + Math.Max(0, clientBounds.Width - width);
        var maxY = clientBounds.Y + Math.Max(0, clientBounds.Height - height);
        var clampedX = Math.Clamp(x, clientBounds.X, maxX);
        var clampedY = Math.Clamp(y, clientBounds.Y, maxY);
        return new RectInt32(clampedX, clampedY, width, height);
    }

    private void HideOverlay()
    {
        if (!_isOverlayVisible)
        {
            return;
        }

        TransparentOverlayWindowHelper.HideWindow(_hwnd);
        _isOverlayVisible = false;
    }

    private void RenderCounters(IReadOnlyList<InfoOverlayCounter> counters)
    {
        CounterList.Children.Clear();

        if (counters.Count == 0)
        {
            CounterList.Children.Add(new TextBlock
            {
                Text = "暂无其他污染目标",
                FontSize = 12,
                Foreground = new SolidColorBrush(Color.FromArgb(0xFF, 0x66, 0x70, 0x85))
            });
            return;
        }

        for (var index = 0; index < counters.Count; index++)
        {
            CounterList.Children.Add(CreateCounterRow(counters[index], index + 2));
        }
    }

    private static Border CreateCounterRow(InfoOverlayCounter counter, int rank)
    {
        var rowContent = new Grid
        {
            ColumnSpacing = 10,
            MinHeight = 28
        };

        rowContent.ColumnDefinitions.Add(new ColumnDefinition
        {
            Width = GridLength.Auto
        });
        rowContent.ColumnDefinitions.Add(new ColumnDefinition
        {
            Width = new GridLength(1, GridUnitType.Star)
        });
        rowContent.ColumnDefinitions.Add(new ColumnDefinition
        {
            Width = GridLength.Auto
        });

        var row = new Border
        {
            Padding = new Thickness(9, 5, 9, 5),
            Background = new SolidColorBrush(Color.FromArgb(0xB8, 0xFF, 0xFF, 0xFF)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x45, 0xD8, 0xDE, 0xE8)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Child = rowContent
        };

        var rankBadge = new Border
        {
            Width = 22,
            Height = 22,
            Background = new SolidColorBrush(Color.FromArgb(0xFF, 0xF2, 0xF4, 0xF7)),
            CornerRadius = new CornerRadius(11),
            Child = new TextBlock
            {
                Text = rank.ToString(),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                FontSize = 11,
                FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(Color.FromArgb(0xFF, 0x66, 0x70, 0x85))
            }
        };

        var name = new TextBlock
        {
            Text = counter.CreatureName,
            FontSize = 13,
            Foreground = new SolidColorBrush(Color.FromArgb(0xFF, 0x1F, 0x29, 0x37)),
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center
        };

        var countBadge = new Border
        {
            MinWidth = 44,
            Padding = new Thickness(8, 3, 8, 3),
            Background = new SolidColorBrush(Color.FromArgb(0xFF, 0xEC, 0xFD, 0xF7)),
            CornerRadius = new CornerRadius(10),
            Child = new TextBlock
            {
                Text = counter.PollutionCount.ToString(),
                HorizontalAlignment = HorizontalAlignment.Center,
                FontSize = 12,
                FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(Color.FromArgb(0xFF, 0x0F, 0x76, 0x6E))
            }
        };

        Grid.SetColumn(rankBadge, 0);
        Grid.SetColumn(name, 1);
        Grid.SetColumn(countBadge, 2);
        rowContent.Children.Add(rankBadge);
        rowContent.Children.Add(name);
        rowContent.Children.Add(countBadge);
        return row;
    }

    private void OverlayRoot_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (_isLocked || _isClosed || !GetCursorPos(out _dragStartCursorPosition))
        {
            return;
        }

        _dragStartOverlayBounds = _currentOverlayBounds;
        _isDragging = true;
        OverlayRoot.CapturePointer(e.Pointer);
        e.Handled = true;
    }

    private void OverlayRoot_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_isDragging || _isLocked || _isClosed || !GetCursorPos(out var cursorPosition))
        {
            return;
        }

        var x = _dragStartOverlayBounds.X + cursorPosition.X - _dragStartCursorPosition.X;
        var y = _dragStartOverlayBounds.Y + cursorPosition.Y - _dragStartCursorPosition.Y;
        var nextBounds = ClampToClient(
            _currentClientBounds,
            x,
            y,
            _dragStartOverlayBounds.Width,
            _dragStartOverlayBounds.Height);

        _hasUserPositioned = true;
        _overlayOffsetX = nextBounds.X - _currentClientBounds.X;
        _overlayOffsetY = nextBounds.Y - _currentClientBounds.Y;
        _currentOverlayBounds = nextBounds;

        AppWindow.MoveAndResize(nextBounds);
        TransparentOverlayWindowHelper.MoveTopMostNoActivate(_hwnd, nextBounds);
        e.Handled = true;
    }

    private void OverlayRoot_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!_isDragging)
        {
            return;
        }

        _isDragging = false;
        OverlayRoot.ReleasePointerCapture(e.Pointer);
        e.Handled = true;
    }

    private void InfoOverlayWindow_Closed(object sender, WindowEventArgs args)
    {
        _isClosed = true;
        _followTimer.Stop();
        _messageHook?.Dispose();
    }

    private static bool SameBounds(RectInt32 left, RectInt32 right)
    {
        return left.X == right.X
            && left.Y == right.Y
            && left.Width == right.Width
            && left.Height == right.Height;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out WindowPoint point);

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowPoint
    {
        public int X;
        public int Y;
    }
}
