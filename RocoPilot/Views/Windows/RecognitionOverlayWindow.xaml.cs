using System.Globalization;

using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;

using RocoPilot.Helpers;
using RocoPilot.Models.Capture;
using RocoPilot.Models.Recognition;

using Windows.Graphics;
using Windows.UI;

namespace RocoPilot.Views.Windows;

public sealed partial class RecognitionOverlayWindow : WindowEx
{
    private static readonly TimeSpan FollowInterval = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan VisualStateInterval = TimeSpan.FromMilliseconds(80);
    private static readonly TimeSpan FlashDuration = TimeSpan.FromMilliseconds(120);
    private static readonly TimeSpan ResultTextDuration = TimeSpan.FromSeconds(4);

    private readonly CaptureTargetWindow _targetWindow;
    private readonly RecognitionRegionConfig _regionConfig;
    private readonly DispatcherQueueTimer _followTimer;
    private readonly DispatcherQueueTimer _visualStateTimer;
    private readonly Dictionary<string, RegionVisualState> _regionVisualStates = new(StringComparer.OrdinalIgnoreCase);
    private readonly IntPtr _hwnd;
    private readonly IDisposable _messageHook;

    private RectInt32 _currentClientBounds;
    private bool _hasActivated;
    private bool _isLoaded;
    private bool _isClosed;
    private bool _isOverlayVisible;

    public RecognitionOverlayWindow(CaptureTargetWindow targetWindow, RecognitionRegionConfig regionConfig)
    {
        _targetWindow = targetWindow;
        _regionConfig = regionConfig;

        InitializeComponent();

        SystemBackdrop = new TransparentTintBackdrop(Color.FromArgb(0, 0, 0, 0));
        Title = "RocoPilot Recognition Overlay";
        AppWindow.Title = Title;
        ConfigurePresenter();

        _hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        _messageHook = TransparentOverlayWindowHelper.InstallMessageHook(_hwnd);
        TransparentOverlayWindowHelper.ApplyTransparentOverlayStyles(_hwnd, topMost: true, passThrough: true, show: false);

        _followTimer = DispatcherQueue.GetForCurrentThread().CreateTimer();
        _followTimer.Interval = FollowInterval;
        _followTimer.Tick += FollowTimer_Tick;

        _visualStateTimer = DispatcherQueue.GetForCurrentThread().CreateTimer();
        _visualStateTimer.Interval = VisualStateInterval;
        _visualStateTimer.Tick += VisualStateTimer_Tick;

        OverlayRoot.Loaded += OverlayRoot_Loaded;
        OverlayRoot.SizeChanged += OverlayRoot_SizeChanged;
        Closed += RecognitionOverlayWindow_Closed;
    }

    public void ShowOverlay()
    {
        if (_isClosed)
        {
            return;
        }

        if (UpdateOverlayState())
        {
            TransparentOverlayWindowHelper.ApplyTransparentOverlayStyles(_hwnd, topMost: true, passThrough: true);
        }

        _followTimer.Start();
        DrawRegions();
    }

    public void ShowOcrResult(string regionId, string text)
    {
        if (_isClosed || string.IsNullOrWhiteSpace(regionId))
        {
            return;
        }

        var now = DateTimeOffset.Now;
        var state = GetRegionVisualState(regionId);
        state.OcrText = text ?? string.Empty;
        state.OcrTextExpiresAt = string.IsNullOrWhiteSpace(text)
            ? now
            : now + ResultTextDuration;
        state.FlashUntil = now + FlashDuration;

        _visualStateTimer.Start();
        DrawRegions();
    }

    public void ShowImageMatchResult(string regionId, double score)
    {
        if (_isClosed || string.IsNullOrWhiteSpace(regionId))
        {
            return;
        }

        var now = DateTimeOffset.Now;
        var state = GetRegionVisualState(regionId);
        state.ImageMatchScore = NormalizeScore(score);
        state.ImageMatchExpiresAt = now + ResultTextDuration;
        state.FlashUntil = now + FlashDuration;

        _visualStateTimer.Start();
        DrawRegions();
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

    private void OverlayRoot_Loaded(object sender, RoutedEventArgs e)
    {
        _isLoaded = true;
        _ = UpdateOverlayState();
        DrawRegions();
    }

    private void OverlayRoot_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        DrawRegions();
    }

