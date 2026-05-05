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

    private readonly CaptureTargetWindow _targetWindow;
    private readonly RecognitionRegionConfig _regionConfig;
    private readonly DispatcherQueueTimer _followTimer;
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
            AppWindow.MoveAndResize(bounds);
            Activate();
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
            AddRegion(region, widthScale, heightScale, canvasWidth, canvasHeight);
        }
    }

    private void AddRegion(
        RecognitionRegion region,
        double widthScale,
        double heightScale,
        double canvasWidth,
        double canvasHeight)
    {
        var x = region.X * widthScale;
        var y = region.Y * heightScale;
        var width = region.Width * widthScale;
        var height = region.Height * heightScale;

        if (width <= 0 || height <= 0)
        {
            return;
        }

        var color = Color.FromArgb(0xFF, 0x2F, 0xD7, 0xFF);
        var strokeBrush = new SolidColorBrush(color);
        var fillBrush = new SolidColorBrush(Color.FromArgb(0x18, color.R, color.G, color.B));
        var outline = new Rectangle();

        outline.Width = width;
        outline.Height = height;
        outline.Stroke = strokeBrush;
        outline.StrokeThickness = 2;
        outline.Fill = fillBrush;

        Canvas.SetLeft(outline, x);
        Canvas.SetTop(outline, y);
        RegionCanvas.Children.Add(outline);

        if (!string.IsNullOrWhiteSpace(region.Id))
        {
            AddLabel(region.Id, strokeBrush, x, y, width, canvasWidth, canvasHeight);
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

    private void RecognitionOverlayWindow_Closed(object sender, WindowEventArgs args)
    {
        _isClosed = true;
        _followTimer.Stop();
        _messageHook.Dispose();
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

}
