using Microsoft.Extensions.Logging;
using RocoPilot.Configuration;
using RocoPilot.Helpers;
using RocoPilot.Models.Capture;
using RocoPilot.Models.Runtime;
using RocoPilot.Services.RuntimeTasks;

namespace RocoPilot.Services;

public sealed partial class RuntimeTaskService
{
    private static readonly TimeSpan AutoBattlePetSwitchConfirmDelay = TimeSpan.FromMilliseconds(1500);
    private static readonly TimeSpan AutoBattlePetSwitchStateCheckDelay = TimeSpan.FromMilliseconds(1500);

    private readonly SemaphoreSlim _autoBattleActionLock = new(1, 1);
    private AutoBattleSettings _autoBattleSettings = AutoBattleSettings.CreateDefault();
    private readonly AutoBattleController _battle = new();
    private bool _hasQueuedAutoBattleSkillFailureTipRecognitionForCurrentAction;
    private DateTimeOffset _enemyNameOcrAt = DateTimeOffset.MinValue;

    public AutoBattleSettings AutoBattleSettings => _autoBattleSettings.Clone();

    public void SetAutoBattleSettings(AutoBattleSettings settings)
    {
        var wasEnabled = _autoBattleSettings.IsEnabled;
        _autoBattleSettings = AutoBattleSettingsRules.Normalize(settings);
        if (_autoBattleSettings.IsEnabled != wasEnabled)
            _logger.LogInformation("自动战斗{State}，输入方式 {Method}", _autoBattleSettings.IsEnabled ? "已开启" : "已关闭",
                _autoBattleSettings.KeyboardInputMethod);
        if (CurrentState is { } state)
        {
            state.Options.AutoBattleSettings.KeyboardInputMethod = _autoBattleSettings.KeyboardInputMethod;
            state.Options.AutoBattleSettings.KeyboardHoldDurationMs = _autoBattleSettings.KeyboardHoldDurationMs;
        }
        if (!AutoBattleSettingsRules.RequiresReliefDetection(_autoBattleSettings.EncounterRelievedAction))
        {
            ResetAutoBattleEncounterRelievedActionState();
        }

        UpdateInfoOverlayTaskIndicators();
        _ = SaveAutoBattleSettingsAsync(_autoBattleSettings);
        NotifySettingsChanged();
    }