    private void FollowTimer_Tick(DispatcherQueueTimer sender, object args)
    {
        _ = UpdateOverlayState();
    }

    private void VisualStateTimer_Tick(DispatcherQueueTimer sender, object args)
    {
        DrawRegions();
        if (!HasActiveVisualStates(DateTimeOffset.Now))
        {
            _visualStateTimer.Stop();
        }
    }

    private bool UpdateOverlayState()
    {
        if (_isClosed
            || !TransparentOverlayWindowHelper.IsForegroundWindow(_targetWindow.Hwnd)
            || !TransparentOverlayWindowHelper.TryGetClientScreenBounds(_targetWindow.Hwnd, out var bounds)
            || bounds.Width <= 0
            || bounds.Height <= 0)
        {
            HideOverlay();
            return false;
        }

        if (!_hasActivated)
        {
            _currentClientBounds = bounds;
            TransparentOverlayWindowHelper.MoveTopMostNoActivate(_hwnd, bounds);
            _hasActivated = true;
            TransparentOverlayWindowHelper.ApplyTransparentOverlayStyles(_hwnd, topMost: true, passThrough: true);
        }

        if (!SameBounds(_currentClientBounds, bounds))
        {
            _currentClientBounds = bounds;
            TransparentOverlayWindowHelper.MoveNoActivate(_hwnd, bounds);
            DrawRegions();
        }

        TransparentOverlayWindowHelper.MoveNoActivate(_hwnd, bounds);
        _isOverlayVisible = true;
        return true;
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

    private void DrawRegions()
    {
        if (!_isLoaded || _isClosed || _currentClientBounds.Width <= 0 || _currentClientBounds.Height <= 0)
        {
            return;
        }

        var rasterizationScale = OverlayRoot.XamlRoot?.RasterizationScale ?? 1d;
        if (rasterizationScale <= 0)
        {
            rasterizationScale = 1d;
        }

        var canvasWidth = _currentClientBounds.Width / rasterizationScale;
        var canvasHeight = _currentClientBounds.Height / rasterizationScale;
        RegionCanvas.Width = canvasWidth;
        RegionCanvas.Height = canvasHeight;
        RegionCanvas.Children.Clear();
        var now = DateTimeOffset.Now;
        TrimExpiredVisualStates(now);

        var configWidth = _regionConfig.ResolutionWidth > 0
            ? _regionConfig.ResolutionWidth
            : _currentClientBounds.Width;
        var configHeight = _regionConfig.ResolutionHeight > 0
            ? _regionConfig.ResolutionHeight
            : _currentClientBounds.Height;

        var widthScale = (double)_currentClientBounds.Width / configWidth / rasterizationScale;
        var heightScale = (double)_currentClientBounds.Height / configHeight / rasterizationScale;

        foreach (var region in _regionConfig.Regions.Where(IsDrawableRegion))
        {
            _ = _regionVisualStates.TryGetValue(region.Id, out var visualState);
            AddRegion(region, widthScale, heightScale, canvasWidth, canvasHeight, visualState, now);
        }
    }

    private void AddRegion(
        RecognitionRegion region,
        double widthScale,
        double heightScale,
        double canvasWidth,
        double canvasHeight,
        RegionVisualState? visualState,
        DateTimeOffset now)
    {
        var x = region.X * widthScale;
        var y = region.Y * heightScale;
        var width = region.Width * widthScale;
        var height = region.Height * heightScale;

        if (width <= 0 || height <= 0)
        {
            return;
        }

        var isFlashing = visualState?.IsFlashing(now) == true;
        var color = isFlashing
            ? Color.FromArgb(0xFF, 0xFF, 0xD7, 0x2F)
            : Color.FromArgb(0xFF, 0x2F, 0xD7, 0xFF);
        var strokeBrush = new SolidColorBrush(color);
        var fillBrush = new SolidColorBrush(Color.FromArgb(
            isFlashing ? (byte)0x36 : (byte)0x18,
            color.R,
            color.G,
            color.B));
        var outline = new Rectangle();

        outline.Width = width;
        outline.Height = height;
        outline.Stroke = strokeBrush;
        outline.StrokeThickness = isFlashing ? 3 : 2;
        outline.Fill = fillBrush;

        Canvas.SetLeft(outline, x);
        Canvas.SetTop(outline, y);
        RegionCanvas.Children.Add(outline);

        if (!string.IsNullOrWhiteSpace(region.Id))
        {
            AddLabel(region.Id, strokeBrush, x, y, width, canvasWidth, canvasHeight);
        }

        if (visualState is null)
        {
            return;
        }

        if (visualState.HasImageMatch(now) && visualState.ImageMatchScore.HasValue)
        {
            AddImageMatchLabel(
                visualState.ImageMatchScore.Value,
                strokeBrush,
                x,
                y,
                height,
                canvasWidth,
                canvasHeight);
        }

        if (visualState.HasOcrText(now))
        {
            AddOcrText(
                visualState.OcrText!,
                strokeBrush,
                x,
                y,
                width,
                height,
                canvasWidth,
                canvasHeight);
        }
    }

    private void AddLabel(
        string label,
        Brush borderBrush,
        double x,
        double y,
        double width,
        double canvasWidth,
        double canvasHeight)
    {
        var labelMaxWidth = Math.Max(80, Math.Min(Math.Max(width, 120), canvasWidth - x - 8));
        var labelTop = y >= 24 ? y - 24 : Math.Min(y + 4, Math.Max(0, canvasHeight - 24));
        var labelElement = new Border
        {
            MaxWidth = labelMaxWidth,
            Padding = new Thickness(6, 2, 6, 2),
            Background = new SolidColorBrush(Color.FromArgb(0xCC, 0x00, 0x00, 0x00)),
            BorderBrush = borderBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Child = new TextBlock
            {
                Text = label,
                FontSize = 11,
                Foreground = new SolidColorBrush(Microsoft.UI.Colors.White),
                TextTrimming = TextTrimming.CharacterEllipsis
            }
        };

        Canvas.SetLeft(labelElement, Math.Max(0, Math.Min(x, canvasWidth - 8)));
        Canvas.SetTop(labelElement, labelTop);
        RegionCanvas.Children.Add(labelElement);
    }

    private void AddImageMatchLabel(
        double score,
        Brush borderBrush,
        double x,
        double y,
        double height,
        double canvasWidth,
        double canvasHeight)
    {
        const double labelHeight = 20;
        const double labelWidth = 44;
        var labelElement = new Border
        {
            MinWidth = labelWidth,
            Padding = new Thickness(5, 1, 5, 1),
            Background = new SolidColorBrush(Color.FromArgb(0xD8, 0x00, 0x00, 0x00)),
            BorderBrush = borderBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Child = new TextBlock
            {
                Text = FormatPercent(score),
                FontSize = 11,
                Foreground = new SolidColorBrush(Microsoft.UI.Colors.White),
                TextTrimming = TextTrimming.CharacterEllipsis
            }
        };

        var labelLeft = Math.Max(0, Math.Min(x + 4, Math.Max(0, canvasWidth - labelWidth - 4)));
        var labelTop = Math.Max(
            0,
            Math.Min(y + height - labelHeight - 4, Math.Max(0, canvasHeight - labelHeight - 4)));
        Canvas.SetLeft(labelElement, labelLeft);
        Canvas.SetTop(labelElement, labelTop);
        RegionCanvas.Children.Add(labelElement);
    }

    private void AddOcrText(
        string text,
        Brush borderBrush,
        double x,
        double y,
        double width,
        double height,
        double canvasWidth,
        double canvasHeight)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        var maximumCanvasLabelWidth = Math.Max(0, canvasWidth - 16);
        if (maximumCanvasLabelWidth <= 0)
        {
            return;
        }

        var labelMaxWidth = Math.Min(Math.Max(width, 180), maximumCanvasLabelWidth);
        var labelLeft = Math.Max(0, Math.Min(x, Math.Max(0, canvasWidth - labelMaxWidth - 8)));
        var labelTop = y + height + 4;
        if (labelTop > canvasHeight - 30)
        {
            labelTop = Math.Max(0, y - 54);
        }

        var labelElement = new Border
        {
            MaxWidth = labelMaxWidth,
            Padding = new Thickness(6, 3, 6, 3),
            Background = new SolidColorBrush(Color.FromArgb(0xD8, 0x00, 0x00, 0x00)),
            BorderBrush = borderBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Child = new TextBlock
            {
                Text = text.Trim(),
                FontSize = 12,
                Foreground = new SolidColorBrush(Microsoft.UI.Colors.White),
                MaxLines = 3,
                TextWrapping = TextWrapping.Wrap,
                TextTrimming = TextTrimming.WordEllipsis
            }
        };

        Canvas.SetLeft(labelElement, labelLeft);
        Canvas.SetTop(labelElement, labelTop);
        RegionCanvas.Children.Add(labelElement);
    }

