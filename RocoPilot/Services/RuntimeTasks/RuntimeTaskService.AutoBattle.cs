using Microsoft.Extensions.Logging;
using RocoPilot.Configuration;
using RocoPilot.Models.Capture;
using RocoPilot.Models.Encounters;
using RocoPilot.Models.ImageMatching;
using RocoPilot.Models.Runtime;
using RocoPilot.Services.RuntimeTasks;
using static RocoPilot.Services.RuntimeTasks.RuntimeDebugLogger;
using static RocoPilot.Services.RuntimeTasks.RuntimeFrameRecognizer;

namespace RocoPilot.Services;

public sealed partial class RuntimeTaskService
{
    private static readonly TimeSpan AutoBattlePetSwitchConfirmDelay = TimeSpan.FromMilliseconds(1500);
    private static readonly TimeSpan AutoBattlePetSwitchStateCheckDelay = TimeSpan.FromMilliseconds(1500);

    private readonly SemaphoreSlim _autoBattleActionLock = new(1, 1);
    private AutoBattleSettings _autoBattleSettings = AutoBattleSettings.CreateDefault();
    private readonly AutoBattleController _battle = new();
    private bool _hasLoggedCurrentAutoBattleTurnAction;
    private bool _hasLoggedCurrentAutoBattleCaptureButtonObservation;
    private bool _hasQueuedAutoBattleSkillFailureTipRecognitionForCurrentAction;
    private Task<AutoBattleSkillSelectionEnemyNameResult>? _autoBattleSkillSelectionEnemyNameTask;
    private long _autoBattleSkillSelectionEnemyNameTaskTurnId;

    public AutoBattleSettings AutoBattleSettings => _autoBattleSettings.Clone();

    public void SetAutoBattleSettings(AutoBattleSettings settings)
    {
        var previousIsEnabled = _autoBattleSettings.IsEnabled;
        _autoBattleSettings = AutoBattleSettingsRules.Normalize(settings);
        if (!AutoBattleSettingsRules.RequiresReliefDetection(_autoBattleSettings.EncounterRelievedAction))
        {
            ResetAutoBattleEncounterRelievedActionState();
        }

        UpdateInfoOverlayTaskIndicators();
        _ = SaveAutoBattleSettingsAsync(_autoBattleSettings);
        if (previousIsEnabled != _autoBattleSettings.IsEnabled)
        {
            NotifySettingsChanged();
        }
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
        RuntimeTaskState state, CapturedFrame frame, CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.Now;
        var settings = _autoBattleSettings;
        if (_battle.Phase != AutoBattlePhase.SkillSelection)
        {
            BeginAutoBattleSkillSelectionTurn(settings, now);
            return;
        }

        if (!await EnsureAutoBattleSkillSelectionEnemyNameResultAsync(state, frame, settings, now, cancellationToken)
            || !_battle.CanAct(settings, now)
            || !await _autoBattleActionLock.WaitAsync(0, cancellationToken)) return;

        try
        {
            var turn = _battle.CurrentTurn;
            if (turn is null || _battle.IsSuspendedForShiny) return;
            var plan = _battle.PlanSkillSelection(
                settings, EncounterBloodlineRecognition.IsAvailable(state.RecognitionRegionConfig), now);
            if (plan.Action == AutoBattleAction.Skill && ShouldHoldAutoBattleAttackForUnconfirmedEncounterRelief())
            {
                LogEncounterCaptureButtonDecisionForCurrentTurn("HoldForUnconfirmedEncounterRelief");
                return;
            }
            if (plan.Action == AutoBattleAction.None)
            {
                LogEncounterCaptureButtonDecisionForCurrentTurn("HoldForBloodlineTip");
                return;
            }
            if (plan.ShouldSendKeys
                && !await _battleInput.ExecuteAsync(state.TargetWindow.Hwnd, settings, plan, cancellationToken)) return;
            if (!_battle.RecordAction(turn.Id, plan.Action, DateTimeOffset.Now)) return;

            LogEncounterCaptureButtonDecisionForCurrentTurn(plan.Action.ToString());
            if (plan.ShouldSendKeys || turn.Action != plan.Action)
                LogAutoBattleTurnAction(plan.Description, plan.ShouldSendKeys ? plan.Sequence : null);
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
        var settings = _autoBattleSettings;
        if (!settings.IsEnabled)
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
        ClearAutoBattleTurnWork();
        var turn = _battle.BeginSkillSelection(settings, now);
        _logger.LogDebug(
            "自动战斗：进入第 {TurnNumber} 回合技能选择，等待 {DelayMs}ms 后执行。ReleaseStep={ReleaseStep}, RoundIndex={RoundIndex}",
            turn.Number, settings.SkillSelectionActionDelayMs,
            AutoBattleSettingsRules.GetReleaseStepDisplay(turn.ReleaseStep), _battle.RoundIndex);
    }

    private async Task<bool> EnsureAutoBattleSkillSelectionEnemyNameResultAsync(
        RuntimeTaskState state,
        CapturedFrame frame,
        AutoBattleSettings settings,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var turn = _battle.CurrentTurn;
        if (turn is null) return false;
        if (turn.EnemyNameResolved) return true;
        if (!_battle.IsEnemyNameRecognitionDue(settings, now)) return false;

        if (_autoBattleSkillSelectionEnemyNameTask is null
            || _autoBattleSkillSelectionEnemyNameTaskTurnId != turn.Id)
        {
            _autoBattleSkillSelectionEnemyNameTask = StartAutoBattleSkillSelectionEnemyNameRecognitionAsync(
                state,
                frame,
                cancellationToken);
            _autoBattleSkillSelectionEnemyNameTaskTurnId = turn.Id;
            return false;
        }

        if (!_autoBattleSkillSelectionEnemyNameTask.IsCompleted)
        {
            return false;
        }

        AutoBattleSkillSelectionEnemyNameResult result;
        try
        {
            result = await _autoBattleSkillSelectionEnemyNameTask;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "自动战斗技能选择精灵名 OCR 失败，将在下一轮重试。");
            ResetAutoBattleSkillSelectionEnemyNameTask();
            return false;
        }

        if (_battle.CurrentTurn?.Id != turn.Id) return false;
        var season = _encounterSeasonConfigService.GetCurrentSeason();
        if (season is null)
        {
            _battle.ConfirmEnemyName(turn.Id);
            return true;
        }

        if (string.IsNullOrWhiteSpace(result.MatchedName))
        {
            if (_encounterCaptureButtonStateTracker.HasSeenDisabled)
            {
                _battle.ConfirmEnemyName(turn.Id);
                _logger.LogDebug(
                    "自动战斗：捕捉按钮处于禁用阶段，按奇遇第一形态继续普通战斗。EnemyNameRaw={EnemyNameRaw}",
                    FormatLogText(result.RawText));
                return true;
            }

            _debugLog.Write(
                CreateDebugLogKey("auto-battle-skill-selection-enemy-missing", season.Id),
                CreateTextDebugFingerprint(result.RawText),
                "自动战斗技能选择精灵名筛选：EnemyNameRaw={EnemyNameRaw}, 未匹配到有效精灵名，等待下一轮 OCR。",
                FormatLogText(result.RawText));
            ResetAutoBattleSkillSelectionEnemyNameTask();
            return false;
        }

        await ApplyAutoBattleSkillSelectionEnemyNameResultAsync(
            season,
            result,
            cancellationToken);
        _battle.ConfirmEnemyName(turn.Id);
        return true;
    }

