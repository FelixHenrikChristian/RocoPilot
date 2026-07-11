using Microsoft.Extensions.Logging;

using RocoPilot.Contracts.Services;
using RocoPilot.Contracts.Services.Capture;
using RocoPilot.Contracts.Services.Recognition;
using RocoPilot.Contracts.Services.Statistics;
using RocoPilot.Contracts.Services.TextRecognition;
using RocoPilot.Models.Capture;
using RocoPilot.Models.Recognition;
using RocoPilot.Models.Statistics;
using RocoPilot.Models.TextRecognition;
using RocoPilot.Services.Recognition;

namespace RocoPilot.Services.Statistics;

public sealed class StatisticsUidDetectionService : IStatisticsUidDetectionService
{
    private const string UidRegionId = "uid";
    private const int DetectionAttemptCount = 3;
    private const int RequiredMatchingResults = 2;
    private static readonly TimeSpan DetectionAttemptDelay = TimeSpan.FromMilliseconds(120);
    private static readonly IReadOnlyList<CaptureMethod> CaptureMethods =
    [
        CaptureMethod.WindowsGraphicsCapture,
        CaptureMethod.BitBlt,
        CaptureMethod.PrintWindow
    ];

    private readonly IGameWindowService _gameWindowService;
    private readonly IScreenCaptureService _screenCaptureService;
    private readonly IRecognitionRegionConfigService _recognitionRegionConfigService;
    private readonly ITextRecognitionService _textRecognitionService;
    private readonly ILogger<StatisticsUidDetectionService> _logger;

    public StatisticsUidDetectionService(
        IGameWindowService gameWindowService,
        IScreenCaptureService screenCaptureService,
        IRecognitionRegionConfigService recognitionRegionConfigService,
        ITextRecognitionService textRecognitionService,
        ILogger<StatisticsUidDetectionService> logger)
    {
        _gameWindowService = gameWindowService;
        _screenCaptureService = screenCaptureService;
        _recognitionRegionConfigService = recognitionRegionConfigService;
        _textRecognitionService = textRecognitionService;
        _logger = logger;
    }

    public async Task<StatisticsUidDetectionResult> DetectAsync(
        CaptureMethod preferredCaptureMethod,
        TextRecognitionMethod textRecognitionMethod,
        CancellationToken cancellationToken = default)
    {
        var targetWindow = _gameWindowService.FindGameWindow();
        if (targetWindow is null)
        {
            return StatisticsUidDetectionResult.Failed(
                $"未找到目标游戏窗口：{_gameWindowService.TargetProcessName}");
        }

        var recognitionMethod = _textRecognitionService
            .GetMethods()
            .FirstOrDefault(method => method.Method == textRecognitionMethod);
        if (recognitionMethod is null || !recognitionMethod.IsAvailable)
        {
            return StatisticsUidDetectionResult.Failed(
                recognitionMethod?.UnavailableReason ?? "当前没有可用的 OCR 识别方法。");
        }

        var recognizedUidCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        var capturedFrame = false;
        var attemptedOcr = false;
        foreach (var captureMethod in BuildCaptureMethods(preferredCaptureMethod))
        {
            try
            {
                for (var attempt = 0; attempt < DetectionAttemptCount; attempt++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    using var frame = await Task.Run(
                        () => _screenCaptureService.Capture(targetWindow, captureMethod),
                        cancellationToken);
                    if (frame is null)
                    {
                        break;
                    }

                    capturedFrame = true;
                    var configWidth = targetWindow.HasClientArea ? targetWindow.ClientWidth : frame.Width;
                    var configHeight = targetWindow.HasClientArea ? targetWindow.ClientHeight : frame.Height;
                    var config = _recognitionRegionConfigService.LoadForResolution(configWidth, configHeight);
                    if (!config.LoadedFromFile)
                    {
                        return StatisticsUidDetectionResult.Failed(
                            $"未能加载 {configWidth}x{configHeight} 的 UID 识别配置。");
                    }

                    var uidRegion = FindUidRegion(config);
                    if (uidRegion is null)
                    {
                        return StatisticsUidDetectionResult.Failed(
                            $"识别配置中缺少启用的 {UidRegionId} 区域：{config.SourcePath}");
                    }

                    var frameRegion = RecognitionRegionImageHelper.ToFrameRegion(
                        uidRegion,
                        frame,
                        targetWindow,
                        config);
                    if (frameRegion.Width <= 0 || frameRegion.Height <= 0)
                    {
                        return StatisticsUidDetectionResult.Failed("UID 识别区域不在当前截图范围内。");
                    }

                    var imageBytes = await RecognitionRegionImageHelper.EncodePngAsync(
                        frame,
                        frameRegion,
                        cancellationToken);
                    var result = await _textRecognitionService.RecognizeAsync(
                        imageBytes,
                        recognitionMethod.Method,
                        cancellationToken);
                    attemptedOcr = true;
                    if (StatisticsUidRules.TryNormalize(result.Text, out var uid))
                    {
                        var count = recognizedUidCounts.GetValueOrDefault(uid) + 1;
                        recognizedUidCounts[uid] = count;
                        _logger.LogDebug(
                            "首页启动 UID OCR 结果：UID={Uid}, MatchCount={MatchCount}, Method={OcrMethod}, Capture={CaptureMethod}",
                            uid,
                            count,
                            recognitionMethod.Method,
                            captureMethod);
                        if (count >= RequiredMatchingResults)
                        {
                            _logger.LogInformation("首页启动时已识别统计账号 UID：{Uid}", uid);
                            return StatisticsUidDetectionResult.Detected(uid);
                        }
                    }
                    else
                    {
                        _logger.LogDebug(
                            "首页启动 UID OCR 未得到纯数字：Text={Text}, Method={OcrMethod}, Capture={CaptureMethod}",
                            FormatLogText(result.Text),
                            recognitionMethod.Method,
                            captureMethod);
                    }

                    if (attempt + 1 < DetectionAttemptCount)
                    {
                        await Task.Delay(DetectionAttemptDelay, cancellationToken);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "使用 {CaptureMethod} 识别首页启动 UID 失败。", captureMethod);
            }
            finally
            {
                _screenCaptureService.Release(targetWindow, captureMethod);
            }
        }

        if (!capturedFrame)
        {
            return StatisticsUidDetectionResult.Failed("已找到游戏窗口，但未能截取用于 UID 识别的画面。");
        }

        return StatisticsUidDetectionResult.Failed(
            attemptedOcr
                ? "UID OCR 结果不稳定或不是纯数字，请在统计页手动确认账号。"
                : "未能执行 UID OCR，请在统计页手动确认账号。");
    }

    private static RecognitionRegion? FindUidRegion(RecognitionRegionConfig config)
    {
        return config.Regions.FirstOrDefault(region =>
            region.Enabled
            && string.Equals(region.Id?.Trim(), UidRegionId, StringComparison.OrdinalIgnoreCase));
    }

    private static IReadOnlyList<CaptureMethod> BuildCaptureMethods(CaptureMethod preferredCaptureMethod)
    {
        return CaptureMethods
            .Prepend(preferredCaptureMethod)
            .Distinct()
            .ToArray();
    }

    private static string FormatLogText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return "<empty>";
        }

        var normalized = string.Join(
            " ",
            text.Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return normalized.Length <= 80 ? normalized : $"{normalized[..80]}...";
    }
}
