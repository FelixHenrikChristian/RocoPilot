using Microsoft.Extensions.Logging;

using RocoPilot.Configuration;
using RocoPilot.Models.Capture;
using RocoPilot.Models.Runtime;

namespace RocoPilot.Services;

public sealed partial class RuntimeTaskService
{
    private const string AutoBattleBossComboRecoverySequence = "X, X, X, X, X, X, Space";

    private static readonly TimeSpan AutoBattleBossComboEnergyTipDelay = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan AutoBattleBossComboWatchdogInterval = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan AutoBattleBossComboWatchdogRetryDelay = TimeSpan.FromSeconds(5);
    private static readonly string[] BattleBossNameRegionIds =
    [
        RecognitionRegionIds.BattleBossName,
        "shouling_name"
    ];
    private static readonly string[] BattleBossComboTipRegionIds =
    [
        RecognitionRegionIds.BattleBossComboTip,
        "shouling_tip"
    ];

    private volatile AutoBattleType _autoBattleType;
    private int _autoBattleBossComboActionState;
    private Task<AutoBattleBossComboPromptResult>? _autoBattleBossComboPromptTask;
    private int _autoBattleBossComboPromptTaskTurnNumber;
    private bool _hasAutoBattleBossComboPromptResultForCurrentTurn;
    private bool _isAutoBattleBossComboPromptMatchedForCurrentTurn;
    private long _autoBattleBossComboWatchdogDueAtUtcTicks;
    private int _autoBattleBossComboWatchdogAction;
    private int _autoBattleBossComboActiveAttempt;

    private bool IsAutoBattleBossBattle => _autoBattleType == AutoBattleType.Boss;

    private bool HasAutoBattleBossComboStarted =>
        Volatile.Read(ref _autoBattleBossComboActionState) != (int)AutoBattleBossComboActionState.Ready;

    private string? AutoBattleBossComboStatusText =>
        (AutoBattleBossComboActionState)Volatile.Read(ref _autoBattleBossComboActionState) switch
        {
            AutoBattleBossComboActionState.Executing =>
                (AutoBattleBossComboAttempt)Volatile.Read(ref _autoBattleBossComboActiveAttempt)
                    == AutoBattleBossComboAttempt.Recovery
                    ? "首领战斗 - 连招解卡"
                    : "首领战斗 - 连招配置",
            AutoBattleBossComboActionState.AwaitingBattleExit => "首领战斗 - 等待连招结算",
            _ => null
        };

    private bool TryActivateAutoBattleBossBattle(
        string? bossNameText,
        AutoBattleSettings settings)
    {
        if (_autoBattleType == AutoBattleType.Normal
            || !BossBattleRecognition.HasRecognizedName(bossNameText))
        {
            return false;
        }

        if (_autoBattleType != AutoBattleType.Boss)
        {
            _autoBattleType = AutoBattleType.Boss;
            _autoBattleRoundIndex = 0;
            _currentAutoBattleReleaseStep = GetCurrentAutoBattleReleaseStep(settings);
            ResetAutoBattleEncounterRelievedActionState();
            ResetAutoBattleShinySuspendState();
            ClearPendingShinyDetection();
            _logger.LogInformation(
                "自动战斗：普通精灵名未匹配，首领名称区域识别到文字，已进入首领战斗并切换到独立释放序列。BossName={BossName}, ReleaseStep={ReleaseStep}",
                FormatLogText(bossNameText),
                GetAutoBattleReleaseStepDisplay(_currentAutoBattleReleaseStep));
        }

        return true;
    }

    private void ActivateNormalAutoBattleIfUnknown()
    {
        if (_autoBattleType == AutoBattleType.Unknown)
        {
            _autoBattleType = AutoBattleType.Normal;
        }
    }

    private static AutoBattleSkillSelectionPlan BuildBossAutoBattleSkillSelectionPlan(
        AutoBattleSettings settings,
        AutoBattleReleaseStep releaseStep)
    {
        return BuildAutoBattleReleaseSkillPlan(settings, releaseStep);
    }