    private Task<AutoBattleSkillSelectionEnemyNameResult> StartAutoBattleSkillSelectionEnemyNameRecognitionAsync(
        RuntimeTaskState state,
        CapturedFrame frame,
        CancellationToken cancellationToken)
    {
        CapturedFrame frameReference;
        try
        {
            frameReference = frame.AddReference();
        }
        catch (ObjectDisposedException)
        {
            return Task.FromResult(new AutoBattleSkillSelectionEnemyNameResult(
                string.Empty,
                string.Empty));
        }

        var session = _session ?? throw new InvalidOperationException("运行会话已结束。");
        return session.RunBackground(
            async () =>
            {
                using (frameReference)
                {
                    var rawText = await _frameRecognizer.RecognizeRegionTextAsync(
                        state,
                        frameReference,
                        BattleEnemyNameRegionIds,
                        cancellationToken,
                        "自动战斗技能选择");
                    var season = _encounterSeasonConfigService.GetCurrentSeason();
                    var matchedName = season is null
                        ? string.Empty
                        : await MatchRecognizedSpiritNameAsync(rawText, cancellationToken);
                    return new AutoBattleSkillSelectionEnemyNameResult(
                        rawText,
                        matchedName);
                }
            });
    }

    private async Task ApplyAutoBattleSkillSelectionEnemyNameResultAsync(
        EncounterSeasonDefinition season,
        AutoBattleSkillSelectionEnemyNameResult result,
        CancellationToken cancellationToken)
    {
        var matchedName = result.MatchedName;
        var spiritNameMatchThreshold = GetSpiritNameMatchThreshold();
        _debugLog.Write(
            CreateDebugLogKey("auto-battle-skill-selection-enemy", season.Id),
            string.Join(
                "|",
                CreateTextDebugFingerprint(result.RawText),
                CreateTextDebugFingerprint(matchedName)),
            "自动战斗技能选择精灵名筛选：EnemyNameRaw={EnemyNameRaw}, Matched={SpiritName}, MatchThreshold={MatchThreshold:P1}",
            FormatLogText(result.RawText),
            matchedName,
            spiritNameMatchThreshold);

        if (TryGetPendingShinyDetection(season.Id, out _))
        {
            await RecordPendingShinyCaptureAsync(season, matchedName, cancellationToken);
            ClearPendingShinyDetection();
        }
    }

    private void ResetAutoBattleSkillSelectionEnemyNameTask()
    {
        _autoBattleSkillSelectionEnemyNameTask = null;
        _autoBattleSkillSelectionEnemyNameTaskTurnId = 0;
    }

