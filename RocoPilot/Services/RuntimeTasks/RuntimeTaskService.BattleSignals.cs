using Microsoft.Extensions.Logging;
using RocoPilot.Helpers;
using RocoPilot.Models.Capture;
using RocoPilot.Models.Runtime;
using RocoPilot.Services.RuntimeTasks;
using static RocoPilot.Services.RuntimeTasks.RuntimeFrameRecognizer;

namespace RocoPilot.Services;

public sealed partial class RuntimeTaskService
{
    private bool ApplyAutoBattleEncounterRelievedDetection(string source)
    {
        var settings = _autoBattleSettings;
        var encounterRelievedAction = settings.EncounterRelievedAction;
        if (_battle.IsSuspendedForShiny
            || !AutoBattleSettingsRules.RequiresReliefDetection(encounterRelievedAction))
        {
            return false;
        }

        if (_battle.IsEncounterRelieved)
        {
            return true;
        }

        if (!_battle.ObserveEncounterRelieved(settings)) return _battle.IsEncounterRelieved;
        _logger.LogInformation(
            "自动战斗：奇遇解除后的操作为“{Action}”",
            AutoBattleSettingsRules.GetRelievedActionDisplay(encounterRelievedAction));
        _logger.LogDebug("奇遇解除信号来源：{Source}", source);
        return true;
    }

    private bool ApplyAutoBattleShinySuspension(string tipText, string source, long battleId)
    {
        if (!_battle.ObserveShiny(battleId)) return false;
        if (CurrentState is not null && _scene == GameScene.Battle)
            _infoOverlayService.UpdateSnapshot(CreateInfoOverlaySnapshot("战斗中 - 异色保护", DateTimeOffset.Now));
        _logger.LogInformation("发现异色精灵：本场战斗已暂停所有自动操作，退出战斗后恢复");
        _logger.LogDebug("异色提示来源：{Source}，原文：{TipText}", source, LoggingHelper.FormatLogText(tipText));
        return true;
    }
}
