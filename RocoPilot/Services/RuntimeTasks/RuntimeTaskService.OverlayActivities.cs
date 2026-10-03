using Microsoft.Extensions.Logging;
using RocoPilot.Models.Overlay;
using RocoPilot.Models.Runtime;

namespace RocoPilot.Services;

public sealed partial class RuntimeTaskService
{
    private readonly InfoOverlayActivityTracker _overlayActivities = new();
    private string _lastInfoOverlayStatus = "状态待识别";
    private long _infoOverlayRevision;

    private void RefreshOverlayActivity(RuntimeTaskState state, InfoOverlayActivity? activity)
    {
        if (activity is null || !ReferenceEquals(CurrentState, state) || _isSuspended) return;
        try
        {
            _infoOverlayService.UpdateSnapshot(CreateInfoOverlaySnapshot(Volatile.Read(ref _lastInfoOverlayStatus), DateTimeOffset.Now));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "更新灵动岛信息失败，不影响任务执行");
        }
    }

    private void PublishBattleInputProgress(RuntimeTaskState state, long battleId, long turnId,
        AutoBattlePlan plan, string sequence, bool completed)
    {
        if (_battle.IsSuspendedForShiny || !ReferenceEquals(CurrentState, state) || _isSuspended) return;
        var kind = plan.Action switch
        {
            AutoBattleAction.Capture => InfoOverlayActivityKind.Capture,
            AutoBattleAction.EnergyRecovery => InfoOverlayActivityKind.EnergyRecovery,
            _ => InfoOverlayActivityKind.Skill
        };
        var isCustomSequence = plan.Action == AutoBattleAction.Skill && plan.FallbackSequence is null;
        var title = plan.Action switch
        {
            AutoBattleAction.Capture => "捕捉精灵",
            AutoBattleAction.EnergyRecovery => "恢复能量",
            _ when isCustomSequence => "执行公共序列",
            _ => plan.DisplayKey is "1" or "2" or "3" or "4" ? $"使用技能 {plan.DisplayKey}"
                : plan.DisplayKey == "X" ? "恢复能量" : "执行公共序列"
        };
        var description = isCustomSequence && plan.DisplayKey != plan.Sequence
            ? plan.DisplayKey : string.Empty;
        RefreshOverlayActivity(state, _overlayActivities.Publish(battleId, turnId,
            $"input:{turnId}:{plan.Action}:{sequence}", kind, title,
            description,
            DateTimeOffset.Now, completed));
    }

    private async Task<bool> ExecuteBattleInputWithOverlayAsync(RuntimeTaskState state, AutoBattleSettings settings,
        AutoBattlePlan plan, AutoBattleTurn turn, CancellationToken token)
    {
        var battleId = _battle.BattleId;
        try
        {
            return await _battleInput.ExecuteAsync(state.TargetWindow.Hwnd, settings, plan, token,
                (sequence, completed) => PublishBattleInputProgress(state, battleId, turn.Id, plan, sequence, completed));
        }
        catch (OperationCanceledException)
        {
            // 取消不是操作失败；停止/挂起会清理事件，不能显示“已发送”。
            _overlayActivities.Clear();
            throw;
        }
        catch
        {
            RefreshOverlayActivity(state, _overlayActivities.Publish(battleId, turn.Id, $"error:{turn.Id}:{plan.Action}",
                InfoOverlayActivityKind.Error, "按键发送未完成", "请查看日志中的失败原因", DateTimeOffset.Now, completed: true));
            throw;
        }
    }
}
