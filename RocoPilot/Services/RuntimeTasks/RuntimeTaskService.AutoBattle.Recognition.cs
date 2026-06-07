using Microsoft.Extensions.Logging;

using RocoPilot.Helpers;
using RocoPilot.Models.Capture;
using RocoPilot.Models.Runtime;

namespace RocoPilot.Services;

public sealed partial class RuntimeTaskService
{
    private async Task<bool> IsBattlePetSwitchingAsync(
        RuntimeTaskState state,
        CapturedFrame frame,
        CancellationToken cancellationToken)
    {
        return await MatchRuntimeTemplateAsync(
            state,
            frame,
            BattleChangeRegionIds,
            BattleChangeTemplateName,
            BattleChangeMatchOptions,
            "状态识别",
            "切换精灵界面",
            cancellationToken);
    }

    private async Task<bool> IsBattleSkillSelectionVisibleAsync(
        RuntimeTaskState state,
        CapturedFrame frame,
        CancellationToken cancellationToken)
    {
        return await MatchRuntimeTemplateAsync(
            state,
            frame,
            BattleSkillRegionIds,
            BattleSkillTemplateName,
            BattleSkillMatchOptions,
            "状态识别",
            "技能选择按钮",
            cancellationToken);
    }

    private async Task<bool> IsBattleChatVisibleAsync(
        RuntimeTaskState state,
        CapturedFrame frame,
        CancellationToken cancellationToken)
    {
        return await MatchRuntimeTemplateAsync(
            state,
            frame,
            BattleChatRegionIds,
            BattleChatTemplateName,
            BattleChatMatchOptions,
            "状态识别",
            "战斗聊天按钮",
            cancellationToken);
    }

    private async Task<bool> TryUpdateAutoBattleEncounterRelievedActionModeAsync(
        RuntimeTaskState state,
        CapturedFrame frame,
        CancellationToken cancellationToken)
    {
        var settings = NormalizeAutoBattleSettings(_autoBattleSettings);
        var encounterRelievedAction = settings.EncounterRelievedAction;
        if (!settings.IsEnabled || !RequiresAutoBattleEncounterRelieveDetection(encounterRelievedAction))
        {
            return false;
        }

        if (_isAutoBattleSuspendedForShiny)
        {
            return false;
        }

        if (_isAutoBattleEncounterRelieved)
        {
            return true;
        }

        var now = DateTimeOffset.Now;
        if (now < _nextAutoBattleEncounterRelieveScanAt)
        {
            return false;
        }

        _nextAutoBattleEncounterRelieveScanAt = now + AutoBattleEncounterRelieveScanInterval;

        var season = _encounterSeasonConfigService.GetCurrentSeason();
        if (season is null)
        {
            return false;
        }

        if (UsesEnemyNameTransitionDetection(season))
        {
            var enemyName = await TryDetectEncounterNameTransitionAsync(
                state,
                frame,
                season,
                cancellationToken,
                "自动战斗");
            if (!string.IsNullOrWhiteSpace(enemyName))
            {
                LogDebugOncePerValue(
                    CreateDebugLogKey("auto-battle-encounter-relieved-transition-filter", season.Id),
                    string.Join(
                        "|",
                        season.DetectionMode,
                        CreateTextDebugFingerprint(enemyName),
                        "true"),
                    "自动战斗奇遇解除操作筛选：DetectionMode={DetectionMode}, Spirit={SpiritName}, IsMatch=True",
                    season.DetectionMode,
                    enemyName);
                return ApplyAutoBattleEncounterRelievedDetection("自动战斗");
            }
        }

        if (string.IsNullOrWhiteSpace(season.TipText))
        {
            return false;
        }

        var tipText = await RecognizeRegionTextAsync(
            state,
            frame,
            BattleTipRegionIds,
            cancellationToken,
            "自动战斗");
        var isTipMatch = TextMatchingHelper.IsSimilar(
            tipText,
            season.TipText,
            season.MatchThreshold,
            out var similarity);
        LogDebugOncePerValue(
            CreateDebugLogKey("auto-battle-encounter-relieved-tip-filter", season.Id),
            CreateMatchFilterDebugFingerprint(tipText, similarity, isTipMatch),
            "自动战斗奇遇解除操作筛选：TipText={TipText}, Expected={ExpectedTipText}, Similarity={Similarity:P1}, Threshold={Threshold:P1}, IsMatch={IsMatch}",
            FormatLogText(tipText),
            FormatLogText(season.TipText),
            similarity,
            season.MatchThreshold,
            isTipMatch);

        if (!isTipMatch)
        {
            return false;
        }

        return ApplyAutoBattleEncounterRelievedDetection("自动战斗");
    }