    private void RecognitionOverlayWindow_Closed(object sender, WindowEventArgs args)
    {
        _isClosed = true;
        _followTimer.Stop();
        _visualStateTimer.Stop();
        _messageHook.Dispose();
    }

    private RegionVisualState GetRegionVisualState(string regionId)
    {
        var normalizedRegionId = regionId.Trim();
        if (!_regionVisualStates.TryGetValue(normalizedRegionId, out var state))
        {
            state = new RegionVisualState();
            _regionVisualStates[normalizedRegionId] = state;
        }

        return state;
    }

    private void TrimExpiredVisualStates(DateTimeOffset now)
    {
        var expiredRegionIds = _regionVisualStates
            .Where(entry => !entry.Value.HasVisibleContent(now))
            .Select(entry => entry.Key)
            .ToArray();
        foreach (var regionId in expiredRegionIds)
        {
            _ = _regionVisualStates.Remove(regionId);
        }
    }

    private bool HasActiveVisualStates(DateTimeOffset now)
    {
        return _regionVisualStates.Values.Any(state => state.HasVisibleContent(now));
    }

    private static bool IsDrawableRegion(RecognitionRegion region)
    {
        return region.Enabled
            && region.Width > 0
            && region.Height > 0;
    }

    private static bool SameBounds(RectInt32 left, RectInt32 right)
    {
        return left.X == right.X
            && left.Y == right.Y
            && left.Width == right.Width
            && left.Height == right.Height;
    }