    private void BeginAutoBattlePetSwitchingTurn(AutoBattleSettings settings)
    {
        var step = _battle.BeginPetSwitching(settings);
        _hasLoggedCurrentAutoBattleTurnAction = false;
        LogAutoBattleTurnAction($"切换精灵，本回合不释放技能，下回合继续 {AutoBattleSettingsRules.GetReleaseStepDisplay(step)}");
    }

    private async Task<bool> TryHandleAutoBattleSkillReleaseFailureAsync(
        RuntimeTaskState state, CapturedFrame frame, CancellationToken cancellationToken)
    {
        if (!_battle.ShouldRecoverAfterSkillFailure(_autoBattleSettings, DateTimeOffset.Now)) return false;
        QueueAutoBattleSkillFailureTipRecognition(state, frame, _battle.TurnNumber, cancellationToken);
        if (!await TrySendAutoBattleEnergyRecoveryAsync(state, cancellationToken)) return false;
        LogAutoBattleTurnAction("技能未离开选择界面，临时回能 X，原技能延后", "X", forceInformation: true);
        return true;
    }

    private void QueueAutoBattleSkillFailureTipRecognition(
        RuntimeTaskState state,
        CapturedFrame frame,
        int turnNumber,
        CancellationToken cancellationToken)
    {
        if (_hasQueuedAutoBattleSkillFailureTipRecognitionForCurrentAction)
        {
            _logger.LogDebug("自动战斗技能失败提示 OCR 本次技能动作已触发过，本次跳过。");
            return;
        }

        if (Interlocked.Exchange(ref _queuedAutoBattleSkillFailureTipRecognition, 1) != 0)
        {
            _logger.LogDebug("自动战斗技能失败提示 OCR 已有待处理任务，本次跳过。");
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
                            cancellationToken,
                            "自动战斗技能失败");
                        var failureReason = string.IsNullOrWhiteSpace(tipText)
                            ? "未识别到提示"
                            : FormatLogText(tipText);

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
        return _battle.IsSuspendedForShiny || !_autoBattleSettings.IsEnabled
            || !_battleInput.CanSend(state.TargetWindow.Hwnd, settings);
    }

    private async Task<bool> TrySendAutoBattleEnergyRecoveryAsync(RuntimeTaskState state, CancellationToken cancellationToken)
    {
        if (_battle.IsSuspendedForShiny || !await _autoBattleActionLock.WaitAsync(0, cancellationToken)) return false;
        try
        {
            var turn = _battle.CurrentTurn;
            if (turn is null || _battle.IsSuspendedForShiny) return false;
            var plan = new AutoBattlePlan(AutoBattleAction.EnergyRecovery, "X", "临时回能", "X");
            return await _battleInput.ExecuteAsync(state.TargetWindow.Hwnd, _autoBattleSettings, plan, cancellationToken)
                && _battle.RecordAction(turn.Id, AutoBattleAction.EnergyRecovery, DateTimeOffset.Now);
        }
        catch (OperationCanceledException) { return false; }
        catch (Exception ex) { _logger.LogWarning(ex, "自动战斗回能失败。"); return false; }
        finally { _autoBattleActionLock.Release(); }
    }

    private void LogAutoBattleTurnAction(
        string description,
        string? sequence = null,
        bool forceInformation = false)
    {
        var turnNumber = Math.Max(1, _battle.TurnNumber);

        if (!_hasLoggedCurrentAutoBattleTurnAction || forceInformation)
        {
            if (string.IsNullOrWhiteSpace(sequence))
            {
                _logger.LogInformation("自动战斗：第 {TurnNumber} 回合，{Description}", turnNumber, description);
            }
            else
            {
                _logger.LogInformation(
                    "自动战斗：第 {TurnNumber} 回合，{Description}（序列 {Sequence}）",
                    turnNumber,
                    description,
                    sequence);
            }

            _hasLoggedCurrentAutoBattleTurnAction = true;
            return;
        }

        if (string.IsNullOrWhiteSpace(sequence))
        {
            _logger.LogDebug("自动战斗：第 {TurnNumber} 回合重试，{Description}", turnNumber, description);
        }
        else
        {
            _logger.LogDebug(
                "自动战斗：第 {TurnNumber} 回合重试，{Description}（序列 {Sequence}）",
                turnNumber,
                description,
                sequence);
        }
    }

    private void CompleteAutoBattleSkillSelectionState()
    {
        _battle.CompleteSkillSelection();
        ClearAutoBattleTurnWork();
    }

    private void ResetAutoBattleBattleState()
    {
        _battle.ResetBattle();
        ClearAutoBattleTurnWork();
    }

    private void ResetAutoBattleEncounterRelievedActionState() => _battle.ResetEncounterRelief();

    private void ClearAutoBattleTurnWork()
    {
        ResetAutoBattleSkillSelectionEnemyNameTask();
        _hasLoggedCurrentAutoBattleTurnAction = false;
        _hasLoggedCurrentAutoBattleCaptureButtonObservation = false;
        _hasQueuedAutoBattleSkillFailureTipRecognitionForCurrentAction = false;
    }

    private sealed record AutoBattleSkillSelectionEnemyNameResult(
        string RawText,
        string MatchedName);
}
