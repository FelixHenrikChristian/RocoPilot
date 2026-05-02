using System.Diagnostics;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading;

using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media.Imaging;

using RocoPilot.Contracts.Services;
using RocoPilot.Contracts.Services.Capture;
using RocoPilot.Models.Capture;

using Windows.Graphics;

namespace RocoPilot.Views.Windows;

public sealed partial class CapturePreviewWindow : WindowEx
{
    private readonly IScreenCaptureService _captureService;
    private readonly IThemeSelectorService _themeSelectorService;
    private readonly CaptureTargetWindow _targetWindow;
    private readonly CaptureMethodOption _captureMethod;
    private readonly DispatcherQueue _dispatcherQueue;
    private readonly Stopwatch _fpsStopwatch = new();

    private CancellationTokenSource? _captureCancellationTokenSource;
    private WriteableBitmap? _previewBitmap;
    private int _frameUpdatePending;
    private int _framesSinceLastFpsUpdate;
    private volatile bool _isPaused;
    private bool _hasStarted;

    public CapturePreviewWindow(CaptureTargetWindow targetWindow, CaptureMethodOption captureMethod)
    {
        _targetWindow = targetWindow;
        _captureMethod = captureMethod;
        _captureService = App.GetService<IScreenCaptureService>();
        _themeSelectorService = App.GetService<IThemeSelectorService>();
        _dispatcherQueue = DispatcherQueue.GetForCurrentThread();

        InitializeComponent();

        ContentRoot.RequestedTheme = _themeSelectorService.Theme;

        Title = $"捕获预览 - {_targetWindow.Title}";
        AppWindow.Title = Title;
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets/WindowIcon.ico"));
        HideNativeTitleBar();
        AppWindow.Resize(new SizeInt32(920, 640));

        TargetText.Text = _targetWindow.DisplayName;
        MethodText.Text = $"方式: {_captureMethod.Name}";
        FrameSizeText.Text = "尺寸: -";
        FpsText.Text = "FPS: -";

        Closed += (_, _) => StopCapture();
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
        if (_hasStarted)
        {
            return;
        }

        _hasStarted = true;
        StartCapture();
    }

    private void StartCapture()
    {
        StopCapture();

        var cancellationTokenSource = new CancellationTokenSource();
        _captureCancellationTokenSource = cancellationTokenSource;
        _fpsStopwatch.Restart();
        _framesSinceLastFpsUpdate = 0;
        StatusText.Text = "捕获中";

        _ = Task.Run(async () =>
        {
            try
            {
                await CaptureLoopAsync(cancellationTokenSource.Token);
            }
            finally
            {
                cancellationTokenSource.Dispose();
            }
        });
    }

    private async Task CaptureLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            if (_isPaused)
            {
                await DelayAsync(120, cancellationToken);
                continue;
            }

            var frameStart = Stopwatch.GetTimestamp();

            try
            {
                var frame = _captureService.Capture(_targetWindow, _captureMethod.Method);
                if (frame is null)
                {
                    QueueStatus("未获取到画面");
                    await DelayAsync(120, cancellationToken);
                    continue;
                }

                QueueFrame(frame);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                QueueStatus($"捕获失败: {ex.Message}");
                await DelayAsync(600, cancellationToken);
            }

            var elapsedMilliseconds = Stopwatch.GetElapsedTime(frameStart).TotalMilliseconds;
            var delay = Math.Max(1, 33 - (int)elapsedMilliseconds);
            await DelayAsync(delay, cancellationToken);
        }
    }

    private static async Task DelayAsync(int milliseconds, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(milliseconds, cancellationToken);
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void QueueFrame(CapturedFrame frame)
    {
        if (Interlocked.Exchange(ref _frameUpdatePending, 1) == 1)
        {
            return;
        }

        if (!_dispatcherQueue.TryEnqueue(() =>
        {
            try
            {
                PresentFrame(frame);
            }
            finally
            {
                _ = Interlocked.Exchange(ref _frameUpdatePending, 0);
            }
        }))
        {
            _ = Interlocked.Exchange(ref _frameUpdatePending, 0);
        }
    }

    private void PresentFrame(CapturedFrame frame)
    {
        if (_previewBitmap is null
            || _previewBitmap.PixelWidth != frame.Width
            || _previewBitmap.PixelHeight != frame.Height)
        {
            _previewBitmap = new WriteableBitmap(frame.Width, frame.Height);
            PreviewImage.Source = _previewBitmap;
            FrameSizeText.Text = $"尺寸: {frame.Width} x {frame.Height}";
        }

        using (var stream = _previewBitmap.PixelBuffer.AsStream())
        {
            stream.Seek(0, SeekOrigin.Begin);
            stream.Write(frame.Pixels, 0, frame.Pixels.Length);
        }

        _previewBitmap.Invalidate();
        EmptyState.Visibility = Visibility.Collapsed;
        StatusText.Text = "捕获中";
        UpdateFps();
    }

    private void UpdateFps()
    {
        _framesSinceLastFpsUpdate++;
        if (_fpsStopwatch.ElapsedMilliseconds < 500)
        {
            return;
        }

        var fps = _framesSinceLastFpsUpdate * 1000d / _fpsStopwatch.ElapsedMilliseconds;
        FpsText.Text = $"FPS: {fps:F1}";
        _framesSinceLastFpsUpdate = 0;
        _fpsStopwatch.Restart();
    }

    private void QueueStatus(string message)
    {
        _ = _dispatcherQueue.TryEnqueue(() => StatusText.Text = message);
    }

    private void PauseButton_Click(object sender, RoutedEventArgs e)
    {
        _isPaused = !_isPaused;
        PauseButtonText.Text = _isPaused ? "继续" : "暂停";
        PauseButtonIcon.Glyph = _isPaused ? "\uE768" : "\uE769";
        StatusText.Text = _isPaused ? "已暂停" : "捕获中";
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void StopCapture()
    {
        var cancellationTokenSource = Interlocked.Exchange(ref _captureCancellationTokenSource, null);
        cancellationTokenSource?.Cancel();
    }
}