    private static double NormalizeScore(double score)
    {
        return double.IsNaN(score) || double.IsInfinity(score)
            ? 0
            : Math.Clamp(score, 0, 1);
    }

    private static string FormatPercent(double score)
    {
        return (NormalizeScore(score) * 100).ToString("0", CultureInfo.InvariantCulture) + "%";
    }

    private sealed class RegionVisualState
    {
        public string? OcrText
        {
            get;
            set;
        }

        public DateTimeOffset OcrTextExpiresAt
        {
            get;
            set;
        } = DateTimeOffset.MinValue;

        public double? ImageMatchScore
        {
            get;
            set;
        }

        public DateTimeOffset ImageMatchExpiresAt
        {
            get;
            set;
        } = DateTimeOffset.MinValue;

        public DateTimeOffset FlashUntil
        {
            get;
            set;
        } = DateTimeOffset.MinValue;

        public bool IsFlashing(DateTimeOffset now)
        {
            return now < FlashUntil;
        }

        public bool HasOcrText(DateTimeOffset now)
        {
            return !string.IsNullOrWhiteSpace(OcrText) && now < OcrTextExpiresAt;
        }

        public bool HasImageMatch(DateTimeOffset now)
        {
            return ImageMatchScore.HasValue && now < ImageMatchExpiresAt;
        }

        public bool HasVisibleContent(DateTimeOffset now)
        {
            return IsFlashing(now) || HasOcrText(now) || HasImageMatch(now);
        }
    }

}
