using System.Runtime.InteropServices;

using Microsoft.UI.Dispatching;
using Microsoft.UI.Text;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

using RocoPilot.Helpers;
using RocoPilot.Models.Capture;
using RocoPilot.Models.Overlay;

using Windows.Graphics;
using Windows.Foundation;
using Windows.UI;

namespace RocoPilot.Views.Windows;

public sealed partial class InfoOverlayWindow : WindowEx
{
    private const int OverlayWidth = 344;
    private const int MinOverlayHeight = 160;
    private const int DefaultMargin = 16;
    private const int MaxVisibleCounters = 5;

    private static readonly TimeSpan FollowInterval = TimeSpan.FromMilliseconds(250);
    private static readonly Color ActiveTaskIndicatorForeground = Color.FromArgb(0xFF, 0x34, 0xD3, 0x99);
    private static readonly Color ActiveTaskIndicatorBackground = Color.FromArgb(0x29, 0x34, 0xD3, 0x99);
    private static readonly Color DisabledIndicatorForeground = Color.FromArgb(0xFF, 0x8B, 0x95, 0xA1);
    private static readonly Color DisabledIndicatorBackground = Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF);
    private static readonly Color DisabledIndicatorBorder = Color.FromArgb(0x24, 0xFF, 0xFF, 0xFF);
    private static readonly Color CounterPrimaryForeground = Color.FromArgb(0xFF, 0xF6, 0xF4, 0xF8);
    private static readonly Color CounterSecondaryForeground = Color.FromArgb(0xFF, 0x9B, 0x9B, 0xAA);
    private static readonly Color CounterAccentForeground = Color.FromArgb(0xFF, 0xDE, 0xA6, 0xEA);
    private static readonly Color InactiveMagicPointForeground = Color.FromArgb(0xFF, 0x44, 0x40, 0x4B);

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
    private int _lastMagicPointCount;
    private int _lastMagicPointMaximum = 6;
    private int _renderedMagicPointCount = -1;
    private int _renderedMagicPointMaximum = -1;
    private IReadOnlyList<InfoOverlayCounter>? _renderedCounters;
    private IReadOnlyDictionary<string, string> _avatarPaths = new Dictionary<string, string>();
    private readonly Dictionary<string, BitmapImage> _avatarImages = new(StringComparer.OrdinalIgnoreCase);
    private bool _hasActivated;
    private bool _hasUserPositioned;
    private bool _isLocked;
    private bool _isClosed;
    private bool _isDragging;
    private bool _isOverlayVisible;

    public InfoOverlayWindow(
        CaptureTargetWindow targetWindow,
        bool isLocked,
        bool isEncounterStatisticsEnabled,
        bool isAutoBattleEnabled)
    {
        _targetWindow = targetWindow;
        _isLocked = isLocked;

        InitializeComponent();
        UpdateTaskIndicators(isEncounterStatisticsEnabled, isAutoBattleEnabled);

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
        if (snapshot.PendingShinyCapture is null)
        {
            PendingShinyAlert.Visibility = Visibility.Collapsed;
            PendingShinyAlertText.Text = string.Empty;
        }
        else
        {
            PendingShinyAlert.Visibility = Visibility.Visible;
            PendingShinyAlertText.Text = $"{snapshot.PendingShinyCapture.CreatureName} · 等待统计页面确认";
        }

        var statusText = string.IsNullOrWhiteSpace(snapshot.StatusText)
            ? "状态待识别"
            : snapshot.StatusText;
        if (snapshot.MagicPointCount.HasValue)
        {
            var magicPointMaximum = Math.Max(1, snapshot.MagicPointMaximum);
            _lastMagicPointMaximum = magicPointMaximum;
            _lastMagicPointCount = Math.Clamp(snapshot.MagicPointCount.Value, 0, magicPointMaximum);
        }

        StatusText.Text = statusText;
        StatusText.Foreground = new SolidColorBrush(ActiveTaskIndicatorForeground);
        MagicPointText.Text = $"{_lastMagicPointCount}/{_lastMagicPointMaximum}";
        UpdateMagicPointDots();
        var recordTime = snapshot.LatestRecordUpdatedAt;
        RecentRecordTimeText.Visibility = recordTime.HasValue ? Visibility.Visible : Visibility.Collapsed;
        RecentRecordTimeText.Text = recordTime.HasValue
            ? $"最新记录更新于 {recordTime.Value.ToLocalTime():HH:mm:ss}"
            : string.Empty;
        ToolTipService.SetToolTip(RecentRecordTimeText,
            recordTime?.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"));

        var visibleCounters = snapshot.Counters
            .OrderByDescending(counter => counter.LastCountedAt)
            .Take(MaxVisibleCounters)
            .ToList();

        RenderCounters(visibleCounters);
    }

    public void SetAvatarPaths(IReadOnlyDictionary<string, string> avatarPaths)
    {
        if (_isClosed) return;
        _avatarPaths = avatarPaths;
        _avatarImages.Clear();
        RenderCounters(_renderedCounters ?? [], force: true);
    }

    private void UpdateMagicPointDots()
    {
        if (_renderedMagicPointCount == _lastMagicPointCount
            && _renderedMagicPointMaximum == _lastMagicPointMaximum) return;
        _renderedMagicPointCount = _lastMagicPointCount;
        _renderedMagicPointMaximum = _lastMagicPointMaximum;
        MagicPointDots.Children.Clear();
        for (var index = 0; index < _lastMagicPointMaximum; index++)
        {
            MagicPointDots.Children.Add(new Border
            {
                Width = 7,
                Height = 7,
                CornerRadius = new CornerRadius(3.5),
                Background = new SolidColorBrush(index < _lastMagicPointCount
                    ? CounterAccentForeground : InactiveMagicPointForeground)
            });
        }
    }

    public void UpdateTaskIndicators(bool isEncounterStatisticsEnabled, bool isAutoBattleEnabled)
    {
        SetTaskIndicator(
            PollutionCounterIndicator,
            PollutionCounterIcon,
            isEncounterStatisticsEnabled,
            ActiveTaskIndicatorForeground,
            ActiveTaskIndicatorBackground);

        SetTaskIndicator(
            AutoBattleIndicator,
            AutoBattleIcon,
            isAutoBattleEnabled,
            ActiveTaskIndicatorForeground,
            ActiveTaskIndicatorBackground);
    }

    private static void SetTaskIndicator(
        Border indicator,
        FontIcon icon,
        bool isEnabled,
        Color activeForeground,
        Color activeBackground)
    {
        indicator.Opacity = isEnabled ? 1d : 0.72d;
        indicator.Background = new SolidColorBrush(isEnabled
            ? activeBackground
            : DisabledIndicatorBackground);
        indicator.BorderBrush = new SolidColorBrush(isEnabled
            ? WithAlpha(activeForeground, 0x66)
            : DisabledIndicatorBorder);
        icon.Foreground = new SolidColorBrush(isEnabled
            ? activeForeground
            : DisabledIndicatorForeground);
    }

    private static Color WithAlpha(Color color, byte alpha)
    {
        return Color.FromArgb(alpha, color.R, color.G, color.B);
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

        var availableWidth = Math.Max(120, clientBounds.Width - DefaultMargin * 2);
        var availableHeight = Math.Max(120, clientBounds.Height - DefaultMargin * 2);
        var width = Math.Min((int)Math.Ceiling(OverlayWidth * rasterizationScale), availableWidth);
        // 测量主体的自然高度；顶部提醒由 Notices 单独预留空间。
        InfoPanel.Measure(new Size(width / rasterizationScale, double.PositiveInfinity));
        var height = Math.Min(availableHeight, (int)Math.Ceiling(
            Math.Max(MinOverlayHeight, InfoPanel.DesiredSize.Height) * rasterizationScale));

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

    private void RenderCounters(IReadOnlyList<InfoOverlayCounter> counters, bool force = false)
    {
        if (!force && _renderedCounters is not null && _renderedCounters.SequenceEqual(counters)) return;
        _renderedCounters = counters.ToArray();
        CounterList.Children.Clear();
        if (counters.Count == 0)
        {
            CounterList.Children.Add(new TextBlock
            {
                Text = "暂无捕捉记录",
                FontSize = 12,
                Foreground = new SolidColorBrush(CounterSecondaryForeground)
            });
            return;
        }
        for (var index = 0; index < counters.Count; index++)
        {
            CounterList.Children.Add(CreateCounterRow(counters[index], index + 1));
        }
    }

    private Grid CreateCounterRow(InfoOverlayCounter counter, int rank)
    {
        var featured = rank == 1;
        var row = new Grid { ColumnSpacing = 8, Height = featured ? 40 : 26 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(20) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(36) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.Children.Add(new TextBlock
        {
            Text = rank.ToString("00"),
            FontSize = 10,
            FontWeight = featured ? FontWeights.SemiBold : FontWeights.Normal,
            Foreground = new SolidColorBrush(featured ? CounterAccentForeground : CounterSecondaryForeground),
            VerticalAlignment = VerticalAlignment.Center
        });
        var avatar = CreateCounterAvatar(counter.CreatureName, featured ? 36 : 24);
        Grid.SetColumn(avatar, 1);
        row.Children.Add(avatar);
        var name = new TextBlock
        {
            Text = counter.CreatureName,
            FontSize = featured ? 15 : 12,
            FontWeight = featured ? FontWeights.SemiBold : FontWeights.Normal,
            Foreground = new SolidColorBrush(CounterPrimaryForeground),
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxLines = 1,
            VerticalAlignment = VerticalAlignment.Center
        };
        ToolTipService.SetToolTip(name, counter.CreatureName);
        Grid.SetColumn(name, 2);
        row.Children.Add(name);
        var count = new TextBlock
        {
            Text = counter.PollutionCount.ToString(),
            FontSize = featured ? GetFeaturedCounterFontSize(counter.PollutionCount) : 14,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(CounterAccentForeground),
            VerticalAlignment = VerticalAlignment.Center,
            TextAlignment = TextAlignment.Right
        };
        Grid.SetColumn(count, 3);
        row.Children.Add(count);
        return row;
    }

    private Border CreateCounterAvatar(string creatureName, double size)
    {
        var avatar = new Border
        {
            Width = size,
            Height = size,
            VerticalAlignment = VerticalAlignment.Center,
            CornerRadius = new CornerRadius(size > 30 ? 10 : 6),
            Background = new SolidColorBrush(Color.FromArgb(0x26, 0xFF, 0xFF, 0xFF)),
            Child = CreateAvatarPlaceholder(size)
        };
        var key = TextMatchingHelper.NormalizeSpiritNameForMatching(creatureName);
        if (_avatarPaths.TryGetValue(key, out var path) && Uri.TryCreate(path, UriKind.Absolute, out var uri))
        {
            if (!_avatarImages.TryGetValue(path, out var source))
            {
                source = new BitmapImage(uri);
                _avatarImages[path] = source;
            }
            var image = new Image { Width = size - 4, Height = size - 4, Stretch = Stretch.Uniform, Source = source };
            image.ImageFailed += (_, _) => avatar.Child = CreateAvatarPlaceholder(size);
            avatar.Child = image;
        }
        return avatar;
    }

    private static FontIcon CreateAvatarPlaceholder(double size) => new()
    {
        Glyph = "\uE77B",
        FontFamily = new FontFamily("Segoe Fluent Icons"),
        FontSize = size / 2,
        Foreground = new SolidColorBrush(CounterSecondaryForeground)
    };

    private static double GetFeaturedCounterFontSize(int count) => count switch
    {
        >= 10000 => 18,
        >= 1000 => 20,
        _ => 24
    };

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
