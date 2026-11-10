using RocoPilot.Configuration;
using RocoPilot.Models.Capture;
using RocoPilot.Models.ImageMatching;
using RocoPilot.Models.Recognition;
using RocoPilot.Models.Runtime;
using RocoPilot.Services.Recognition;

namespace RocoPilot.Services.RuntimeTasks;

public enum BattleScreen { SkillSelection, PetSwitching, Chat, Unknown, PetSwitchConfirmation }

public sealed class BattleScreenRecognizer(RuntimeFrameRecognizer recognizer)
{
    private const string BattleChatTemplateName = "battle-chat.png";
    private const string BattleSkillTemplateName = "battle-button-skill.png";
    private const string BattleChangeTemplateName = "battle-button-change.png";

    private static readonly string[] BattleChatRegionIds =
    [
        RecognitionRegionIds.BattleChatButton
    ];
    private static readonly string[] BattleSkillRegionIds =
    [
        RecognitionRegionIds.BattleSkillButton
    ];
    private static readonly string[] BattleChangeRegionIds =
    [
        RecognitionRegionIds.BattleChangeButton
    ];
    private static readonly string[] BattleSkillTurnArrowRegionIds =
    [
        RecognitionRegionIds.BattleSkillTurnArrowMine
    ];
    private const string BattleSkillTurnArrowTemplateName = "battle-skill-turn-arrow.png";
    private static readonly ImageMatchOptions BattleSkillTurnArrowMatchOptions = new()
    {
        // 实测箭头帧 0.89-0.99；0.85 留余量。
        MinimumScore = 0.85,
        AlphaThreshold = 16,
        SearchStep = 1
    };

    private static readonly string[] BattleSkillQueueFirstRegionIds =
    [
        RecognitionRegionIds.BattleSkillQueueAnchor
    ];
    private const int QueueSampleSize = 16;
    /// <summary>第 1 个技能图标（16x16 灰度降采样）的平均绝对差阈值：图标更换远高于动画微变。</summary>
    private const int QueueChangeThreshold = 20;

    /// <summary>
    /// 记录出招队列第 1 个技能图标（含右下角释放者标注）的灰度快照，作为队列基线。
    /// </summary>
    public byte[]? CaptureSkillQueueAnchor(RuntimeTaskState state, CapturedFrame frame, CancellationToken cancellationToken)
    {
        var region = RuntimeFrameRecognizer.FindRegion(state.RecognitionRegionConfig, BattleSkillQueueFirstRegionIds);
        var frameRegion = RecognitionRegionImageHelper.ToFrameRegion(
            region, frame, state.TargetWindow, state.RecognitionRegionConfig);
        return SampleRegion(frame, frameRegion, QueueSampleSize);
    }

    /// <summary>第 1 个技能图标是否相对基线发生显著变化（出招队列变动）。</summary>
    public bool HasSkillQueueChanged(
        RuntimeTaskState state, CapturedFrame frame, byte[]? baseline, CancellationToken cancellationToken)
    {
        if (baseline is not { Length: QueueSampleSize * QueueSampleSize }) return false;
        var current = CaptureSkillQueueAnchor(state, frame, cancellationToken);
        if (current is null) return false;
        var total = 0;
        for (var i = 0; i < baseline.Length; i++)
        {
            total += Math.Abs(current[i] - baseline[i]);
        }

        return total / (double)baseline.Length >= QueueChangeThreshold;
    }

    private static byte[]? SampleRegion(CapturedFrame frame, RecognitionRegion frameRegion, int size)
    {
        if (frameRegion.Width <= 0 || frameRegion.Height <= 0) return null;
        var samples = new byte[size * size];
        for (var y = 0; y < size; y++)
        {
            var sy = frameRegion.Y + Math.Min(frameRegion.Height - 1, y * frameRegion.Height / size);
            for (var x = 0; x < size; x++)
            {
                var sx = frameRegion.X + Math.Min(frameRegion.Width - 1, x * frameRegion.Width / size);
                var offset = (sy * frame.Width + sx) * 4;
                if (offset + 2 >= frame.Pixels.Length) return null;
                // BGRA -> 灰度
                samples[y * size + x] =
                    (byte)((frame.Pixels[offset] * 11 + frame.Pixels[offset + 1] * 59 + frame.Pixels[offset + 2] * 30) / 100);
            }
        }

        return samples;
    }

    /// <summary>第 1 技能图标黄框（轮到本玩家可出招）的黄色像素数下限，实测黄框约 500+、无黄框 &lt;60。</summary>
    private const int FirstIconHighlightMinimumPixels = 150;

