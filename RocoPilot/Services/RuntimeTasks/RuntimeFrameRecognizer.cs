using Microsoft.Extensions.Logging;
using RocoPilot.Contracts.Services;
using RocoPilot.Contracts.Services.ImageMatching;
using RocoPilot.Contracts.Services.TextRecognition;
using RocoPilot.Models.Capture;
using RocoPilot.Models.ImageMatching;
using RocoPilot.Models.Recognition;
using RocoPilot.Models.Runtime;
using RocoPilot.Services.Recognition;
using static RocoPilot.Services.RuntimeTasks.RuntimeDebugLogger;

namespace RocoPilot.Services.RuntimeTasks;

/// <summary>将会话中的配置区域映射到截图，执行模板匹配与 OCR；不修改战斗状态。</summary>
public sealed class RuntimeFrameRecognizer(
    IImageMatchingService imageMatching,
    ITextRecognitionService textRecognition,
    IRecognitionOverlayService overlay,
    RuntimeDebugLogger debugLog)
{
    public async Task<string> RecognizeRegionTextAsync(
        RuntimeTaskState state,
        CapturedFrame frame,
        IReadOnlyList<string> regionAliases,
        CancellationToken cancellationToken,
        string taskName)
    {
        var region = FindRegion(state.RecognitionRegionConfig, regionAliases);
        var frameRegion = RecognitionRegionImageHelper.ToFrameRegion(
            region,
            frame,
            state.TargetWindow,
            state.RecognitionRegionConfig);
        if (frameRegion.Width <= 0 || frameRegion.Height <= 0)
        {
            debugLog.Write(
                CreateDebugLogKey("ocr-skip-outside-frame", taskName, region.Id),
                $"{frameRegion.X},{frameRegion.Y},{frameRegion.Width}x{frameRegion.Height}",
                "{TaskName} OCR跳过：识别区域不在截图内。Region={RegionId}, Aliases={RegionAliases}",
                taskName,
                region.Id,
                string.Join("|", regionAliases));
            return string.Empty;
        }

        var recognitionMethod = textRecognition
            .GetMethods()
            .FirstOrDefault(method => method.Method == state.Options.TextRecognitionMethod && method.IsAvailable);
        if (recognitionMethod is null)
        {
            debugLog.Write(
                CreateDebugLogKey("ocr-skip-method-unavailable", taskName, region.Id, state.Options.TextRecognitionMethod),
                "method-unavailable",
                "{TaskName} OCR跳过：OCR 方法不可用。Method={TextRecognitionMethod}, Region={RegionId}",
                taskName,
                state.Options.TextRecognitionMethod,
                region.Id);
            return string.Empty;
        }

        var result = await textRecognition.RecognizeAsync(
            frame,
            frameRegion,
            recognitionMethod.Method,
            cancellationToken);
        overlay.ShowOcrResult(region.Id, result.Text);
        debugLog.Write(
            CreateDebugLogKey("ocr-result", taskName, region.Id, recognitionMethod.Method),
            CreateTextDebugFingerprint(result.Text),
            "{TaskName} OCR结果：Region={RegionId}, Method={TextRecognitionMethod}, FrameRegion={X},{Y},{Width}x{Height}, Text={Text}",
            taskName,
            region.Id,
            recognitionMethod.Method,
            frameRegion.X,
            frameRegion.Y,
            frameRegion.Width,
            frameRegion.Height,
            FormatLogText(result.Text));
        return result.Text;
    }

    public async Task<bool> MatchRuntimeTemplateAsync(
        RuntimeTaskState state,
        CapturedFrame frame,
        IReadOnlyList<string> regionAliases,
        string templateName,
        ImageMatchOptions options,
        string taskName,
        string targetName,
        CancellationToken cancellationToken)
    {
        var result = await MatchRuntimeTemplateResultAsync(
            state,
            frame,
            regionAliases,
            templateName,
            options,
            taskName,
            targetName,
            cancellationToken);
        return result.IsMatch;
    }

    public async Task<ImageMatchResult> MatchRuntimeTemplateResultAsync(
        RuntimeTaskState state,
        CapturedFrame frame,
        IReadOnlyList<string> regionAliases,
        string templateName,
        ImageMatchOptions options,
        string taskName,
        string targetName,
        CancellationToken cancellationToken)
    {
        var region = FindRegion(state.RecognitionRegionConfig, regionAliases);
        var templatePath = GetResolutionTemplatePath(state.RecognitionRegionConfig, templateName);
        if (!TemplateExists(templatePath))
        {
            debugLog.Write(
                CreateDebugLogKey("template-skip-missing-template", taskName, targetName, region.Id, templatePath),
                "missing-template",
                "{TaskName} 目标识别跳过：未找到模板。Target={Target}, Region={RegionId}, Template={Template}",
                taskName,
                targetName,
                region.Id,
                templatePath);
            return ImageMatchResult.NoMatch(0, templatePath);
        }

        var frameRegion = RecognitionRegionImageHelper.ToFrameRegion(
            region,
            frame,
            state.TargetWindow,
            state.RecognitionRegionConfig);
        if (frameRegion.Width <= 0 || frameRegion.Height <= 0)
        {
            debugLog.Write(
                CreateDebugLogKey("template-skip-outside-frame", taskName, targetName, region.Id, templatePath),
                $"{frameRegion.X},{frameRegion.Y},{frameRegion.Width}x{frameRegion.Height}",
                "{TaskName} 目标识别跳过：识别区域不在截图内。Target={Target}, Region={RegionId}, Template={Template}",
                taskName,
                targetName,
                region.Id,
                templatePath);
            return ImageMatchResult.NoMatch(0, templatePath);
        }

        var matchOptions = CreateScaledImageMatchOptions(
            options,
            frame,
            state.TargetWindow,
            state.RecognitionRegionConfig);
        var result = await imageMatching.MatchAsync(
            frame,
            frameRegion,
            templatePath,
            matchOptions,
            cancellationToken);
        overlay.ShowImageMatchResult(region.Id, result.Score);
        debugLog.Write(
            CreateDebugLogKey("template-result", taskName, targetName, region.Id, templatePath),
            CreateBooleanDebugFingerprint(result.IsMatch),
            "{TaskName} 目标识别结果：Target={Target}, Region={RegionId}, Template={Template}, Score={Score:F3}, Threshold={Threshold:F3}, IsMatch={IsMatch}, FrameRegion={X},{Y},{Width}x{Height}",
            taskName,
            targetName,
            region.Id,
            templatePath,
            result.Score,
            matchOptions.MinimumScore,
            result.IsMatch,
            frameRegion.X,
            frameRegion.Y,
            frameRegion.Width,
            frameRegion.Height);
        return result;
    }


    public bool TemplateExists(string templateName)
    {
        return File.Exists(Path.Combine(imageMatching.TemplateDirectory, templateName));
    }

    public static string GetResolutionTemplatePath(
        RecognitionRegionConfig config,
        string templateName)
    {
        if (config.ResolutionWidth <= 0 || config.ResolutionHeight <= 0)
        {
            return templateName;
        }

        return Path.Combine(
            $"{config.ResolutionWidth}x{config.ResolutionHeight}",
            templateName);
    }

    public static RecognitionRegion FindRegion(
        RecognitionRegionConfig config,
        IReadOnlyList<string> aliases)
    {
        return config.Regions.FirstOrDefault(region => IsRegionMatch(region, aliases))
            ?? throw new InvalidOperationException(
                $"识别区域配置缺少启用区域：{string.Join(", ", aliases)}。配置文件：{config.SourcePath}");
    }

    public static bool IsRegionMatch(RecognitionRegion region, IReadOnlyList<string> aliases)
    {
        if (!region.Enabled || string.IsNullOrWhiteSpace(region.Id))
        {
            return false;
        }

        var id = region.Id.Trim();
        return aliases.Any(alias => string.Equals(id, alias, StringComparison.OrdinalIgnoreCase));
    }

    public static ImageMatchOptions CreateScaledImageMatchOptions(
        ImageMatchOptions options,
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
            return CloneImageMatchOptions(options, 1, 1);
        }

        _ = RecognitionRegionImageHelper.TryGetClientAreaInCapturedFrame(
            frame,
            targetWindow,
            out _,
            out _,
            out var sourceWidth,
            out var sourceHeight);

        var scaleX = sourceWidth > 0 ? sourceWidth / (double)configWidth : 1;
        var scaleY = sourceHeight > 0 ? sourceHeight / (double)configHeight : 1;
        return CloneImageMatchOptions(options, scaleX, scaleY);
    }

    public static ImageMatchOptions CloneImageMatchOptions(ImageMatchOptions options, double scaleX, double scaleY)
    {
        return new ImageMatchOptions
        {
            Algorithm = options.Algorithm,
            MinimumScore = options.MinimumScore,
            AlphaThreshold = options.AlphaThreshold,
            SearchStep = options.SearchStep,
            TemplateScaleX = options.TemplateScaleX * scaleX,
            TemplateScaleY = options.TemplateScaleY * scaleY
        };
    }

}
