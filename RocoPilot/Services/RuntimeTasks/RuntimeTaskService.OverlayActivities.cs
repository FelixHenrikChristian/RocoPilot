using Microsoft.Extensions.Logging;
using RocoPilot.Models.Capture;
using RocoPilot.Models.Overlay;
using RocoPilot.Models.Runtime;
using RocoPilot.Services.Logging;

namespace RocoPilot.Services;

public sealed partial class RuntimeTaskService
{
    private readonly InfoOverlayActivityTracker _overlayActivities = new();
    private string _lastInfoOverlayStatus = "状态待识别";
    private long _infoOverlayRevision;
    private sealed record IndependentTaskOverlay(string Name, IndependentTaskProgress Progress);
    private IndependentTaskOverlay? _independentTaskOverlay;

    // 名称只补充展示，在现有后台 OCR 循环中执行，不参与战斗放行。
    private async Task UpdateRuntimeSpiritNameAsync(RuntimeTaskState state, CapturedFrame frame,
        long battleId, CancellationToken cancellationToken)
    {
        if (!state.Options.InfoOverlayEnabled) return;
        var turnId = _battle.CurrentTurn?.Id;
        var rawText = await _frameRecognizer.RecognizeRegionTextAsync(
            state, frame, BattleEnemyNameRegionIds, cancellationToken);
        var name = await MatchRecognizedSpiritNameAsync(rawText, cancellationToken);
        if (battleId != _battle.BattleId) return;
        NoteBattleSpirit(battleId, name);
        RefreshOverlayActivity(state, _overlayActivities.RecognizeSpirit(battleId, turnId, name, DateTimeOffset.Now));
    }

    public void UpdateIndependentTaskStatus(string? taskName, IndependentTaskProgress? progress = null)
    {
        Volatile.Write(ref _independentTaskOverlay,
            string.IsNullOrWhiteSpace(taskName) ? null : new IndependentTaskOverlay(taskName, progress ?? new("准备启动")));
        if (!IsRunning) return;
        try
        {
            _infoOverlayService.UpdateSnapshot(CreateInfoOverlaySnapshot(Volatile.Read(ref _lastInfoOverlayStatus), DateTimeOffset.Now));
        }
        catch (Exception ex)
        {
            LogOverlayFailure(ex);
        }
    }

    // 遮罩只负责展示，更新失败不影响任务；刷新很频繁，同一种故障不逐次记录。
    private void LogOverlayFailure(Exception exception)
    {
        if (_overlayFailures.TryEnter(out var skipped))
        {
            _logger.LogWarning(exception, "信息遮罩更新失败，任务照常执行{Repeat}", RepeatedFailureGate.Describe(skipped));
        }
    }

    private void RefreshOverlayActivity(RuntimeTaskState state, InfoOverlayActivity? activity)
    {
        if (activity is null || !ReferenceEquals(CurrentState, state) || _isSuspended) return;
        try
        {
            _infoOverlayService.UpdateSnapshot(CreateInfoOverlaySnapshot(Volatile.Read(ref _lastInfoOverlayStatus), DateTimeOffset.Now));
        }
        catch (Exception ex)
        {
            LogOverlayFailure(ex);
        }
    }

    private void PublishBattleInputProgress(RuntimeTaskState state, long battleId, AutoBattleTurn turn,
        AutoBattlePlan plan, string sequence, bool completed)
    {
        if (completed)
        {
            // 给用户看的是这一回合做了什么；捕捉时带上目标精灵。实际按键另记一条调试信息。
            var creature = _overlayActivities.CreatureName;
            if (plan.Action == AutoBattleAction.Capture && creature.Length > 0)
            {
                _logger.LogInformation("自动战斗：第 {Turn} 回合，{Action}：{SpiritName}",
                    turn.Number, plan.Description, creature);
            }
            else
            {
                _logger.LogInformation("自动战斗：第 {Turn} 回合，{Action}", turn.Number, plan.Description);
            }

            _logger.LogDebug("自动战斗：第 {Turn} 回合已发送按键 {Sequence}", turn.Number, sequence);
        }

        if (_battle.IsSuspendedForShiny || !ReferenceEquals(CurrentState, state) || _isSuspended) return;
        var title = InfoOverlayIslandPresentation.BattleInputTitle(plan);
        // 技能序列里的 X 同样是回能，步骤行与标题保持一致。
        var kind = plan.Action == AutoBattleAction.Capture ? InfoOverlayActivityKind.Capture
            : plan.Action == AutoBattleAction.EnergyRecovery || title == "回能" ? InfoOverlayActivityKind.EnergyRecovery
            : InfoOverlayActivityKind.Skill;
        var isCustomSequence = plan.Action == AutoBattleAction.Skill && plan.FallbackSequence is null;
        var bloodline = _battle.Bloodline;
        var description = plan.Action == AutoBattleAction.Capture
            ? bloodline == EncounterBloodlineKind.Unrecognized ? string.Empty
                : $"{EncounterBloodlineRecognition.GetDisplayName(bloodline)}血脉"
            : isCustomSequence && plan.DisplayKey != plan.Sequence ? plan.DisplayKey : string.Empty;
        RefreshOverlayActivity(state, _overlayActivities.Publish(battleId, turn.Id,
            $"input:{turn.Id}:{plan.Action}:{sequence}", kind, title,
            description,
            DateTimeOffset.Now, completed));
    }

    private async Task<bool> ExecuteBattleInputWithOverlayAsync(RuntimeTaskState state, AutoBattleSettings settings,
        AutoBattlePlan plan, AutoBattleTurn turn, CancellationToken token)
    {
        if (_isSuspended || !ReferenceEquals(CurrentState, state)) return false;
        var battleId = _battle.BattleId;
        try
        {
            return await _battleInput.ExecuteAsync(state.TargetWindow.Hwnd, settings, plan, token,
                (sequence, completed) => PublishBattleInputProgress(state, battleId, turn, plan, sequence, completed));
        }
        catch (OperationCanceledException)
        {
            // 取消不是操作失败；停止/挂起会清理事件，不能显示“已发送”。
            _overlayActivities.Clear();
            throw;
        }
        catch (Exception ex)
        {
            RefreshOverlayActivity(state, _overlayActivities.Publish(battleId, turn.Id, $"error:{turn.Id}:{plan.Action}",
                InfoOverlayActivityKind.Error, "按键未发送", ex.Message, DateTimeOffset.Now, completed: true));
            throw;
        }
    }
}