    private async Task<bool> EnsureBossBattleSkillSelectionCanContinueAsync(
        RuntimeTaskState state,
        CapturedFrame frame,
        AutoBattleSettings settings,
        CancellationToken cancellationToken)
    {
        if (!IsAutoBattleBossBattle)
        {
            return true;
        }

        if (Volatile.Read(ref _autoBattleBossComboActionState)
            != (int)AutoBattleBossComboActionState.Ready)
        {
            return false;
        }

        if (_hasAutoBattleBossComboPromptResultForCurrentTurn)
        {
            if (!_isAutoBattleBossComboPromptMatchedForCurrentTurn)
            {
                return true;
            }

            await TryExecuteAutoBattleBossComboAsync(state, settings, cancellationToken);
            return false;
        }

        var promptTask = _autoBattleBossComboPromptTask;
        if (promptTask is null
            || _autoBattleBossComboPromptTaskTurnNumber != _currentAutoBattleTurnNumber)
        {
            _autoBattleBossComboPromptTask = StartAutoBattleBossComboPromptRecognitionAsync(
                state,
                frame,
                cancellationToken);
            _autoBattleBossComboPromptTaskTurnNumber = _currentAutoBattleTurnNumber;
            return false;
        }

        if (!promptTask.IsCompleted)
        {
            return false;
        }

        AutoBattleBossComboPromptResult result;
        try
        {
            result = await promptTask;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "首领战斗连招提示 OCR 失败，将在下一轮重试。");
            ResetAutoBattleBossSkillSelectionState();
            return false;
        }

        _hasAutoBattleBossComboPromptResultForCurrentTurn = true;
        _isAutoBattleBossComboPromptMatchedForCurrentTurn = result.IsMatch;
        LogAutoBattleBossComboPromptResult(result);
        if (!result.IsMatch)
        {
            return true;
        }

