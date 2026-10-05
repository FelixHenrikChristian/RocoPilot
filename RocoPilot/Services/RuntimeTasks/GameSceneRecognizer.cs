using RocoPilot.Configuration;
using RocoPilot.Contracts.Services;
using RocoPilot.Contracts.Services.ImageMatching;
using RocoPilot.Models.Capture;
using RocoPilot.Models.ImageMatching;
using RocoPilot.Models.Runtime;
using RocoPilot.Services.Recognition;
using static RocoPilot.Services.RuntimeTasks.RuntimeDebugLogger;
using static RocoPilot.Services.RuntimeTasks.RuntimeFrameRecognizer;

namespace RocoPilot.Services.RuntimeTasks;

public enum GameScene { Unknown, World, Battle }

public sealed record GameSceneMatch(
    GameScene Scene,
    int MagicPointCount = 0,
    BattleScreen? BattleScreen = null);

/// <summary>每帧按图像证据识别场景，不依赖自动战斗开关或上一帧场景。</summary>
public sealed class GameSceneRecognizer(
    RuntimeFrameRecognizer recognizer,
    IImageMatchingService imageMatching,
    IRecognitionOverlayService overlay,
    RuntimeDebugLogger debugLog,
    BattleScreenRecognizer battleScreen)
{
    public const int MagicPointSlotCount = 6;
    private const string MagicPointTemplateName = "magic-point.png";
    private static readonly string[] MagicPointRegionIds = [RecognitionRegionIds.MagicPoint];
    private static readonly ImageMatchOptions MagicPointMatchOptions = new()
    {
        MinimumScore = 0.96,
        AlphaThreshold = 16,
        SearchStep = 1
    };

    public async Task<GameSceneMatch> RecognizeAsync(
        RuntimeTaskState state,
        CapturedFrame frame,
        CancellationToken cancellationToken)
    {
        var magicPointCount = await CountMagicPointsAsync(state, frame, cancellationToken);
        if (magicPointCount > 0)
        {
            return new(GameScene.World, magicPointCount);
        }

        var screen = await battleScreen.RecognizeAsync(state, frame, cancellationToken);
        return screen == BattleScreen.Unknown
            ? new(GameScene.Unknown)
            : new(GameScene.Battle, BattleScreen: screen);
    }

    private async Task<int> CountMagicPointsAsync(
        RuntimeTaskState state,
        CapturedFrame frame,
        CancellationToken cancellationToken)
    {
        var region = FindRegion(state.RecognitionRegionConfig, MagicPointRegionIds);
        var templatePath = GetResolutionTemplatePath(state.RecognitionRegionConfig, MagicPointTemplateName);
        if (!recognizer.TemplateExists(templatePath))
        {
            return 0;
        }

        var frameRegion = RecognitionRegionImageHelper.ToFrameRegion(
            region, frame, state.TargetWindow, state.RecognitionRegionConfig);
        if (frameRegion.Width <= 0 || frameRegion.Height <= 0)
        {
            return 0;
        }

        var options = CreateScaledImageMatchOptions(
            MagicPointMatchOptions, frame, state.TargetWindow, state.RecognitionRegionConfig);
        var result = await imageMatching.FindMatchesAsync(
            frame, frameRegion, templatePath, MagicPointSlotCount, options,
            cancellationToken: cancellationToken);
        var count = result.Matches.Count;
        overlay.ShowImageMatchResult(region.Id, result.BestScore);
        debugLog.Write(
            CreateDebugLogKey("game-state-magic-point", region.Id),
            $"{count}/{MagicPointSlotCount}",
            "状态识别目标结果：Target=大世界魔力点, Region={RegionId}, Count={Count}/{Maximum}, FrameRegion={X},{Y},{Width}x{Height}",
            region.Id, count, MagicPointSlotCount,
            frameRegion.X, frameRegion.Y, frameRegion.Width, frameRegion.Height);
        return count;
    }
}
