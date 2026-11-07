using RocoPilot.Contracts.Services;
using RocoPilot.Contracts.Services.ImageMatching;
using RocoPilot.Contracts.Services.TextRecognition;
using RocoPilot.Models.Capture;
using RocoPilot.Models.ImageMatching;
using RocoPilot.Models.Recognition;
using RocoPilot.Models.Runtime;
using RocoPilot.Services.Recognition;

namespace RocoPilot.Services.RuntimeTasks;

/// <summary>将会话中的配置区域映射到截图，执行模板匹配与 OCR；不修改战斗状态。</summary>
public sealed class RuntimeFrameRecognizer(
    IImageMatchingService imageMatching,
    ITextRecognitionService textRecognition,
    IRecognitionOverlayService overlay)
{
    public async Task<string> RecognizeRegionTextAsync(
        RuntimeTaskState state,
        CapturedFrame frame,
        IReadOnlyList<string> regionAliases,
        CancellationToken cancellationToken)
    {
        var region = FindRegion(state.RecognitionRegionConfig, regionAliases);
        var frameRegion = RecognitionRegionImageHelper.ToFrameRegion(
            region,
            frame,
            state.TargetWindow,
            state.RecognitionRegionConfig);
        if (frameRegion.Width <= 0 || frameRegion.Height <= 0)
        {
            return string.Empty;
        }

        var recognitionMethod = textRecognition.GetDefaultMethod();
        if (recognitionMethod is not { IsAvailable: true })
        {
            return string.Empty;
        }

        var result = await textRecognition.RecognizeAsync(
            frame,
            frameRegion,
            recognitionMethod.Method,
            cancellationToken);
        overlay.ShowOcrResult(region.Id, result.Text);
        return result.Text;
    }

    public async Task<bool> MatchRuntimeTemplateAsync(
        RuntimeTaskState state,
        CapturedFrame frame,
        IReadOnlyList<string> regionAliases,
        string templateName,
        ImageMatchOptions options,
        CancellationToken cancellationToken)
    {
        var result = await MatchRuntimeTemplateResultAsync(
            state,
            frame,
            regionAliases,
            templateName,
            options,
            cancellationToken);
        return result.IsMatch;
    }

    public async Task<ImageMatchResult> MatchRuntimeTemplateResultAsync(
        RuntimeTaskState state,
        CapturedFrame frame,
        IReadOnlyList<string> regionAliases,
        string templateName,
        ImageMatchOptions options,
        CancellationToken cancellationToken)
    {
        var region = FindRegion(state.RecognitionRegionConfig, regionAliases);
        var templatePath = GetResolutionTemplatePath(state.RecognitionRegionConfig, templateName);
        if (!TemplateExists(templatePath))
        {
            return ImageMatchResult.NoMatch(0, templatePath);
        }

        var frameRegion = RecognitionRegionImageHelper.ToFrameRegion(
            region,
            frame,
            state.TargetWindow,
            state.RecognitionRegionConfig);
        if (frameRegion.Width <= 0 || frameRegion.Height <= 0)
        {
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
        return CloneImageMatchOptions(options, Math.Min(scaleX, scaleY), Math.Min(scaleX, scaleY));
    }

    private static ImageMatchOptions CloneImageMatchOptions(ImageMatchOptions options, double scaleX, double scaleY)
    {
        return new ImageMatchOptions
        {
            MinimumScore = options.MinimumScore,
            ScaleTolerance = options.ScaleTolerance,
            AlphaThreshold = options.AlphaThreshold,
            SearchStep = options.SearchStep,
            TemplateScaleX = options.TemplateScaleX * scaleX,
            TemplateScaleY = options.TemplateScaleY * scaleY
        };
    }

}