        await TryExecuteAutoBattleBossComboAsync(state, settings, cancellationToken);
        return false;
    }

    private Task<AutoBattleBossComboPromptResult> StartAutoBattleBossComboPromptRecognitionAsync(
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
            return Task.FromResult(new AutoBattleBossComboPromptResult(string.Empty, 0, false));
        }

        return Task.Run(
            async () =>
            {
                using (frameReference)
                {
                    return await RecognizeAutoBattleBossComboPromptAsync(
                        state,
                        frameReference,
                        cancellationToken);
                }
            },
            cancellationToken);
    }

    private async Task UpdateAutoBattleBossOcrSignalAsync(
        RuntimeTaskState state,
        CapturedFrame frame,
        CancellationToken cancellationToken)
    {
        var settings = NormalizeAutoBattleSettings(_autoBattleSettings);
        if (!IsAutoBattleBossBattle || !settings.IsEnabled)
        {
            return;
        }

        var actionState = (AutoBattleBossComboActionState)Volatile.Read(
            ref _autoBattleBossComboActionState);
        if (actionState == AutoBattleBossComboActionState.AwaitingBattleExit)
        {
            await TryRunAutoBattleBossComboWatchdogAsync(state, settings, cancellationToken);
            return;
        }

        if (actionState != AutoBattleBossComboActionState.Ready)
        {
            return;
        }

        var result = await RecognizeAutoBattleBossComboPromptAsync(state, frame, cancellationToken);
        LogAutoBattleBossComboPromptResult(result);
        if (result.IsMatch)
        {
            await TryExecuteAutoBattleBossComboAsync(state, settings, cancellationToken);
        }
    }

    private async Task<AutoBattleBossComboPromptResult> RecognizeAutoBattleBossComboPromptAsync(
        RuntimeTaskState state,
        CapturedFrame frame,
        CancellationToken cancellationToken)
    {
        if (!HasEnabledRecognitionRegion(state, BattleBossComboTipRegionIds))
        {
            return new AutoBattleBossComboPromptResult(string.Empty, 0, false);
        }

        var tipText = await RecognizeRegionTextAsync(
            state,
            frame,
            BattleBossComboTipRegionIds,
            cancellationToken,
            "首领战斗连招提示");
        var isMatch = BossBattleRecognition.IsComboPrompt(tipText, out var similarity);
        return new AutoBattleBossComboPromptResult(tipText, similarity, isMatch);
    }

    private async Task<string> RecognizeAutoBattleBossNameAsync(
        RuntimeTaskState state,
        CapturedFrame frame,
        CancellationToken cancellationToken)
    {
        if (!HasEnabledRecognitionRegion(state, BattleBossNameRegionIds))
        {
            return string.Empty;
        }

        return await RecognizeRegionTextAsync(
            state,
            frame,
            BattleBossNameRegionIds,
            cancellationToken,
            "首领战斗名称");
    }

    private void LogAutoBattleBossComboPromptResult(AutoBattleBossComboPromptResult result)
    {
        LogDebugOncePerValue(
            CreateDebugLogKey("auto-battle-boss-combo-tip-filter"),
            CreateMatchFilterDebugFingerprint(result.Text, result.Similarity, result.IsMatch),
            "首领战斗连招提示筛选：TipText={TipText}, Expected={ExpectedTipText}, Similarity={Similarity:P1}, Threshold={Threshold:P1}, IsMatch={IsMatch}",
            FormatLogText(result.Text),
            BossBattleRecognition.ComboPromptText,
            result.Similarity,
            BossBattleRecognition.ComboPromptMatchThreshold,
            result.IsMatch);
    }

    private Task<bool> TryExecuteAutoBattleBossComboAsync(
        RuntimeTaskState state,
        AutoBattleSettings settings,
        CancellationToken cancellationToken)
    {
        return TryExecuteAutoBattleBossComboAttemptAsync(
            state,
            settings,
            AutoBattleBossComboAttempt.Configured,
            AutoBattleBossComboActionState.Ready,
            cancellationToken);
    }

    private async Task TryRunAutoBattleBossComboWatchdogAsync(
        RuntimeTaskState state,
        AutoBattleSettings settings,
        CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref _autoBattleBossComboActionState)
                != (int)AutoBattleBossComboActionState.AwaitingBattleExit
            || DateTime.UtcNow.Ticks < Volatile.Read(ref _autoBattleBossComboWatchdogDueAtUtcTicks))
        {
            return;
        }

        var attempt = (AutoBattleBossComboAttempt)Volatile.Read(
            ref _autoBattleBossComboWatchdogAction);
        await TryExecuteAutoBattleBossComboAttemptAsync(
            state,
            settings,
            attempt,
            AutoBattleBossComboActionState.AwaitingBattleExit,
            cancellationToken);
    }

    private async Task<bool> TryExecuteAutoBattleBossComboAttemptAsync(
        RuntimeTaskState state,
        AutoBattleSettings settings,
        AutoBattleBossComboAttempt attempt,
        AutoBattleBossComboActionState expectedState,
        CancellationToken cancellationToken)
    {
        if (!IsAutoBattleBossBattle
            || !settings.IsEnabled
            || Volatile.Read(ref _autoBattleBossComboActionState) != (int)expectedState)
        {
            return false;
        }

        if (!await _autoBattleActionLock.WaitAsync(0, cancellationToken))
        {
            return false;
        }

        var attemptCompleted = false;
        try
        {
            Volatile.Write(ref _autoBattleBossComboActiveAttempt, (int)attempt);
            if (Interlocked.CompareExchange(
                ref _autoBattleBossComboActionState,
                (int)AutoBattleBossComboActionState.Executing,
                (int)expectedState)
                != (int)expectedState)
            {
                return false;
            }

            UpdateRecognizedInfoOverlaySnapshot(CreateInfoOverlaySnapshot(
                AutoBattleBossComboStatusText ?? "首领战斗 - 连招配置",
                DateTimeOffset.Now));

            if (expectedState == AutoBattleBossComboActionState.AwaitingBattleExit
                && DateTime.UtcNow.Ticks < Volatile.Read(ref _autoBattleBossComboWatchdogDueAtUtcTicks))
            {
                return false;
            }

            if (!_keyboardInputService.IsWindowAvailable(state.TargetWindow.Hwnd))
            {
                _logger.LogWarning("首领战斗连招未执行：目标游戏窗口句柄已失效。");
                return false;
            }

            if (ShouldSkipAutoBattleKeyboardInput(state, settings))
            {
                return false;
            }

            var sequence = attempt == AutoBattleBossComboAttempt.Configured
                ? await SendConfiguredAutoBattleBossComboAsync(state, settings, cancellationToken)
                : await SendAutoBattleBossComboRecoveryAsync(state, settings, cancellationToken);

            attemptCompleted = true;
            if (!IsAutoBattleBossBattle)
            {
                return true;
            }

            ScheduleNextAutoBattleBossComboWatchdog(attempt);
            ResetAutoBattleSkillSelectionState();
            if (attempt == AutoBattleBossComboAttempt.Configured)
            {
                _logger.LogInformation(
                    "首领战斗：已逐步配置 6 个连招技能并按 Space 确认；若 5 分钟后仍未退出战斗，将发送 6 次 X + Space 解卡。Sequence={Sequence}",
                    sequence);
            }
            else
            {
                _logger.LogWarning(
                    "首领战斗看门狗：等待 5 分钟后战斗仍未结束，已发送 6 次 X + Space 解卡；若再等待 5 分钟仍未结束，将重新执行正常连招配置。");
            }

            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "首领战斗连招处理失败。Attempt={Attempt}", attempt);
            return false;
        }
        finally
        {
            if (!attemptCompleted
                && Volatile.Read(ref _autoBattleBossComboActionState)
                    == (int)AutoBattleBossComboActionState.Executing)
            {
                if (expectedState == AutoBattleBossComboActionState.AwaitingBattleExit)
                {
                    Volatile.Write(
                        ref _autoBattleBossComboWatchdogDueAtUtcTicks,
                        DateTime.UtcNow.Add(AutoBattleBossComboWatchdogRetryDelay).Ticks);
                }

                Volatile.Write(
                    ref _autoBattleBossComboActionState,
                    (int)expectedState);
            }

            _autoBattleActionLock.Release();
        }
    }

    private async Task<string> SendConfiguredAutoBattleBossComboAsync(
        RuntimeTaskState state,
        AutoBattleSettings settings,
        CancellationToken cancellationToken)
    {
        var skillKeys = BossBattleComboSequence.ParseOrDefault(settings.BossComboSequence);
        var inputOptions = CreateAutoBattleKeyboardInputOptions(settings);
        var stepDelay = TimeSpan.FromMilliseconds(Math.Max(
            settings.KeyboardIntervalMs,
            (int)AutoBattleBossComboEnergyTipDelay.TotalMilliseconds));

        for (var index = 0; index < skillKeys.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var skillKey = skillKeys[index];
            await _keyboardInputService.SendSequenceAsync(
                state.TargetWindow.Hwnd,
                skillKey,
                inputOptions,
                cancellationToken);

            await Task.Delay(stepDelay, cancellationToken);
            if (string.Equals(skillKey, "X", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!await TryDetectAutoBattleBossEnergyInsufficientAsync(
                state,
                index + 1,
                skillKey,
                cancellationToken))
            {
                continue;
            }

            await _keyboardInputService.SendSequenceAsync(
                state.TargetWindow.Hwnd,
                "X",
                inputOptions,
                cancellationToken);
            await Task.Delay(stepDelay, cancellationToken);
            _logger.LogInformation(
                "首领战斗：第 {Step}/{StepCount} 个连招技能 {SkillKey} 提示能量不足，已临时插入 X 回能，继续下一配置槽。",
                index + 1,
                AutoBattleSettings.BossComboSkillCount,
                skillKey);
        }

        await _keyboardInputService.SendSequenceAsync(
            state.TargetWindow.Hwnd,
            "Space",
            inputOptions,
            cancellationToken);
        return BossBattleComboSequence.BuildConfirmedSequence(settings.BossComboSequence);
    }

    private async Task<bool> TryDetectAutoBattleBossEnergyInsufficientAsync(
        RuntimeTaskState state,
        int step,
        string skillKey,
        CancellationToken cancellationToken)
    {
        try
        {
            using var frame = _screenCaptureService.Capture(
                state.TargetWindow,
                state.Options.CaptureMethod);
            if (frame is null)
            {
                _logger.LogDebug(
                    "首领战斗：第 {Step} 个连招技能后未能获取画面，跳过能量不足 OCR。",
                    step);
                return false;
            }

            var tipText = await RecognizeRegionTextAsync(
                state,
                frame,
                BattleTipRegionIds,
                cancellationToken,
                "首领战斗能量不足");
            var isMatch = BossBattleRecognition.IsEnergyInsufficient(tipText, out var similarity);
            LogDebugOncePerValue(
                CreateDebugLogKey("auto-battle-boss-energy-insufficient", step),
                CreateMatchFilterDebugFingerprint(tipText, similarity, isMatch),
                "首领战斗能量不足筛选：Step={Step}, SkillKey={SkillKey}, TipText={TipText}, Expected={ExpectedTipText}, Similarity={Similarity:P1}, Threshold={Threshold:P1}, IsMatch={IsMatch}",
                step,
                skillKey,
                FormatLogText(tipText),
                BossBattleRecognition.EnergyInsufficientText,
                similarity,
                BossBattleRecognition.EnergyInsufficientMatchThreshold,
                isMatch);
            return isMatch;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(
                ex,
                "首领战斗：第 {Step} 个连招技能后能量不足 OCR 失败，继续后续配置。",
                step);
            return false;
        }
    }

    private async Task<string> SendAutoBattleBossComboRecoveryAsync(
        RuntimeTaskState state,
        AutoBattleSettings settings,
        CancellationToken cancellationToken)
    {
        await _keyboardInputService.SendSequenceAsync(
            state.TargetWindow.Hwnd,
            AutoBattleBossComboRecoverySequence,
            CreateAutoBattleKeyboardInputOptions(settings),
            cancellationToken);
        return AutoBattleBossComboRecoverySequence;
    }

    private void ScheduleNextAutoBattleBossComboWatchdog(AutoBattleBossComboAttempt completedAttempt)
    {
        var nextAttempt = completedAttempt == AutoBattleBossComboAttempt.Configured
            ? AutoBattleBossComboAttempt.Recovery
            : AutoBattleBossComboAttempt.Configured;
        Volatile.Write(ref _autoBattleBossComboWatchdogAction, (int)nextAttempt);
        Volatile.Write(
            ref _autoBattleBossComboWatchdogDueAtUtcTicks,
            DateTime.UtcNow.Add(AutoBattleBossComboWatchdogInterval).Ticks);
        Volatile.Write(
            ref _autoBattleBossComboActionState,
            (int)AutoBattleBossComboActionState.AwaitingBattleExit);
    }

    private static bool HasEnabledRecognitionRegion(
        RuntimeTaskState state,
        IReadOnlyList<string> regionAliases)
    {
        return state.RecognitionRegionConfig.Regions.Any(region =>
            region.Enabled
            && !string.IsNullOrWhiteSpace(region.Id)
            && regionAliases.Any(alias => string.Equals(
                region.Id.Trim(),
                alias,
                StringComparison.OrdinalIgnoreCase)));
    }

    private void ResetAutoBattleBossBattleState()
    {
        _autoBattleType = AutoBattleType.Unknown;
        Volatile.Write(ref _autoBattleBossComboWatchdogDueAtUtcTicks, 0);
        Volatile.Write(
            ref _autoBattleBossComboActiveAttempt,
            (int)AutoBattleBossComboAttempt.Configured);
        Volatile.Write(
            ref _autoBattleBossComboWatchdogAction,
            (int)AutoBattleBossComboAttempt.Recovery);
        Volatile.Write(
            ref _autoBattleBossComboActionState,
            (int)AutoBattleBossComboActionState.Ready);
        ResetAutoBattleBossSkillSelectionState();
    }

    private void ResetAutoBattleBossSkillSelectionState()
    {
        _autoBattleBossComboPromptTask = null;
        _autoBattleBossComboPromptTaskTurnNumber = 0;
        _hasAutoBattleBossComboPromptResultForCurrentTurn = false;
        _isAutoBattleBossComboPromptMatchedForCurrentTurn = false;
    }

    private enum AutoBattleType
    {
        Unknown,
        Normal,
        Boss
    }

    private enum AutoBattleBossComboActionState
    {
        Ready,
        Executing,
        AwaitingBattleExit
    }

    private enum AutoBattleBossComboAttempt
    {
        Configured,
        Recovery
    }

    private sealed record AutoBattleBossComboPromptResult(
        string Text,
        double Similarity,
        bool IsMatch);
}
