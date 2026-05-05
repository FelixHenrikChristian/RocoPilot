using System.Diagnostics;

using Microsoft.Extensions.Logging;

using RocoPilot.Contracts.Services;
using RocoPilot.Contracts.Services.Capture;
using RocoPilot.Contracts.Services.ImageMatching;
using RocoPilot.Contracts.Services.Recognition;
using RocoPilot.Models.Capture;
using RocoPilot.Models.ImageMatching;
using RocoPilot.Models.Overlay;
using RocoPilot.Models.Recognition;
using RocoPilot.Models.Runtime;

namespace RocoPilot.Services;

public sealed class RuntimeTaskService : IRuntimeTaskService
{
    private const int TargetFrameIntervalMs = 33;
    private const int MagicPointSlotCount = 6;
    private const string MagicPointTemplateName = "magic-point.png";
    private const string BattleChatTemplateName = "battle-chat.png";

    private static readonly TimeSpan GameStateScanInterval = TimeSpan.FromMilliseconds(250);
    private static readonly string[] MagicPointRegionIds =
    [
        "magic-point",
        "magic-points",
        "magic",
        "magic-value"
    ];
    private static readonly string[] BattleChatRegionIds =
    [
        "battle-button-chat"
    ];
    private static readonly string[] BattleMagicRegionIds =
    [
        "battle-magic"
    ];
    private static readonly ImageMatchOptions MagicPointMatchOptions = new()
    {
        MinimumScore = 0.88,
        AlphaThreshold = 16,
        SearchStep = 1
    };
    private static readonly ImageMatchOptions BattleChatMatchOptions = new()
    {
        MinimumScore = 0.88,
        AlphaThreshold = 16,
        SearchStep = 1
    };

