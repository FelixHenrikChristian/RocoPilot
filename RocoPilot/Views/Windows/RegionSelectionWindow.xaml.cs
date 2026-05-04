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

namespace RocoPilot.Views.Windows;

public sealed partial class RegionSelectionWindow : WindowEx
{
    private readonly CapturedFrame _frame;
    private readonly TaskCompletionSource<RecognitionRegion?> _selectionCompletion = new();
    private readonly IThemeSelectorService _themeSelectorService;

    private Rect _imageBounds;
    private Point _selectionStart;
    private bool _isDragging;
    private bool _hasImageBounds;
    private bool _hasCompleted;

    public RegionSelectionWindow(CapturedFrame frame, string sourceName)
    {
        _frame = frame;
        _themeSelectorService = App.GetService<IThemeSelectorService>();

        InitializeComponent();

        ContentRoot.RequestedTheme = _themeSelectorService.Theme;

        Title = "框选检测区域";
        AppWindow.Title = Title;
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets/WindowIcon.ico"));
        HideNativeTitleBar();
        AppWindow.Resize(new SizeInt32(1000, 700));

        SourceText.Text = sourceName;
        FrameSizeText.Text = $"{_frame.Width} x {_frame.Height}";

        Closed += (_, _) => CompleteSelection(null);
    }

    public Task<RecognitionRegion?> SelectAsync()
    {
        Activate();
        return _selectionCompletion.Task;
    }

    private void HideNativeTitleBar()
    {
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.SetBorderAndTitleBar(true, false);
            return;
        }

        var overlappedPresenter = OverlappedPresenter.Create();
        overlappedPresenter.SetBorderAndTitleBar(true, false);
        AppWindow.SetPresenter(overlappedPresenter);
    }

    private void ContentRoot_Loaded(object sender, RoutedEventArgs e)
    {
        PresentFrame();
        UpdateImageBounds();
    }

    private void PresentFrame()
    {
        var bitmap = new WriteableBitmap(_frame.Width, _frame.Height);
        using (var stream = bitmap.PixelBuffer.AsStream())
        {
            stream.Seek(0, SeekOrigin.Begin);
            stream.Write(_frame.Pixels, 0, _frame.Pixels.Length);
        }

        bitmap.Invalidate();
        PreviewImage.Source = bitmap;
    }

    private void ImageHost_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        _isDragging = false;
        UpdateImageBounds();
        HideSelection();
    }

    private void ImageHost_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint(ImageHost).Position;
        if (!IsInsideImage(point))
        {
            StatusText.Text = "请从截图画面内部开始拖拽";
            return;
        }

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
        if (selectedRect.Width < 4 || selectedRect.Height < 4)
        {
            StatusText.Text = "区域太小，请重新拖拽";
            HideSelection();
            return;
        }

        var region = CreateRegionFromDisplayRect(selectedRect);
        CompleteSelection(region);
        Close();
    }

    private void ImageHost_PointerCanceled(object sender, PointerRoutedEventArgs e)
    {
        _isDragging = false;
        HideSelection();
    }

    private void ResetButton_Click(object sender, RoutedEventArgs e)
    {
        _isDragging = false;
        HideSelection();
        StatusText.Text = "在截图上拖拽一个矩形区域";
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        CompleteSelection(null);
        Close();
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
        var rect = GetRect(start, end);
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
