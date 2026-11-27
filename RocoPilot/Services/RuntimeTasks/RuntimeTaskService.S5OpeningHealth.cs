using Microsoft.Extensions.Logging;
using RocoPilot.Models.Recognition;
using RocoPilot.Services.Recognition;
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
    // 缩放后舍弃遮罩边缘的半透明像素，避免圆角外的场景颜色参与评分。
    internal static readonly ImageMatchOptions S5OpeningHealthOptions = new() { MinimumScore = 0.90, AlphaThreshold = 220 };
    private int _s5OpeningChecksRemaining;

    // S5 换季清理：一并删除 血脉与异色入场缓存、CacheS5OpeningTip、ReadS5OpeningTipsAsync 及其调用和重置点。
    // 仅采集最后一次大世界之后短窗口内的提示区；未确认大成功时不运行补读 OCR。
    private static readonly TimeSpan S5OpeningTipWindow = TimeSpan.FromSeconds(15);
    private readonly List<(DateTimeOffset At, CapturedFrame? Bloodline, CapturedFrame? Shiny)> _s5OpeningTips = [];
    private DateTimeOffset? _s5OpeningWorldAt;

    private void CacheS5OpeningTip(RuntimeTaskState state, CapturedFrame frame)
    {
        if (_s5OpeningWorldAt is not { } worldAt || _s5OpeningChecksRemaining == 0 || _battleStartedAt is not null) return;
        if (frame.CapturedAt - worldAt > S5OpeningTipWindow)
        {
            _s5OpeningTips.Clear();
            _s5OpeningWorldAt = null;
            return;
        }

        var season = _encounterSeasonConfigService.GetCurrentSeason();
        if (season is null || !IsS5OpeningSeason(season, frame.CapturedAt)) return;
        var shiny = CropS5OpeningTip(state, frame, BattleShinyTipRegionIds);
        var bloodline = CropS5OpeningTip(state, frame, EncounterBloodlineRecognition.RegionIds);
        if (shiny is not null || bloodline is not null)
            _s5OpeningTips.Add((frame.CapturedAt, bloodline, shiny));
    }

    private static CapturedFrame? CropS5OpeningTip(RuntimeTaskState state, CapturedFrame frame, IReadOnlyList<string> aliases)
    {
        var region = state.RecognitionRegionConfig.Regions.FirstOrDefault(r => RuntimeFrameRecognizer.IsRegionMatch(r, aliases));
        if (region is null) return null;
        var area = RecognitionRegionImageHelper.ToFrameRegion(region, frame, state.TargetWindow, state.RecognitionRegionConfig);
        if (area.Width <= 0 || area.Height <= 0) return null;
        return new CapturedFrame(area.Width, area.Height, RecognitionRegionImageHelper.CropFrame(frame, area));
    }

    private async Task ReadS5OpeningTipsAsync(CapturedFrame battleFrame, EncounterSeasonDefinition season, CancellationToken cancellationToken)
    {
        var battleId = _battle.BattleId;
        var accountUid = GetShinyAccountUid();
        try
        {
            // 优先完成异色检查；血脉可能提前确认，两种提示也可能出现在不同帧。
            for (var i = _s5OpeningTips.Count - 1; i >= 0; i--)
            {
                var (at, _, shiny) = _s5OpeningTips[i];
                if (battleFrame.CapturedAt - at > S5OpeningTipWindow) break;
                if (shiny is null) continue;
                var region = new RecognitionRegion { Id = BattleShinyTipRegionIds[0], Width = shiny.Width, Height = shiny.Height };
                var text = await _frameRecognizer.TryReadFrameRegionAsync(shiny, region, cancellationToken);
                if (text is null || await ApplyShinyTipAsync(text, battleFrame, season, battleId, at, accountUid)) break;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "S5 开场异色补读失败");
        }

        try
        {
            // 从最接近技能菜单的帧往前找，空白入场画面不能作为普通血脉的依据。
            for (var i = _s5OpeningTips.Count - 1; i >= 0; i--)
            {
                var (at, crop, _) = _s5OpeningTips[i];
                if (battleFrame.CapturedAt - at > S5OpeningTipWindow || _battle.Bloodline != EncounterBloodlineKind.Unrecognized) break;
                if (crop is null) continue;
                var region = new RecognitionRegion { Id = EncounterBloodlineRecognition.RegionIds[0], Width = crop.Width, Height = crop.Height };
                var text = await _frameRecognizer.TryReadFrameRegionAsync(crop, region, cancellationToken);
                if (text is null) break;
                if (EncounterBloodlineRecognition.TryParse(text, out var kind))
                {
                    if (_battle.ObserveBloodline(battleId, kind))
                        _logger.LogInformation("S5 大成功开场血脉：{Bloodline}", EncounterBloodlineRecognition.GetDisplayName(kind));
                    break;
                }
                if (!string.IsNullOrWhiteSpace(text)) _battle.ObserveUnparsedBloodlineTip(battleId, true);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "S5 开场血脉补读失败");
        }
        finally { _s5OpeningTips.Clear(); }
    }

    private static bool IsS5OpeningSeason(EncounterSeasonDefinition season, DateTimeOffset capturedAt)
    {
        var today = DateOnly.FromDateTime(capturedAt.LocalDateTime);
        return season.Id == "S5" && EncounterSeasonTimeline.TryGetDates(season.DateRange, out var start, out var end)
            && today >= start && today <= end;
    }

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

        if (screen != BattleScreen.SkillSelection || button != EncounterCaptureButtonState.Enabled
            || _encounterCaptureButtonStateTracker.HasSeenDisabled || !IsS5OpeningSeason(season, frame.CapturedAt)
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