    private readonly IGameWindowService _gameWindowService;
    private readonly IScreenCaptureService _screenCaptureService;
    private readonly IRecognitionRegionConfigService _recognitionRegionConfigService;
    private readonly IImageMatchingService _imageMatchingService;
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
        IImageMatchingService imageMatchingService,
        IRecognitionOverlayService recognitionOverlayService,
        IInfoOverlayService infoOverlayService,
        ILogger<RuntimeTaskService> logger)
    {
        _gameWindowService = gameWindowService;
        _screenCaptureService = screenCaptureService;
        _recognitionRegionConfigService = recognitionRegionConfigService;
        _imageMatchingService = imageMatchingService;
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
        var nextGameStateScanAt = DateTimeOffset.MinValue;

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var frameStart = Stopwatch.GetTimestamp();
                CapturedFrame? frame = null;

                try
                {
                    frame = _screenCaptureService.Capture(state.TargetWindow, state.Options.CaptureMethod);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogWarning(ex, "捕获画面失败");
                    await DelayAsync(500, cancellationToken);
                }

                if (frame is not null && state.Options.InfoOverlayEnabled)
                {
                    var now = DateTimeOffset.Now;
                    if (now >= nextGameStateScanAt)
                    {
                        nextGameStateScanAt = now + GameStateScanInterval;

                        try
                        {
                            await UpdateGameStateSnapshotAsync(state, frame, cancellationToken);
                        }
                        catch (Exception ex) when (ex is not OperationCanceledException)
                        {
                            _logger.LogWarning(ex, "状态图像匹配失败");
                        }
                    }
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

    private async Task UpdateGameStateSnapshotAsync(
        RuntimeTaskState state,
        CapturedFrame frame,
        CancellationToken cancellationToken)
    {
        if (await IsBattlePetSwitchingAsync(state, frame, cancellationToken))
        {
            _infoOverlayService.UpdateSnapshot(new InfoOverlaySnapshot(
                "战斗中 - 切换精灵",
                Array.Empty<InfoOverlayCounter>(),
                DateTimeOffset.Now));
            return;
        }

        if (await IsBattleChatVisibleAsync(state, frame, cancellationToken))
        {
            _infoOverlayService.UpdateSnapshot(new InfoOverlaySnapshot(
                "战斗中",
                Array.Empty<InfoOverlayCounter>(),
                DateTimeOffset.Now));
            return;
        }

        await UpdateMagicPointSnapshotAsync(state, frame, cancellationToken);
    }

    private async Task<bool> IsBattlePetSwitchingAsync(
        RuntimeTaskState state,
        CapturedFrame frame,
        CancellationToken cancellationToken)
    {
        var battleMagicRegion = FindRegion(state.RecognitionRegionConfig, BattleMagicRegionIds);
        if (battleMagicRegion is null || !TemplateExists(MagicPointTemplateName))
        {
            return false;
        }

        var frameRegion = ToFrameRegion(
            battleMagicRegion,
            frame,
            state.TargetWindow,
            state.RecognitionRegionConfig);
        if (frameRegion.Width <= 0 || frameRegion.Height <= 0)
        {
            return false;
        }

        var result = await _imageMatchingService.MatchAsync(
            frame,
            frameRegion,
            MagicPointTemplateName,
            MagicPointMatchOptions,
            cancellationToken);
        return result.IsMatch;
    }

    private async Task<bool> IsBattleChatVisibleAsync(
        RuntimeTaskState state,
        CapturedFrame frame,
        CancellationToken cancellationToken)
    {
        var battleChatRegion = FindRegion(state.RecognitionRegionConfig, BattleChatRegionIds);
        if (battleChatRegion is null || !TemplateExists(BattleChatTemplateName))
        {
            return false;
        }

        var frameRegion = ToFrameRegion(
            battleChatRegion,
            frame,
            state.TargetWindow,
            state.RecognitionRegionConfig);
        if (frameRegion.Width <= 0 || frameRegion.Height <= 0)
        {
            return false;
        }

        var result = await _imageMatchingService.MatchAsync(
            frame,
            frameRegion,
            BattleChatTemplateName,
            BattleChatMatchOptions,
            cancellationToken);
        return result.IsMatch;
    }

    private async Task UpdateMagicPointSnapshotAsync(
        RuntimeTaskState state,
        CapturedFrame frame,
        CancellationToken cancellationToken)
    {
        var magicPointRegion = FindRegion(state.RecognitionRegionConfig, MagicPointRegionIds);
        if (magicPointRegion is null)
        {
            _infoOverlayService.UpdateSnapshot(new InfoOverlaySnapshot(
                "等待 magic-point 区域",
                Array.Empty<InfoOverlayCounter>(),
                DateTimeOffset.Now));
            return;
        }

        if (!TemplateExists(MagicPointTemplateName))
        {
            _infoOverlayService.UpdateSnapshot(new InfoOverlaySnapshot(
                "未找到 magic-point.png",
                Array.Empty<InfoOverlayCounter>(),
                DateTimeOffset.Now));
            return;
        }

        var frameRegion = ToFrameRegion(
            magicPointRegion,
            frame,
            state.TargetWindow,
            state.RecognitionRegionConfig);
        if (frameRegion.Width <= 0 || frameRegion.Height <= 0)
        {
            _infoOverlayService.UpdateSnapshot(new InfoOverlaySnapshot(
                "魔力区域不在截图内",
                Array.Empty<InfoOverlayCounter>(),
                DateTimeOffset.Now,
                0,
                MagicPointSlotCount));
            return;
        }

        var magicPointCount = 0;
        foreach (var slotRegion in SplitMagicPointSlots(frameRegion))
        {
            var result = await _imageMatchingService.MatchAsync(
                frame,
                slotRegion,
                MagicPointTemplateName,
                MagicPointMatchOptions,
                cancellationToken);
            if (result.IsMatch)
            {
                magicPointCount++;
            }
        }

        var statusText = magicPointCount > 0
            ? "大世界"
            : "未检测到大世界";
        _infoOverlayService.UpdateSnapshot(new InfoOverlaySnapshot(
            statusText,
            Array.Empty<InfoOverlayCounter>(),
            DateTimeOffset.Now,
            magicPointCount,
            MagicPointSlotCount));
    }

    private bool TemplateExists(string templateName)
    {
        return File.Exists(Path.Combine(_imageMatchingService.TemplateDirectory, templateName));
    }

    private static RecognitionRegion? FindRegion(
        RecognitionRegionConfig config,
        IReadOnlyList<string> aliases)
    {
        return config.Regions.FirstOrDefault(region => IsRegionMatch(region, aliases));
    }

    private static bool IsRegionMatch(RecognitionRegion region, IReadOnlyList<string> aliases)
    {
        if (!region.Enabled || string.IsNullOrWhiteSpace(region.Id))
        {
            return false;
        }

        var id = region.Id.Trim();
        return aliases.Any(alias =>
            string.Equals(id, alias, StringComparison.OrdinalIgnoreCase)
            || id.StartsWith($"{alias}-", StringComparison.OrdinalIgnoreCase));
    }

    private static IEnumerable<RecognitionRegion> SplitMagicPointSlots(RecognitionRegion region)
    {
        for (var index = 0; index < MagicPointSlotCount; index++)
        {
            var left = (int)Math.Round(region.Width * (double)index / MagicPointSlotCount);
            var right = (int)Math.Round(region.Width * (double)(index + 1) / MagicPointSlotCount);
            yield return new RecognitionRegion
            {
                Id = $"{region.Id}-{index + 1}",
                X = region.X + left,
                Y = region.Y,
                Width = Math.Max(1, right - left),
                Height = region.Height,
                Enabled = region.Enabled
            };
        }
    }

    private static RecognitionRegion ToFrameRegion(
        RecognitionRegion region,
        CapturedFrame frame,
        CaptureTargetWindow targetWindow,
        RecognitionRegionConfig config)
    {
        var configWidth = config.ResolutionWidth > 0
            ? config.ResolutionWidth
            : targetWindow.HasClientArea ? targetWindow.ClientWidth : frame.Width;
        var configHeight = config.ResolutionHeight > 0
            ? config.ResolutionHeight
            : targetWindow.HasClientArea ? targetWindow.ClientHeight : frame.Height;

        if (configWidth <= 0 || configHeight <= 0)
        {
            return new RecognitionRegion();
        }

        _ = TryGetClientAreaInCapturedFrame(
            frame,
            targetWindow,
            out var clientX,
            out var clientY,
            out var sourceWidth,
            out var sourceHeight);

        var sourceRegion = new RecognitionRegion
        {
            Id = region.Id,
            X = ScaleValue(region.X, configWidth, sourceWidth),
            Y = ScaleValue(region.Y, configHeight, sourceHeight),
            Width = Math.Max(1, ScaleValue(region.Width, configWidth, sourceWidth)),
            Height = Math.Max(1, ScaleValue(region.Height, configHeight, sourceHeight)),
            Enabled = region.Enabled
        };

        var x = clientX + sourceRegion.X;
        var y = clientY + sourceRegion.Y;
        var right = clientX + sourceRegion.X + sourceRegion.Width;
        var bottom = clientY + sourceRegion.Y + sourceRegion.Height;
        var clampedX = ClampToRange(x, 0, frame.Width);
        var clampedY = ClampToRange(y, 0, frame.Height);
        var clampedRight = ClampToRange(right, 0, frame.Width);
        var clampedBottom = ClampToRange(bottom, 0, frame.Height);

        return clampedRight <= clampedX || clampedBottom <= clampedY
            ? new RecognitionRegion()
            : new RecognitionRegion
            {
                Id = region.Id,
                X = clampedX,
                Y = clampedY,
                Width = clampedRight - clampedX,
                Height = clampedBottom - clampedY,
                Enabled = region.Enabled
            };
    }

    private static bool TryGetClientAreaInCapturedFrame(
        CapturedFrame frame,
        CaptureTargetWindow targetWindow,
        out int clientX,
        out int clientY,
        out int clientWidth,
        out int clientHeight)
    {
        clientX = 0;
        clientY = 0;
        clientWidth = frame.Width;
        clientHeight = frame.Height;

        if (!targetWindow.HasClientArea
            || frame.Width <= 0
            || frame.Height <= 0
            || (frame.Width == targetWindow.ClientWidth && frame.Height == targetWindow.ClientHeight))
        {
            return false;
        }

        clientX = Math.Max(0, targetWindow.ClientOffsetX);
        clientY = Math.Max(0, targetWindow.ClientOffsetY);

        if (clientX >= frame.Width || clientY >= frame.Height)
        {
            return false;
        }

        clientWidth = Math.Min(targetWindow.ClientWidth, frame.Width - clientX);
        clientHeight = Math.Min(targetWindow.ClientHeight, frame.Height - clientY);

        return clientWidth > 0
            && clientHeight > 0
            && (clientX != 0
                || clientY != 0
                || clientWidth != frame.Width
                || clientHeight != frame.Height);
    }

    private static int ScaleValue(int value, int sourceSize, int targetSize)
    {
        if (sourceSize <= 0 || targetSize <= 0)
        {
            return value;
        }

        return (int)Math.Round(value * (double)targetSize / sourceSize);
    }

    private static int ClampToRange(int value, int min, int max)
    {
        if (value < min)
        {
            return min;
        }

        return value > max ? max : value;
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
