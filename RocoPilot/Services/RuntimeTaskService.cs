using System.Diagnostics;

using Microsoft.Extensions.Logging;

using RocoPilot.Contracts.Services;
using RocoPilot.Contracts.Services.Capture;
using RocoPilot.Contracts.Services.Recognition;
using RocoPilot.Models.Capture;
using RocoPilot.Models.Runtime;

namespace RocoPilot.Services;

public sealed class RuntimeTaskService : IRuntimeTaskService
{
    private const int TargetFrameIntervalMs = 33;

    private readonly IGameWindowService _gameWindowService;
    private readonly IScreenCaptureService _screenCaptureService;
    private readonly IRecognitionRegionConfigService _recognitionRegionConfigService;
    private readonly IRecognitionOverlayService _recognitionOverlayService;
    private readonly IInfoOverlayService _infoOverlayService;
    private readonly ILogger<RuntimeTaskService> _logger;
    private readonly SemaphoreSlim _lifecycleLock = new(1, 1);

    private CancellationTokenSource? _captureCancellationTokenSource;
    private Task? _captureTask;

    public RuntimeTaskState? CurrentState
    {
        get;
        private set;
    }

    public bool IsRunning => CurrentState is not null;

    public RuntimeTaskService(
        IGameWindowService gameWindowService,
        IScreenCaptureService screenCaptureService,
        IRecognitionRegionConfigService recognitionRegionConfigService,
        IRecognitionOverlayService recognitionOverlayService,
        IInfoOverlayService infoOverlayService,
        ILogger<RuntimeTaskService> logger)
    {
        _gameWindowService = gameWindowService;
        _screenCaptureService = screenCaptureService;
        _recognitionRegionConfigService = recognitionRegionConfigService;
        _recognitionOverlayService = recognitionOverlayService;
        _infoOverlayService = infoOverlayService;
        _logger = logger;
    }

    public async Task<RuntimeTaskStartResult> StartAsync(
        RuntimeTaskStartOptions options,
        CancellationToken cancellationToken = default)
    {
        await _lifecycleLock.WaitAsync(cancellationToken);
        try
        {
            if (CurrentState is not null)
            {
                return RuntimeTaskStartResult.Started(CurrentState);
            }

            var targetWindow = _gameWindowService.FindGameWindow();
            if (targetWindow is null)
            {
                var missingWindowMessage = $"未找到目标游戏窗口：{_gameWindowService.TargetProcessName}";
                _logger.LogWarning("{Message}", missingWindowMessage);
                return RuntimeTaskStartResult.Failed(missingWindowMessage);
            }

            var firstFrame = await CaptureFrameAsync(targetWindow, options.CaptureMethod, cancellationToken);
            if (firstFrame is null)
            {
                _screenCaptureService.Release(targetWindow, options.CaptureMethod);
                var captureFailedMessage = $"找到窗口，但未能获取画面：{targetWindow.DisplayName}";
                _logger.LogWarning("{Message}", captureFailedMessage);
                return RuntimeTaskStartResult.Failed(captureFailedMessage);
            }

            var configResolutionWidth = targetWindow.HasClientArea
                ? targetWindow.ClientWidth
                : firstFrame.Width;
            var configResolutionHeight = targetWindow.HasClientArea
                ? targetWindow.ClientHeight
                : firstFrame.Height;
            var recognitionRegionConfig = _recognitionRegionConfigService.LoadForResolution(
                configResolutionWidth,
                configResolutionHeight);
            var state = new RuntimeTaskState(
                targetWindow,
                recognitionRegionConfig,
                options,
                DateTimeOffset.Now);
            var cancellationTokenSource = new CancellationTokenSource();
            _captureCancellationTokenSource = cancellationTokenSource;
            CurrentState = state;
            _recognitionOverlayService.Show(state);
            _infoOverlayService.Show(state);
            _captureTask = Task.Run(
                () => CaptureLoopAsync(state, cancellationTokenSource.Token),
                cancellationTokenSource.Token);

            _logger.LogInformation(
                "运行任务已启动，窗口: {Window}, 客户区: {ClientWidth}x{ClientHeight}, 首帧: {FrameWidth}x{FrameHeight}, 截图方式: {CaptureMethod}, 区域配置: {ConfigPath}",
                targetWindow.DisplayName,
                configResolutionWidth,
                configResolutionHeight,
                firstFrame.Width,
                firstFrame.Height,
                options.CaptureMethod,
                recognitionRegionConfig.SourcePath);

            _logger.LogInformation(
                "识别区域配置状态：Loaded={Loaded}, EnabledRegions={EnabledRegionCount}, Resolution={ResolutionWidth}x{ResolutionHeight}",
                recognitionRegionConfig.LoadedFromFile,
                recognitionRegionConfig.Regions.Count(region => region.Enabled),
                configResolutionWidth,
                configResolutionHeight);

            var message = "实时任务已启动。";

            return RuntimeTaskStartResult.Started(state, message);
        }
        catch (OperationCanceledException)
        {
            return RuntimeTaskStartResult.Failed("启动任务已取消。");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "启动运行任务失败");
            return RuntimeTaskStartResult.Failed($"启动失败：{ex.Message}");
        }
        finally
        {
            _lifecycleLock.Release();
        }
    }

    public async Task StopAsync()
    {
        CancellationTokenSource? cancellationTokenSource = null;
        Task? captureTask = null;
        RuntimeTaskState? state = null;

        await _lifecycleLock.WaitAsync();
        try
        {
            if (CurrentState is null)
            {
                return;
            }

            cancellationTokenSource = _captureCancellationTokenSource;
            captureTask = _captureTask;
            state = CurrentState;
            _captureCancellationTokenSource = null;
            _captureTask = null;
            CurrentState = null;
            cancellationTokenSource?.Cancel();
            _recognitionOverlayService.Hide();
            _infoOverlayService.Hide();
        }
        finally
        {
            _lifecycleLock.Release();
        }

        try
        {
            if (captureTask is not null)
            {
                await captureTask;
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            if (state is not null)
            {
                _screenCaptureService.Release(state.TargetWindow, state.Options.CaptureMethod);
            }

            cancellationTokenSource?.Dispose();
            _logger.LogInformation("运行任务已停止");
        }
    }

    private async Task CaptureLoopAsync(RuntimeTaskState state, CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var frameStart = Stopwatch.GetTimestamp();

                try
                {
                    _ = _screenCaptureService.Capture(state.TargetWindow, state.Options.CaptureMethod);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogWarning(ex, "捕获画面失败");
                    await DelayAsync(500, cancellationToken);
                }

                var elapsedMilliseconds = Stopwatch.GetElapsedTime(frameStart).TotalMilliseconds;
                var delay = Math.Max(1, TargetFrameIntervalMs - (int)elapsedMilliseconds);
                await DelayAsync(delay, cancellationToken);
            }
        }
        finally
        {
            _screenCaptureService.Release(state.TargetWindow, state.Options.CaptureMethod);
        }
    }

    private Task<CapturedFrame?> CaptureFrameAsync(
        CaptureTargetWindow targetWindow,
        CaptureMethod captureMethod,
        CancellationToken cancellationToken)
    {
        return Task.Run(() => _screenCaptureService.Capture(targetWindow, captureMethod), cancellationToken);
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
}
