using System.Runtime.InteropServices.WindowsRuntime;

using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Imaging;

using RocoPilot.Contracts.Services;
using RocoPilot.Models.Capture;
using RocoPilot.Models.Recognition;

using Windows.Foundation;
using Windows.Graphics;
using Windows.System;

namespace RocoPilot.Views.Windows;

public sealed partial class RegionSelectionWindow : WindowEx
{
    private const int PreferredWindowWidth = 1680;
    private const int PreferredWindowHeight = 920;
    private const int MinimumWindowWidth = 900;
    private const int MinimumWindowHeight = 560;
    private const int DisplayHorizontalMargin = 120;
    private const int DisplayVerticalMargin = 100;
    private const float MinimumZoomFactor = 0.25f;
    private const float MaximumZoomFactor = 4f;
    private const float ZoomStep = 0.25f;
    private const double MinimumSelectionScreenSize = 4d;
    private const double SelectionStrokeScreenThickness = 2d;
    private const int OemPlusVirtualKey = 0xBB;
    private const int OemMinusVirtualKey = 0xBD;

    private readonly CapturedFrame _frame;
    private readonly TaskCompletionSource<RecognitionRegion?> _selectionCompletion = new();
    private readonly IThemeSelectorService _themeSelectorService;

    private Rect _imageBounds;
    private Point _selectionStart;
    private RecognitionRegion? _selectedRegion;
    private bool _isDragging;
    private bool _hasImageBounds;
    private bool _hasCompleted;

    public RegionSelectionWindow(
        CapturedFrame frame,
        string sourceName,
        RecognitionRegion? initialRegion = null)
    {
        _frame = frame;
        _themeSelectorService = App.GetService<IThemeSelectorService>();

        if (initialRegion is not null)
        {
            _selectedRegion = new RecognitionRegion
            {
                Id = initialRegion.Id,
                X = initialRegion.X,
                Y = initialRegion.Y,
                Width = initialRegion.Width,
                Height = initialRegion.Height,
                Enabled = initialRegion.Enabled
            };
        }

        InitializeComponent();

        ContentRoot.RequestedTheme = _themeSelectorService.Theme;

        Title = initialRegion is null ? "框选检测区域" : "修改检测区域";
        SelectionTitleText.Text = Title;
        AppWindow.Title = Title;
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets/WindowIcon.ico"));
        AppWindow.TitleBar.PreferredTheme = TitleBarTheme.UseDefaultAppMode;
        ResizeToDisplayWorkArea();

        SourceText.Text = sourceName;
        FrameSizeText.Text = $"{_frame.Width} x {_frame.Height}";
        RegisterZoomKeyboardAccelerators();

        Closed += (_, _) => CompleteSelection(null);
    }

    public Task<RecognitionRegion?> SelectAsync()
    {
        Activate();
        return _selectionCompletion.Task;
    }

    private void ContentRoot_Loaded(object sender, RoutedEventArgs e)
    {
        PresentFrame();
        UpdateImageHostSize();
        UpdateImageBounds();
        UpdateZoomControls();

        if (_selectedRegion is not null)
        {
            ShowSelectedRegion();
            ConfirmButton.IsEnabled = true;
            StatusText.Text = $"当前区域：{FormatRegion(_selectedRegion)}，拖拽可重新框选";
        }
    }

    private void PresentFrame()
    {
        var bitmap = new WriteableBitmap(_frame.Width, _frame.Height);
        using (var stream = bitmap.PixelBuffer.AsStream())
        {
            stream.Seek(0, SeekOrigin.Begin);
            stream.Write(_frame.Pixels, 0, _frame.PixelByteLength);
        }

        bitmap.Invalidate();
        PreviewImage.Source = bitmap;
    }