    /// <summary>
    /// 出招队列第 1 个技能图标是否带黄色高亮边框（轮到本玩家可出招的标志）。
    /// 中间跳过回合用它判定"已轮到玩家"：黄框出现后玩家选择不出招，等待 AI 插入。
    /// </summary>
    public bool IsFirstSkillIconHighlighted(RuntimeTaskState state, CapturedFrame frame, CancellationToken cancellationToken)
    {
        var region = RuntimeFrameRecognizer.FindRegion(state.RecognitionRegionConfig, BattleSkillQueueFirstRegionIds);
        var frameRegion = RecognitionRegionImageHelper.ToFrameRegion(
            region, frame, state.TargetWindow, state.RecognitionRegionConfig);
        if (frameRegion.Width <= 0 || frameRegion.Height <= 0) return false;
        var yellow = 0;
        for (var y = 0; y < frameRegion.Height; y++)
        {
            var row = (frameRegion.Y + y) * frame.Width;
            for (var x = 0; x < frameRegion.Width; x++)
            {
                var offset = (row + frameRegion.X + x) * 4;
                if (offset + 2 >= frame.Pixels.Length) return false;
                var b = frame.Pixels[offset];
                var g = frame.Pixels[offset + 1];
                var r = frame.Pixels[offset + 2];
                // 亮黄橙高亮：红绿高、蓝低、红蓝差大
                if (r > 150 && g > 120 && b < 120 && r - b > 70) yellow++;
            }
        }

        return yellow >= FirstIconHighlightMinimumPixels;
    }

    /// <summary>
    /// 判断是否轮到本玩家出招：顶部技能图标（出招队列）下方的黄色上三角箭头
    /// 指向第 2 个图标。跳过回合等待的正是这个状态——进战斗时箭头先指第 1 个
    /// （本玩家可出招，跳过即不出招），AI 插队出招后箭头顺延到第 2 个，
    /// 此时本玩家在顺延位上再次轮到，执行后续技能。识别区域只覆盖第 2 个
    /// 图标下方的条带，箭头指第 1/3/4 个或不存在时均不匹配。
    /// </summary>
    public async Task<bool> IsMyTurnAsync(
        RuntimeTaskState state,
        CapturedFrame frame,
        int playerNumber,
        CancellationToken cancellationToken)
    {
        if (playerNumber is < 1 or > 1)
        {
            // 目前仅制作了 1P 的箭头判定区域；其他编号暂视为已轮到，避免永久等待。
            return true;
        }

        return await recognizer.MatchRuntimeTemplateAsync(
            state,
            frame,
            BattleSkillTurnArrowRegionIds,
            BattleSkillTurnArrowTemplateName,
            BattleSkillTurnArrowMatchOptions,
            cancellationToken);
    }
    private static readonly ImageMatchOptions BattleChatMatchOptions = new()
    {
        MinimumScore = 0.96,
        AlphaThreshold = 16,
        SearchStep = 1
    };
    private static readonly ImageMatchOptions BattleSkillMatchOptions = new()
    {
        MinimumScore = 0.88,
        AlphaThreshold = 16,
        SearchStep = 1
    };
    private static readonly ImageMatchOptions BattleChangeMatchOptions = new()
    {
        MinimumScore = 0.88,
        AlphaThreshold = 16,
        SearchStep = 1
    };

    public async Task<bool> IsBattlePetSwitchingAsync(
        RuntimeTaskState state,
        CapturedFrame frame,
        CancellationToken cancellationToken)
    {
        if (await recognizer.MatchRuntimeTemplateAsync(
            state,
            frame,
            BattleChangeRegionIds,
            BattleChangeTemplateName,
            BattleChangeMatchOptions,
            cancellationToken))
        {
            return true;
        }

        return await recognizer.MatchRuntimeTemplateAsync(
            state,
            frame,
            BattleSkillRegionIds,
            BattleChangeTemplateName,
            BattleChangeMatchOptions,
            cancellationToken);
    }

    public async Task<bool> IsBattleSkillSelectionVisibleAsync(
        RuntimeTaskState state,
        CapturedFrame frame,
        CancellationToken cancellationToken)
    {
        return await recognizer.MatchRuntimeTemplateAsync(
            state,
            frame,
            BattleSkillRegionIds,
            BattleSkillTemplateName,
            BattleSkillMatchOptions,
            cancellationToken);
    }

    public async Task<bool> IsBattleChatVisibleAsync(
        RuntimeTaskState state,
        CapturedFrame frame,
        CancellationToken cancellationToken)
    {
        return await recognizer.MatchRuntimeTemplateAsync(
            state,
            frame,
            BattleChatRegionIds,
            BattleChatTemplateName,
            BattleChatMatchOptions,
            cancellationToken);
    }

    public async Task<BattleScreen> RecognizeAsync(RuntimeTaskState state, CapturedFrame frame, CancellationToken token)
    {
        if (await IsBattleSkillSelectionVisibleAsync(state, frame, token)) return BattleScreen.SkillSelection;
        if (await IsBattlePetSwitchingAsync(state, frame, token)) return BattleScreen.PetSwitching;
        return await IsBattleChatVisibleAsync(state, frame, token) ? BattleScreen.Chat : BattleScreen.Unknown;
    }
}
