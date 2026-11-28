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
    // 换季清理：删除本文件，并搜索 S5Opening、s5-opening 和 openingEvidence，
    // 移除截图缓存、状态重置、补读期间按键限制、血条模板、识别区和专用测试。
    // 将 ApplyS5OpeningCaptureButtonStateAsync 的调用改回 ApplyEncounterCaptureButtonState，
    // 移除捕捉按钮跟踪器的开场证据分支，保留普通奇遇的解除确认。
    // 仅从大世界进入战斗时开启；启动、恢复采集或中途接入一场残血战斗均不启用。
    private const string S5OpeningHealthSource = "S5 开场敌方 10% 血条及可用捕捉按钮";
    private static readonly string[] S5OpeningHealthRegion = ["s5-opening-ten-percent"];
    // 缩放后舍弃遮罩边缘的半透明像素，避免圆角外的场景颜色参与评分。
    internal static readonly ImageMatchOptions S5OpeningHealthOptions = new() { MinimumScore = 0.90, AlphaThreshold = 220 };
    private int _s5OpeningChecksRemaining;

    // 保留解除附近的每张截图，更早的入场画面只保留场景扫描帧。
    private static readonly TimeSpan S5OpeningTipWindow = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan S5OpeningDenseTipWindow = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan S5OpeningPostReliefTipWindow = TimeSpan.FromMilliseconds(300);
    private readonly List<S5OpeningTipFrame> _s5OpeningTips = [];
    private DateTimeOffset? _s5OpeningWorldAt;
    private DateTimeOffset? _s5OpeningTipConfirmedAt;
    private Task? _s5OpeningTipRead;

    private sealed record S5OpeningTipFrame(DateTimeOffset At, CapturedFrame? Bloodline, CapturedFrame? Shiny, bool SceneScan);

    private bool IsS5OpeningTipReadPending => _s5OpeningTipConfirmedAt is not null || _s5OpeningTipRead is { IsCompleted: false };

    private void ResetS5OpeningState()
    {
        _s5OpeningTips.Clear();
        _s5OpeningTipConfirmedAt = null;
        _s5OpeningTipRead = null;
        _s5OpeningWorldAt = _scene == GameScene.World ? DateTimeOffset.Now : null;
        _s5OpeningChecksRemaining = _scene == GameScene.World
            ? EncounterCaptureButtonStateTracker.RequiredEnabledConfirmationCount : 0;
    }

    private void CacheS5OpeningTip(RuntimeTaskState state, CapturedFrame frame, bool sceneScan, CancellationToken cancellationToken)
    {
        if (_scene == GameScene.World) return;
        var confirmedAt = _s5OpeningTipConfirmedAt;
        if (confirmedAt is null)
        {
            if (_s5OpeningWorldAt is not { } worldAt || _s5OpeningChecksRemaining == 0) return;
            if (frame.CapturedAt - worldAt > S5OpeningTipWindow)
            {
                _s5OpeningTips.Clear();
                _s5OpeningWorldAt = null;
                return;
            }
        }

        var season = _encounterSeasonConfigService.GetCurrentSeason();
        if (season is null || !IsS5OpeningSeason(season, confirmedAt ?? frame.CapturedAt))
        {
            _s5OpeningTipConfirmedAt = null;
            return;
        }

        _s5OpeningTips.RemoveAll(tip => !tip.SceneScan && frame.CapturedAt - tip.At > S5OpeningDenseTipWindow);
        var shiny = CropS5OpeningTip(state, frame, BattleShinyTipRegionIds);
        var bloodline = CropS5OpeningTip(state, frame, EncounterBloodlineRecognition.RegionIds);
        if (shiny is not null || bloodline is not null)
            _s5OpeningTips.Add(new(frame.CapturedAt, bloodline, shiny, sceneScan));

        if (confirmedAt is not { } reliefAt || frame.CapturedAt - reliefAt < S5OpeningPostReliefTipWindow) return;

        _s5OpeningTipConfirmedAt = null;
        var tips = _s5OpeningTips.ToArray();
        _s5OpeningTips.Clear();
        var battleId = _battle.BattleId;
        var accountUid = GetShinyAccountUid();
        var notificationFrame = frame.AddReference();
        _s5OpeningTipRead = Task.Run(async () =>
        {
            using (notificationFrame)
                await ReadS5OpeningTipsAsync(tips, notificationFrame, season, reliefAt, battleId, accountUid, cancellationToken);
        });
        _session?.Track(_s5OpeningTipRead);
    }

    private static CapturedFrame? CropS5OpeningTip(RuntimeTaskState state, CapturedFrame frame, IReadOnlyList<string> aliases)
    {
        var region = state.RecognitionRegionConfig.Regions.FirstOrDefault(r => RuntimeFrameRecognizer.IsRegionMatch(r, aliases));
        if (region is null) return null;
        var area = RecognitionRegionImageHelper.ToFrameRegion(region, frame, state.TargetWindow, state.RecognitionRegionConfig);
        if (area.Width <= 0 || area.Height <= 0) return null;
        return new CapturedFrame(area.Width, area.Height, RecognitionRegionImageHelper.CropFrame(frame, area));
    }

    private async Task ReadS5OpeningTipsAsync(
        S5OpeningTipFrame[] tips, CapturedFrame battleFrame, EncounterSeasonDefinition season,
        DateTimeOffset reliefAt, long battleId, string? accountUid, CancellationToken cancellationToken)
    {
        try
        {
            // 优先完成异色检查；血脉可能提前确认，两种提示也可能出现在不同帧。
            for (var i = tips.Length - 1; i >= 0; i--)
            {
                var (at, _, shiny, _) = tips[i];
                if (_isSuspended || battleId != _battle.BattleId) return;
                if (reliefAt - at > S5OpeningTipWindow) break;
                if (shiny is null) continue;
                var region = new RecognitionRegion { Id = BattleShinyTipRegionIds[0], Width = shiny.Width, Height = shiny.Height };
                var text = await _frameRecognizer.TryReadFrameRegionAsync(shiny, region, cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                if (_isSuspended || battleId != _battle.BattleId) return;
                if (text is null || await ApplyShinyTipAsync(text, battleFrame, season, battleId, at, accountUid)) break;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "S5 开场异色补读失败");
        }

        try
        {
            for (var i = tips.Length - 1; i >= 0; i--)
            {
                var (at, crop, _, _) = tips[i];
                if (_isSuspended || battleId != _battle.BattleId || reliefAt - at > S5OpeningTipWindow
                    || _battle.Bloodline != EncounterBloodlineKind.Unrecognized) break;
                if (crop is null) continue;
                var region = new RecognitionRegion { Id = EncounterBloodlineRecognition.RegionIds[0], Width = crop.Width, Height = crop.Height };
                var text = await _frameRecognizer.TryReadFrameRegionAsync(crop, region, cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                if (text is null || _isSuspended || battleId != _battle.BattleId) break;
                if (EncounterBloodlineRecognition.IsUnrelatedCombatTip(text)) text = string.Empty;
                if (EncounterBloodlineRecognition.TryParse(text, out var kind))
                {
                    if (_battle.ObserveBloodline(battleId, kind))
                        _logger.LogInformation("S5 大成功开场血脉：{Bloodline}", EncounterBloodlineRecognition.GetDisplayName(kind));
                    break;
                }
                // 解除前的空白入场画面不能作为普通血脉的依据。
                var hasText = !string.IsNullOrWhiteSpace(text);
                if (hasText || at >= reliefAt) _battle.ObserveUnparsedBloodlineTip(battleId, hasText);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "S5 开场血脉补读失败");
        }
    }

    private static bool IsS5OpeningSeason(EncounterSeasonDefinition season, DateTimeOffset capturedAt)
    {
        var today = DateOnly.FromDateTime(capturedAt.LocalDateTime);
        return season.Id == "S5" && EncounterSeasonTimeline.TryGetDates(season.DateRange, out var start, out var end)
            && today >= start && today <= end;
    }

    private async Task ApplyS5OpeningCaptureButtonStateAsync(
        RuntimeTaskState state, CapturedFrame frame, EncounterSeasonDefinition season,
        BattleScreen screen, EncounterCaptureButtonObservation observation, CancellationToken cancellationToken)
    {
        var openingEvidence = await MatchS5OpeningHealthAsync(state, frame, season, screen, observation.State, cancellationToken);
        ApplyEncounterCaptureButtonState(season, observation, openingEvidence ? S5OpeningHealthSource : null);
        if (openingEvidence && _encounterCaptureButtonStateTracker.IsRelieved)
            _s5OpeningTipConfirmedAt = DateTimeOffset.Now;
        if (_s5OpeningChecksRemaining == 0 && _s5OpeningTipConfirmedAt is null) _s5OpeningTips.Clear();
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
