using RocoPilot.Models.Capture;
using RocoPilot.Models.Encounters;
using RocoPilot.Models.ImageMatching;
using RocoPilot.Models.Runtime;
using RocoPilot.Services.Encounters;
using RocoPilot.Services.RuntimeTasks;

namespace RocoPilot.Services;

public sealed partial class RuntimeTaskService
{
    // S5 临时机制。退役时搜索 S5Opening 和 s5-opening，删除接入点、模板、区域和测试。同步移除 tracker.Observe 的 openingEvidence 参数及对应的暂缓出招条件。
    // 仅从大世界进入战斗时开启；启动、恢复采集或中途接入一场残血战斗均不启用。
    private const string S5OpeningHealthSource = "S5 开场敌方 10% 血条及可用捕捉按钮";
    private static readonly string[] S5OpeningHealthRegion = ["s5-opening-ten-percent"];
    internal static readonly ImageMatchOptions S5OpeningHealthOptions = new() { MinimumScore = 0.96 };
    private int _s5OpeningChecksRemaining;

    private async Task<bool> MatchS5OpeningHealthAsync(
        RuntimeTaskState state, CapturedFrame frame, EncounterSeasonDefinition season,
        BattleScreen screen, EncounterCaptureButtonState button, CancellationToken cancellationToken)
    {
        if (_s5OpeningChecksRemaining == 0) return false;
        // 聊天按钮可能先于技能菜单出现。只在首个可操作菜单检查，离开该菜单即放弃复查。
        if (screen == BattleScreen.Chat
            && _s5OpeningChecksRemaining == EncounterCaptureButtonStateTracker.RequiredEnabledConfirmationCount)
        {
            if (button == EncounterCaptureButtonState.Disabled) _s5OpeningChecksRemaining = 0;
            return false;
        }

        var today = DateOnly.FromDateTime(frame.CapturedAt.LocalDateTime);
        if (screen != BattleScreen.SkillSelection || button != EncounterCaptureButtonState.Enabled
            || _encounterCaptureButtonStateTracker.HasSeenDisabled || season.Id != "S5"
            || !EncounterSeasonTimeline.TryGetDates(season.DateRange, out var start, out var end)
            || today < start || today > end
            || !state.RecognitionRegionConfig.Regions.Any(region =>
                RuntimeFrameRecognizer.IsRegionMatch(region, S5OpeningHealthRegion)))
        {
            _s5OpeningChecksRemaining = 0;
            return false;
        }

        _s5OpeningChecksRemaining--;
        var matched = await _frameRecognizer.MatchRuntimeTemplateAsync(
            state, frame, S5OpeningHealthRegion, "s5-opening-ten-percent.png",
            S5OpeningHealthOptions, cancellationToken);
        if (!matched) _s5OpeningChecksRemaining = 0;
        return matched;
    }
}
