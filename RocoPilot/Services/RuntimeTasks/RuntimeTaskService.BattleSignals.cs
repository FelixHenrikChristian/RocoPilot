using Microsoft.Extensions.Logging;
using RocoPilot.Helpers;
using RocoPilot.Models.Capture;
using RocoPilot.Models.Runtime;
using RocoPilot.Services.RuntimeTasks;
using static RocoPilot.Services.RuntimeTasks.RuntimeDebugLogger;
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
            "自动战斗：{Source}检测到奇遇效果解除，解除操作：{Action}。",
            source,
            AutoBattleSettingsRules.GetRelievedActionDisplay(encounterRelievedAction));
        return true;
    }

    private bool ApplyAutoBattleShinySuspension(string tipText, string source, long battleId)
    {
        if (!_battle.ObserveShiny(battleId)) return _battle.IsSuspendedForShiny;
        _logger.LogInformation(
            "自动战斗：{Source}检测到异色精灵提示，本场战斗暂停所有自动操作，退出战斗后恢复。TipText={TipText}",
            source,
            FormatLogText(tipText));
        return true;
    }
}