    private async Task SaveAutoBattleSettingsAsync(AutoBattleSettings settings)
    {
        try
        {
            await _localSettingsService.SaveSettingAsync(SettingsKeys.AutoBattleSettings, settings);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "保存自动战斗设置失败。");
        }
    }

    private async Task HandleAutoBattleSkillSelectionAsync(
        RuntimeTaskState state, CancellationToken cancellationToken)
    {
        if (_isSuspended) return;
        var now = DateTimeOffset.Now;
        var settings = _autoBattleSettings;
        if (settings.ReleaseSequence.Count == 0) return;

        if (_battle.Phase != AutoBattlePhase.SkillSelection)
        {
            BeginAutoBattleSkillSelectionTurn(settings, now);
            return;
        }

        if (!_battle.CanAct(settings, now)
            || !await _autoBattleActionLock.WaitAsync(0, cancellationToken)) return;

        try
        {
            var turn = _battle.CurrentTurn;
            if (turn is null || _isSuspended || _battle.IsSuspendedForShiny) return;
            var plan = _battle.PlanSkillSelection(
                settings, EncounterBloodlineRecognition.IsAvailable(state.RecognitionRegionConfig), now);
            if (plan.Action == AutoBattleAction.Skill && ShouldHoldAutoBattleAttackForUnconfirmedEncounterRelief())
            {
                return;
            }
            if (plan.Action == AutoBattleAction.None)
            {
                RefreshOverlayActivity(state, _overlayActivities.Publish(_battle.BattleId, turn.Id, $"wait:{turn.Id}",
                    RocoPilot.Models.Overlay.InfoOverlayActivityKind.Waiting, "识别精灵血脉或名单比对", string.Empty, now, completed: true));
                return;
            }
            if (plan.ShouldSendKeys
                && !await ExecuteBattleInputWithOverlayAsync(state, settings, plan, turn, cancellationToken)) return;
            if (!_battle.RecordAction(turn.Id, plan.Action, DateTimeOffset.Now)) return;
            if (plan.Action == AutoBattleAction.Capture && turn.Action != plan.Action)
                _logger.LogInformation("自动战斗：第 {Turn} 回合，捕捉，已发送 {Sequence}（{Description}）",
                    turn.Number, plan.Sequence, plan.Description);
            if (!plan.ShouldSendKeys)
                RefreshOverlayActivity(state, _overlayActivities.Publish(_battle.BattleId, turn.Id, $"manual:{turn.Id}",
                    RocoPilot.Models.Overlay.InfoOverlayActivityKind.Waiting, "等待手动操作", string.Empty, now, completed: true));

            if (!plan.ShouldSendKeys && turn.Action != plan.Action)
                _logger.LogInformation("自动战斗：第 {Turn} 回合，{Action}", turn.Number, plan.Description);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { _logger.LogWarning(ex, "自动战斗技能释放失败。"); }
        finally { _autoBattleActionLock.Release(); }
    }

    private bool ShouldHoldAutoBattleAttackForUnconfirmedEncounterRelief()
    {
        return _encounterCaptureButtonStateTracker.ShouldHoldAttackForUnconfirmedRelief;
    }

    private async Task HandleAutoBattlePetSwitchingAsync(
        RuntimeTaskState state,
        CancellationToken cancellationToken)
    {
        if (_isSuspended) return;
        var settings = _autoBattleSettings;
        if (!settings.IsEnabled || settings.ReleaseSequence.Count == 0)
        {
            _battle.ObservePetSwitching(true);
            return;
        }

        if (_battle.IsSuspendedForShiny)
        {
            _battle.ObservePetSwitching(true);
            return;
        }

        if (_battle.Phase == AutoBattlePhase.PetSwitching)
        {
            return;
        }

        if (!await _autoBattleActionLock.WaitAsync(0, cancellationToken))
        {
            return;
        }

        try
        {
            if (!_keyboardInputService.IsWindowAvailable(state.TargetWindow.Hwnd))
            {
                _logger.LogWarning("自动战斗换精灵未执行：目标游戏窗口句柄已失效。");
                return;
            }

            if (ShouldSkipAutoBattleKeyboardInput(state, settings))
            {
                return;
            }

            BeginAutoBattlePetSwitchingTurn(settings);
            var switchBattleId = _battle.BattleId;
            var switchTurn = _battle.TurnNumber;
            _overlayActivities.BeginTurn(switchTurn);
            _battle.ObservePetSwitching(true);

            var keyboardInputOptions = AutoBattleInputExecutor.CreateOptions(settings);
            for (var slot = 1; slot <= 6; slot++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (ShouldSkipAutoBattleKeyboardInput(state, settings))
                {
                    _battle.ObservePetSwitching(false);
                    return;
                }

                var slotKey = slot.ToString();
                RefreshOverlayActivity(state, _overlayActivities.Publish(switchBattleId, switchTurn, $"switch:{switchTurn}:{slot}",
                    RocoPilot.Models.Overlay.InfoOverlayActivityKind.PetSwitch, "切换精灵",
                    $"选择第 {slot} 只精灵", DateTimeOffset.Now, completed: true));
                _logger.LogDebug("自动战斗换精灵：尝试按 {SlotKey}", slotKey);
                await _keyboardInputService.SendSequenceAsync(
                    state.TargetWindow.Hwnd,
                    slotKey,
                    keyboardInputOptions,
                    cancellationToken);

                await Task.Delay(AutoBattlePetSwitchConfirmDelay, cancellationToken);

                if (ShouldSkipAutoBattleKeyboardInput(state, settings))
                {
                    _battle.ObservePetSwitching(false);
                    return;
                }

                await _keyboardInputService.SendSequenceAsync(
                    state.TargetWindow.Hwnd,
                    "Space",
                    keyboardInputOptions,
                    cancellationToken);

                await Task.Delay(AutoBattlePetSwitchStateCheckDelay, cancellationToken);

                if (ShouldSkipAutoBattleKeyboardInput(state, settings)) return;
                using var frame = _screenCaptureService.Capture(state.TargetWindow, state.Options.CaptureMethod);
                if (frame is null)
                {
                    _logger.LogWarning("自动战斗换精灵：按 Space 确认后未能获取画面，停止本轮自动换精灵。");
                    return;
                }

                if (await _battleScreen.IsBattlePetSwitchingAsync(state, frame, cancellationToken))
                {
                    _logger.LogDebug("自动战斗换精灵：第 {Slot} 只精灵确认后仍在切换界面，继续尝试下一只", slot);
                    continue;
                }

                _logger.LogInformation("自动战斗：切换到第 {Slot} 只精灵，按 Space 确认后离开切换界面", slot);
                return;
            }

            _logger.LogWarning("自动战斗换精灵失败：已尝试 1-6 并按 Space 确认，但仍处于切换精灵界面。");
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "自动战斗换精灵失败。");
        }
        finally
        {
            _autoBattleActionLock.Release();
        }
    }

    private void BeginAutoBattleSkillSelectionTurn(AutoBattleSettings settings, DateTimeOffset now)
    {
        _hasQueuedAutoBattleSkillFailureTipRecognitionForCurrentAction = false;
        var turn = _battle.BeginSkillSelection(settings, now);
        _overlayActivities.BeginTurn(turn.Id);
        _logger.LogDebug(
            "自动战斗：进入第 {TurnNumber} 回合技能选择，等待 {DelayMs}ms 后执行。ReleaseStep={ReleaseStep}, RoundIndex={RoundIndex}",
            turn.Number, settings.SkillSelectionActionDelayMs,
            AutoBattleSettingsRules.GetReleaseStepDisplay(turn.ReleaseStep), _battle.RoundIndex);
    }

    private void BeginAutoBattlePetSwitchingTurn(AutoBattleSettings settings)
    {
        var step = _battle.BeginPetSwitching(settings);
        _logger.LogInformation("自动战斗：切换精灵，下回合继续 {Step}", AutoBattleSettingsRules.GetReleaseStepDisplay(step));
    }

    private async Task<bool> TryHandleAutoBattleSkillReleaseFailureAsync(
        RuntimeTaskState state, CapturedFrame frame, CancellationToken cancellationToken)
    {
        if (_isSuspended) return false;
        if (!_battle.ShouldRecoverAfterSkillFailure(_autoBattleSettings, DateTimeOffset.Now)) return false;
        QueueAutoBattleSkillFailureTipRecognition(state, frame, _battle.TurnNumber, cancellationToken);
        if (!await TrySendAutoBattleEnergyRecoveryAsync(state, cancellationToken)) return false;
        _logger.LogInformation("自动战斗：第 {Turn} 回合已临时回能，原技能延后", _battle.TurnNumber);
        return true;
    }

    private void QueueAutoBattleSkillFailureTipRecognition(
        RuntimeTaskState state,
        CapturedFrame frame,
        int turnNumber,
        CancellationToken cancellationToken)
    {
        if (_isSuspended) return;
        if (_hasQueuedAutoBattleSkillFailureTipRecognitionForCurrentAction)
        {
            return;
        }

        if (Interlocked.Exchange(ref _queuedAutoBattleSkillFailureTipRecognition, 1) != 0)
        {
            return;
        }

        _hasQueuedAutoBattleSkillFailureTipRecognitionForCurrentAction = true;

        CapturedFrame frameReference;
        try
        {
            frameReference = frame.AddReference();
        }
        catch (ObjectDisposedException)
        {
            _ = Interlocked.Exchange(ref _queuedAutoBattleSkillFailureTipRecognition, 0);
            return;
        }

        var recognitionTask = Task.Run(
            async () =>
            {
                using (frameReference)
                {
                    try
                    {
                        var tipText = await _frameRecognizer.RecognizeRegionTextAsync(
                            state,
                            frameReference,
                            BattleTipRegionIds,
                            cancellationToken);
                        var failureReason = string.IsNullOrWhiteSpace(tipText)
                            ? "未识别到提示"
                            : LoggingHelper.FormatLogText(tipText);

                        _logger.LogInformation(
                            "自动战斗：第 {TurnNumber} 回合，技能失败提示：{FailureReason}",
                            turnNumber > 0 ? turnNumber : 1,
                            failureReason);
                    }
                    catch (OperationCanceledException)
                    {
                    }
                    catch (Exception ex)
                    {
                        _logger.LogDebug(ex, "自动战斗技能失败提示 OCR 失败");
                    }
                    finally
                    {
                        _ = Interlocked.Exchange(ref _queuedAutoBattleSkillFailureTipRecognition, 0);
                    }
                }
            });
        _session?.Track(recognitionTask);
    }

    private bool ShouldSkipAutoBattleKeyboardInput(RuntimeTaskState state, AutoBattleSettings settings)
    {
        return _isSuspended || _battle.IsSuspendedForShiny || !_autoBattleSettings.IsEnabled
            || !_battleInput.CanSend(state.TargetWindow.Hwnd, settings);
    }

    private async Task<bool> TrySendAutoBattleEnergyRecoveryAsync(RuntimeTaskState state, CancellationToken cancellationToken)
    {
        if (_isSuspended || _battle.IsSuspendedForShiny || !await _autoBattleActionLock.WaitAsync(0, cancellationToken)) return false;
        try
        {
            var turn = _battle.CurrentTurn;
            if (turn is null || _isSuspended || _battle.IsSuspendedForShiny) return false;
            var plan = new AutoBattlePlan(AutoBattleAction.EnergyRecovery, "X", "临时回能", "X");
            return await ExecuteBattleInputWithOverlayAsync(state, _autoBattleSettings, plan, turn, cancellationToken)
                && _battle.RecordAction(turn.Id, AutoBattleAction.EnergyRecovery, DateTimeOffset.Now);
        }
        catch (OperationCanceledException) { return false; }
        catch (Exception ex) { _logger.LogWarning(ex, "自动战斗回能失败。"); return false; }
        finally { _autoBattleActionLock.Release(); }
    }

    private void CompleteAutoBattleSkillSelectionState()
    {
        _battle.CompleteSkillSelection();
        _hasQueuedAutoBattleSkillFailureTipRecognitionForCurrentAction = false;
    }

    private void ResetAutoBattleBattleState()
    {
        _battle.ResetBattle();
        _overlayActivities.ResetBattle(_battle.BattleId);
        _hasQueuedAutoBattleSkillFailureTipRecognitionForCurrentAction = false;
    }

    private void ResetAutoBattleEncounterRelievedActionState() => _battle.ResetEncounterRelief();
}