    private void ImageHost_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        _isDragging = false;
        UpdateImageBounds();
        ShowSelectedRegion();
    }

    private void ImageScrollViewer_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        UpdateImageHostSize();
    }

    private void ImageScrollViewer_ViewChanged(object sender, ScrollViewerViewChangedEventArgs e)
    {
        UpdateZoomControls();
    }

    private void ZoomOutButton_Click(object sender, RoutedEventArgs e)
    {
        ChangeZoom(-ZoomStep);
    }

    private void ResetZoomButton_Click(object sender, RoutedEventArgs e)
    {
        ResetZoom();
    }

    private void ZoomInButton_Click(object sender, RoutedEventArgs e)
    {
        ChangeZoom(ZoomStep);
    }

    private void ImageHost_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint(ImageHost).Position;
        if (!IsInsideImage(point))
        {
            StatusText.Text = "请从截图画面内部开始拖拽";
            return;
        }

        ClearSelection();
        _selectionStart = ClampToImage(point);
        _isDragging = true;
        ImageHost.CapturePointer(e.Pointer);
        SetSelectionRect(_selectionStart, _selectionStart);
        e.Handled = true;
    }

    private void ImageHost_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_isDragging)
        {
            return;
        }

        var point = ClampToImage(e.GetCurrentPoint(ImageHost).Position);
        SetSelectionRect(_selectionStart, point);
        e.Handled = true;
    }

    private void ImageHost_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!_isDragging)
        {
            return;
        }

        _isDragging = false;
        ImageHost.ReleasePointerCapture(e.Pointer);

        var end = ClampToImage(e.GetCurrentPoint(ImageHost).Position);
        var selectedRect = GetRect(_selectionStart, end);
        var minimumSelectionSize = MinimumSelectionScreenSize / ImageScrollViewer.ZoomFactor;
        if (selectedRect.Width < minimumSelectionSize || selectedRect.Height < minimumSelectionSize)
        {
            StatusText.Text = "区域太小，请重新拖拽";
            HideSelection();
            return;
        }

        _selectedRegion = CreateRegionFromDisplayRect(selectedRect);
        ConfirmButton.IsEnabled = true;
        StatusText.Text = $"已选择区域：{FormatRegion(_selectedRegion)}，点击确认应用";
    }

    private void ImageHost_PointerCanceled(object sender, PointerRoutedEventArgs e)
    {
        _isDragging = false;
        ClearSelection();
    }

    private void ResetButton_Click(object sender, RoutedEventArgs e)
    {
        _isDragging = false;
        ClearSelection();
        StatusText.Text = "在截图上拖拽一个矩形区域；Ctrl++ / Ctrl+- 缩放，Ctrl+0 复位";
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        CompleteSelection(null);
        Close();
    }

    private void ConfirmButton_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedRegion is null)
        {
            ConfirmButton.IsEnabled = false;
            StatusText.Text = "请先在截图上框选一个矩形区域";
            return;
        }

        CompleteSelection(_selectedRegion);
        Close();
    }

    private void ResizeToDisplayWorkArea()
    {
        var workArea = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Nearest).WorkArea;
        var maxWidth = Math.Max(1, workArea.Width - DisplayHorizontalMargin);
        var maxHeight = Math.Max(1, workArea.Height - DisplayVerticalMargin);

        var width = Math.Min(PreferredWindowWidth, maxWidth);
        var height = Math.Min(PreferredWindowHeight, maxHeight);

        width = width < MinimumWindowWidth
            ? Math.Min(MinimumWindowWidth, workArea.Width)
            : width;
        height = height < MinimumWindowHeight
            ? Math.Min(MinimumWindowHeight, workArea.Height)
            : height;

        var x = workArea.X + Math.Max(0, (workArea.Width - width) / 2);
        var y = workArea.Y + Math.Max(0, (workArea.Height - height) / 2);
        AppWindow.MoveAndResize(new RectInt32(x, y, width, height));
    }

    private void UpdateImageHostSize()
    {
        var width = ImageScrollViewer.ActualWidth;
        var height = ImageScrollViewer.ActualHeight;
        if (width <= 0 || height <= 0)
        {
            return;
        }

        ImageHost.Width = width;
        ImageHost.Height = height;
    }

    private void ChangeZoom(float delta)
    {
        var currentZoom = ImageScrollViewer.ZoomFactor;
        var targetZoom = Math.Clamp(
            (float)Math.Round((currentZoom + delta) / ZoomStep) * ZoomStep,
            MinimumZoomFactor,
            MaximumZoomFactor);

        if (Math.Abs(targetZoom - currentZoom) < 0.001f)
        {
            return;
        }

        _ = ImageScrollViewer.ChangeView(null, null, targetZoom, disableAnimation: false);
    }

    private void ResetZoom()
    {
        _ = ImageScrollViewer.ChangeView(0, 0, 1f, disableAnimation: false);
    }

    private void UpdateZoomControls()
    {
        var zoomFactor = ImageScrollViewer.ZoomFactor;
        ZoomPercentText.Text = $"{Math.Round(zoomFactor * 100):0}%";
        ZoomOutButton.IsEnabled = zoomFactor > MinimumZoomFactor + 0.001f;
        ZoomInButton.IsEnabled = zoomFactor < MaximumZoomFactor - 0.001f;
        SelectionRectangle.StrokeThickness = SelectionStrokeScreenThickness / zoomFactor;
    }

    private void RegisterZoomKeyboardAccelerators()
    {
        AddZoomKeyboardAccelerator(VirtualKey.Add, VirtualKeyModifiers.Control, () => ChangeZoom(ZoomStep));
        AddZoomKeyboardAccelerator(
            (VirtualKey)OemPlusVirtualKey,
            VirtualKeyModifiers.Control,
            () => ChangeZoom(ZoomStep));
        AddZoomKeyboardAccelerator(
            (VirtualKey)OemPlusVirtualKey,
            VirtualKeyModifiers.Control | VirtualKeyModifiers.Shift,
            () => ChangeZoom(ZoomStep));
        AddZoomKeyboardAccelerator(
            VirtualKey.Subtract,
            VirtualKeyModifiers.Control,
            () => ChangeZoom(-ZoomStep));
        AddZoomKeyboardAccelerator(
            (VirtualKey)OemMinusVirtualKey,
            VirtualKeyModifiers.Control,
            () => ChangeZoom(-ZoomStep));
        AddZoomKeyboardAccelerator(VirtualKey.Number0, VirtualKeyModifiers.Control, ResetZoom);
        AddZoomKeyboardAccelerator(VirtualKey.NumberPad0, VirtualKeyModifiers.Control, ResetZoom);
    }

    private void AddZoomKeyboardAccelerator(
        VirtualKey key,
        VirtualKeyModifiers modifiers,
        Action action)
    {
        var accelerator = new KeyboardAccelerator
        {
            Key = key,
            Modifiers = modifiers
        };
        accelerator.Invoked += (_, args) =>
        {
            action();
            args.Handled = true;
        };
        ContentRoot.KeyboardAccelerators.Add(accelerator);
    }

    private void UpdateImageBounds()
    {
        var hostWidth = ImageHost.ActualWidth;
        var hostHeight = ImageHost.ActualHeight;
        OverlayCanvas.Width = hostWidth;
        OverlayCanvas.Height = hostHeight;

        if (hostWidth <= 0 || hostHeight <= 0 || _frame.Width <= 0 || _frame.Height <= 0)
        {
            _imageBounds = default;
            _hasImageBounds = false;
            return;
        }

        var scale = Math.Min(hostWidth / _frame.Width, hostHeight / _frame.Height);
        var displayWidth = _frame.Width * scale;
        var displayHeight = _frame.Height * scale;
        var displayX = (hostWidth - displayWidth) / 2;
        var displayY = (hostHeight - displayHeight) / 2;
        _imageBounds = new Rect(displayX, displayY, displayWidth, displayHeight);
        _hasImageBounds = displayWidth > 0 && displayHeight > 0;
    }

    private void SetSelectionRect(Point start, Point end)
    {
        SetSelectionRect(GetRect(start, end));
    }

    private void SetSelectionRect(Rect rect)
    {
        SelectionRectangle.Visibility = Visibility.Visible;
        SelectionRectangle.Width = rect.Width;
        SelectionRectangle.Height = rect.Height;
        Canvas.SetLeft(SelectionRectangle, rect.X);
        Canvas.SetTop(SelectionRectangle, rect.Y);
    }

    private void HideSelection()
    {
        SelectionRectangle.Visibility = Visibility.Collapsed;
        SelectionRectangle.Width = 0;
        SelectionRectangle.Height = 0;
    }

    private void ClearSelection()
    {
        _selectedRegion = null;
        ConfirmButton.IsEnabled = false;
        HideSelection();
    }

    private void ShowSelectedRegion()
    {
        if (_selectedRegion is null || !_hasImageBounds || _frame.Width <= 0 || _frame.Height <= 0)
        {
            HideSelection();
            return;
        }

        var x = _imageBounds.X + (_selectedRegion.X * _imageBounds.Width / _frame.Width);
        var y = _imageBounds.Y + (_selectedRegion.Y * _imageBounds.Height / _frame.Height);
        var width = _selectedRegion.Width * _imageBounds.Width / _frame.Width;
        var height = _selectedRegion.Height * _imageBounds.Height / _frame.Height;
        SetSelectionRect(new Rect(x, y, width, height));
    }

    private RecognitionRegion CreateRegionFromDisplayRect(Rect displayRect)
    {
        var left = (displayRect.X - _imageBounds.X) / _imageBounds.Width;
        var top = (displayRect.Y - _imageBounds.Y) / _imageBounds.Height;
        var right = (displayRect.X + displayRect.Width - _imageBounds.X) / _imageBounds.Width;
        var bottom = (displayRect.Y + displayRect.Height - _imageBounds.Y) / _imageBounds.Height;

        var x = ClampToRange((int)Math.Round(left * _frame.Width), 0, Math.Max(0, _frame.Width - 1));
        var y = ClampToRange((int)Math.Round(top * _frame.Height), 0, Math.Max(0, _frame.Height - 1));
        var regionRight = ClampToRange((int)Math.Round(right * _frame.Width), x + 1, _frame.Width);
        var regionBottom = ClampToRange((int)Math.Round(bottom * _frame.Height), y + 1, _frame.Height);

        return new RecognitionRegion
        {
            X = x,
            Y = y,
            Width = regionRight - x,
            Height = regionBottom - y,
            Enabled = true
        };
    }

    private static string FormatRegion(RecognitionRegion region)
    {
        return $"X={region.X}, Y={region.Y}, 宽={region.Width}, 高={region.Height}";
    }

    private Point ClampToImage(Point point)
    {
        return new Point(
            Clamp(point.X, _imageBounds.X, _imageBounds.X + _imageBounds.Width),
            Clamp(point.Y, _imageBounds.Y, _imageBounds.Y + _imageBounds.Height));
    }

    private bool IsInsideImage(Point point)
    {
        return _hasImageBounds
            && point.X >= _imageBounds.X
            && point.Y >= _imageBounds.Y
            && point.X <= _imageBounds.X + _imageBounds.Width
            && point.Y <= _imageBounds.Y + _imageBounds.Height;
    }

    private void CompleteSelection(RecognitionRegion? region)
    {
        if (_hasCompleted)
        {
            return;
        }

        _hasCompleted = true;
        _selectionCompletion.TrySetResult(region);
    }

    private static Rect GetRect(Point start, Point end)
    {
        var x = Math.Min(start.X, end.X);
        var y = Math.Min(start.Y, end.Y);
        return new Rect(x, y, Math.Abs(end.X - start.X), Math.Abs(end.Y - start.Y));
    }

    private static double Clamp(double value, double min, double max)
    {
        if (value < min)
        {
            return min;
        }

        return value > max ? max : value;
    }

    private static int ClampToRange(int value, int min, int max)
    {
        if (value < min)
        {
            return min;
        }

        return value > max ? max : value;
    }
}