    private async Task<bool> TrySuspendAutoBattleForShinyAsync(
        RuntimeTaskState state,
        CapturedFrame frame,
        CancellationToken cancellationToken)
    {
        var settings = NormalizeAutoBattleSettings(_autoBattleSettings);
        if (!settings.IsEnabled)
        {
            return false;
        }

        if (_isAutoBattleSuspendedForShiny)
        {
            return true;
        }

        var now = DateTimeOffset.Now;
        if (now < _nextAutoBattleShinySuspendScanAt)
        {
            return false;
        }

        _nextAutoBattleShinySuspendScanAt = now + AutoBattleShinySuspendScanInterval;

        var tipText = await RecognizeRegionTextAsync(
            state,
            frame,
            BattleTipHeterochromiaRegionIds,
            cancellationToken,
            "自动战斗异色保护");
        var isTipMatch = IsHeterochromiaTip(tipText, out var similarity);
        LogDebugOncePerValue(
            CreateDebugLogKey("auto-battle-shiny-filter"),
            CreateMatchFilterDebugFingerprint(tipText, similarity, isTipMatch),
            "自动战斗异色保护筛选：TipText={TipText}, Expected={ExpectedTipText}, Similarity={Similarity:P1}, Threshold={Threshold:P1}, IsMatch={IsMatch}",
            FormatLogText(tipText),
            HeterochromiaTipText,
            similarity,
            HeterochromiaTipMatchThreshold,
            isTipMatch);

        if (!isTipMatch)
        {
            return false;
        }

        return ApplyAutoBattleShinySuspension(tipText, "自动战斗异色保护");
    }

    private bool ApplyAutoBattleEncounterRelievedDetection(string source)
    {
        var settings = NormalizeAutoBattleSettings(_autoBattleSettings);
        var encounterRelievedAction = settings.EncounterRelievedAction;
        if (!settings.IsEnabled
            || _isAutoBattleSuspendedForShiny
            || !RequiresAutoBattleEncounterRelieveDetection(encounterRelievedAction))
        {
            return false;
        }

        if (_isAutoBattleEncounterRelieved)
        {
            return true;
        }

        _isAutoBattleEncounterRelieved = true;
        _logger.LogInformation(
            "自动战斗：{Source}检测到奇遇效果解除，解除操作：{Action}。",
            source,
            GetAutoBattleEncounterRelievedActionLogText(encounterRelievedAction));
        return true;
    }

    private bool ApplyAutoBattleShinySuspension(string tipText, string source)
    {
        var settings = NormalizeAutoBattleSettings(_autoBattleSettings);
        if (!settings.IsEnabled)
        {
            return false;
        }

        if (_isAutoBattleSuspendedForShiny)
        {
            return true;
        }

        _isAutoBattleSuspendedForShiny = true;
        ResetAutoBattleSkillSelectionState();
        _logger.LogInformation(
            "自动战斗：{Source}检测到异色精灵提示，本场战斗暂停所有自动操作，退出战斗后恢复。TipText={TipText}",
            source,
            FormatLogText(tipText));
        return true;
    }
}
