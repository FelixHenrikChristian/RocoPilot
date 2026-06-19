using Microsoft.Extensions.Logging;

using RocoPilot.Configuration;
using RocoPilot.Models.Capture;
using RocoPilot.Models.Encounters;
using RocoPilot.Models.ImageMatching;
using RocoPilot.Models.Runtime;

namespace RocoPilot.Services;

public sealed partial class RuntimeTaskService
{
    private const string BattleChatTemplateName = "battle-chat.png";
    private const string BattleSkillTemplateName = "battle-button-skill.png";
    private const string BattleChangeTemplateName = "battle-button-change.png";
    private const string AutoBattleCaptureSequence = "W, 1, Space";
    private const string AutoBattleSkillPlaceholder = "{skill}";

    private static readonly TimeSpan AutoBattleSkillReleaseFailureCheckDelay = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan AutoBattleEncounterRelieveScanInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan AutoBattleShinySuspendScanInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan AutoBattlePetSwitchConfirmDelay = TimeSpan.FromMilliseconds(1500);
    private static readonly TimeSpan AutoBattlePetSwitchStateCheckDelay = TimeSpan.FromMilliseconds(1500);
    private static readonly string[] AutoBattleDefaultRoundOrder =
    [
        "1",
        "2",
        "3",
        "4",
        "X"
    ];
    private static readonly string[] BattleChatRegionIds =
    [
        "battle-button-chat"
    ];
    private static readonly string[] BattleSkillRegionIds =
    [
        "battle-button-skill"
    ];
    private static readonly string[] BattleChangeRegionIds =
    [
        "battle-button-change"
    ];
    private static readonly ImageMatchOptions BattleChatMatchOptions = new()
    {
        MinimumScore = 0.88,
        AlphaThreshold = 16,
        SearchStep = 1
    };
    private static readonly ImageMatchOptions BattleSkillMatchOptions = new()
    {
        MinimumScore = 0.88,
        AlphaThreshold = 16,
        SearchStep = 1
    };
    private static readonly ImageMatchOptions BattleChangeMatchOptions = new()
    {
        MinimumScore = 0.88,
        AlphaThreshold = 16,
        SearchStep = 1
    };

    private readonly SemaphoreSlim _autoBattleActionLock = new(1, 1);
    private AutoBattleSettings _autoBattleSettings = AutoBattleSettings.CreateDefault();
    private int _autoBattleRoundIndex;
    private int _autoBattleTurnNumber;
    private int _currentAutoBattleTurnNumber;
    private bool _wasAutoBattleSkillSelectionVisible;
    private bool _wasAutoBattlePetSwitchingVisible;
    private bool _hasLoggedCurrentAutoBattleTurnAction;
    private DateTimeOffset? _autoBattleSkillSelectionVisibleSince;
    private DateTimeOffset? _lastAutoBattleSkillSelectionActionAt;
    private AutoBattleReleaseStep? _currentAutoBattleReleaseStep;
    private AutoBattleSkillSelectionAction _autoBattleSkillSelectionAction;
    private Task<AutoBattleSkillSelectionEnemyNameResult>? _autoBattleSkillSelectionEnemyNameTask;
    private int _autoBattleSkillSelectionEnemyNameTaskTurnNumber;
    private bool _hasAutoBattleSkillSelectionEnemyNameResult;
    private bool _hasQueuedAutoBattleSkillFailureTipRecognitionForCurrentAction;
    private bool _shouldDelayAutoBattleSkillSelectionForLegacyTipEncounter;
    private bool _isAutoBattleEncounterRelieved;
    private DateTimeOffset _nextAutoBattleEncounterRelieveScanAt = DateTimeOffset.MinValue;
    private bool _isAutoBattleSuspendedForShiny;
    private DateTimeOffset _nextAutoBattleShinySuspendScanAt = DateTimeOffset.MinValue;

    public AutoBattleSettings AutoBattleSettings => _autoBattleSettings.Clone();

    public void SetAutoBattleSettings(AutoBattleSettings settings)
    {
        var previousIsEnabled = _autoBattleSettings.IsEnabled;
        _autoBattleSettings = NormalizeAutoBattleSettings(settings);
        if (!RequiresAutoBattleEncounterRelieveDetection(_autoBattleSettings.EncounterRelievedAction))
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
        RuntimeTaskState state,
        CapturedFrame frame,
        CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.Now;
        var settings = NormalizeAutoBattleSettings(_autoBattleSettings);

        if (!_wasAutoBattleSkillSelectionVisible)
        {
            BeginAutoBattleSkillSelectionTurn(settings, now);
            return;
        }

        if (!await EnsureAutoBattleSkillSelectionEnemyNameResultAsync(
            state,
            frame,
            settings,
            now,
            cancellationToken))
        {
            return;
        }

        if (_isAutoBattleSuspendedForShiny || !settings.IsEnabled)
        {
            return;
        }

        if (!ShouldRunAutoBattleSkillSelectionAction(settings, now))
        {
            return;
        }

        if (!await _autoBattleActionLock.WaitAsync(0, cancellationToken))
        {
            return;
        }

        try
        {
            if (_isAutoBattleSuspendedForShiny)
            {
                return;
            }

            if (!_keyboardInputService.IsWindowAvailable(state.TargetWindow.Hwnd))
            {
                _logger.LogWarning("自动战斗未执行：目标游戏窗口句柄已失效。");
                return;
            }

            var releaseStep = _currentAutoBattleReleaseStep ?? GetCurrentAutoBattleReleaseStep(settings);
            var plan = BuildAutoBattleSkillSelectionPlan(settings, releaseStep);
            if (!plan.ShouldSendKeys)
            {
                if (_autoBattleSkillSelectionAction != plan.Action)
                {
                    LogAutoBattleTurnAction(plan.Description);
                }

                _autoBattleSkillSelectionAction = plan.Action;
                _lastAutoBattleSkillSelectionActionAt = DateTimeOffset.Now;
                return;
            }

            if (!_keyboardInputService.TryParseSequence(plan.Sequence, out var keyStrokes, out var parseError)
                || keyStrokes.Count == 0)
            {
                _logger.LogWarning(
                    "自动战斗单回合序列无效。ReleaseStep={ReleaseStep}, Sequence={Sequence}, Error={Error}",
                    GetAutoBattleReleaseStepDisplay(releaseStep),
                    plan.Sequence,
                    parseError);
                keyStrokes = plan.Action == AutoBattleSkillSelectionAction.Skill
                    && !releaseStep.IsCustom
                    && _keyboardInputService.TryParseSequence(releaseStep.SkillKey, out var fallbackStrokes, out _)
                    ? fallbackStrokes
                    : [];
            }

            if (keyStrokes.Count == 0)
            {
                return;
            }

            if (ShouldSkipAutoBattleKeyboardInput(state, settings))
            {
                return;
            }

            await _keyboardInputService.SendSequenceAsync(
                state.TargetWindow.Hwnd,
                keyStrokes,
                plan.InputOptions,
                cancellationToken);

            _autoBattleSkillSelectionAction = plan.Action;
            _lastAutoBattleSkillSelectionActionAt = DateTimeOffset.Now;

            LogAutoBattleTurnAction(plan.Description, plan.Sequence);
            _logger.LogDebug(
                "自动战斗按键已发送：ReleaseStep={ReleaseStep}, Sequence={Sequence}, Action={Action}, KeyStrokeCount={KeyStrokeCount}, RoundIndex={RoundIndex}",
                plan.DisplayKey,
                plan.Sequence,
                _autoBattleSkillSelectionAction,
                keyStrokes.Count,
                _autoBattleRoundIndex);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "自动战斗技能释放失败。");
        }
        finally
        {
            _autoBattleActionLock.Release();
        }
    }

    private async Task HandleAutoBattlePetSwitchingAsync(
        RuntimeTaskState state,
        CancellationToken cancellationToken)
    {
        var settings = NormalizeAutoBattleSettings(_autoBattleSettings);
        if (!settings.IsEnabled)
        {
            _wasAutoBattlePetSwitchingVisible = true;
            return;
        }

        if (_isAutoBattleSuspendedForShiny)
        {
            _wasAutoBattlePetSwitchingVisible = true;
            return;
        }

        if (_wasAutoBattlePetSwitchingVisible)
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
            _wasAutoBattlePetSwitchingVisible = true;

            var keyboardInputOptions = CreateAutoBattleKeyboardInputOptions(settings);
            for (var slot = 1; slot <= 6; slot++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (ShouldSkipAutoBattleKeyboardInput(state, settings))
                {
                    _wasAutoBattlePetSwitchingVisible = false;
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
                    _wasAutoBattlePetSwitchingVisible = false;
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

                if (await IsBattlePetSwitchingAsync(state, frame, cancellationToken))
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
        _autoBattleTurnNumber++;
        _currentAutoBattleTurnNumber = _autoBattleTurnNumber;
        _hasLoggedCurrentAutoBattleTurnAction = false;
        _wasAutoBattleSkillSelectionVisible = true;
        _autoBattleSkillSelectionVisibleSince = now;
        _lastAutoBattleSkillSelectionActionAt = null;
        _currentAutoBattleReleaseStep = GetCurrentAutoBattleReleaseStep(settings);
        _autoBattleSkillSelectionAction = AutoBattleSkillSelectionAction.None;
        _logger.LogDebug(
            "自动战斗：进入第 {TurnNumber} 回合技能选择，等待 {DelayMs}ms 后执行。ReleaseStep={ReleaseStep}, RoundIndex={RoundIndex}",
            _currentAutoBattleTurnNumber,
            settings.SkillSelectionActionDelayMs,
            GetAutoBattleReleaseStepDisplay(_currentAutoBattleReleaseStep),
            _autoBattleRoundIndex);
    }

    private async Task<bool> EnsureAutoBattleSkillSelectionEnemyNameResultAsync(
        RuntimeTaskState state,
        CapturedFrame frame,
        AutoBattleSettings settings,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (_hasAutoBattleSkillSelectionEnemyNameResult)
        {
            return true;
        }

        if (!_autoBattleSkillSelectionVisibleSince.HasValue)
        {
            _autoBattleSkillSelectionVisibleSince = now;
            return false;
        }

        if (now - _autoBattleSkillSelectionVisibleSince.Value
            < TimeSpan.FromMilliseconds(settings.SkillSelectionActionDelayMs))
        {
            return false;
        }

        if (_autoBattleSkillSelectionEnemyNameTask is null
            || _autoBattleSkillSelectionEnemyNameTaskTurnNumber != _currentAutoBattleTurnNumber)
        {
            _autoBattleSkillSelectionEnemyNameTask = StartAutoBattleSkillSelectionEnemyNameRecognitionAsync(
                state,
                frame,
                cancellationToken);
            _autoBattleSkillSelectionEnemyNameTaskTurnNumber = _currentAutoBattleTurnNumber;
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

        var season = _encounterSeasonConfigService.GetCurrentSeason();
        if (season is null)
        {
            _hasAutoBattleSkillSelectionEnemyNameResult = true;
            return true;
        }

        if (IsEncounterPlaceholderName(result.RawText, season, out var placeholderSimilarity))
        {
            if (_currentAutoBattleTurnNumber == 1)
            {
                _shouldDelayAutoBattleSkillSelectionForLegacyTipEncounter = false;
            }

            if (MarkEncounterPlaceholderNameSeen(season.Id))
            {
                _logger.LogInformation(
                    "自动战斗：技能选择阶段识别到 {PlaceholderName}，本回合按普通自动战斗流程执行。",
                    GetEncounterPlaceholderName(season));
            }

            LogDebugOncePerValue(
                CreateDebugLogKey("auto-battle-skill-selection-placeholder", season.Id),
                CreateSimilarityDebugFingerprint(placeholderSimilarity),
                "自动战斗技能选择精灵名筛选：EnemyNameRaw={EnemyNameRaw}, Placeholder={PlaceholderName}, Similarity={Similarity:P1}, Threshold={Threshold:P1}, IsPlaceholder=True",
                FormatLogText(result.RawText),
                GetEncounterPlaceholderName(season),
                placeholderSimilarity,
                season.PlaceholderMatchThreshold);
            _hasAutoBattleSkillSelectionEnemyNameResult = true;
            return true;
        }

        if (string.IsNullOrWhiteSpace(result.MatchedName))
        {
            LogDebugOncePerValue(
                CreateDebugLogKey("auto-battle-skill-selection-enemy-missing", season.Id),
                CreateTextDebugFingerprint(result.RawText),
                "自动战斗技能选择精灵名筛选：EnemyNameRaw={EnemyNameRaw}, 未匹配到有效精灵名，等待下一轮 OCR。",
                FormatLogText(result.RawText));
            ResetAutoBattleSkillSelectionEnemyNameTask();
            return false;
        }

        UpdateAutoBattleLegacyTipEncounterSkillDelayState(season, result);

        await ApplyAutoBattleSkillSelectionEnemyNameResultAsync(
            season,
            result,
            cancellationToken);
        _hasAutoBattleSkillSelectionEnemyNameResult = true;
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
            return Task.FromResult(new AutoBattleSkillSelectionEnemyNameResult(string.Empty, string.Empty));
        }

        return Task.Run(
            async () =>
            {
                using (frameReference)
                {
                    var rawText = await RecognizeRegionTextAsync(
                        state,
                        frameReference,
                        BattleEnemyNameRegionIds,
                        cancellationToken,
                        "自动战斗技能选择");
                    var season = _encounterSeasonConfigService.GetCurrentSeason();
                    var matchedName = season is null
                        ? string.Empty
                        : await MatchRecognizedSpiritNameAsync(rawText, season, cancellationToken);
                    return new AutoBattleSkillSelectionEnemyNameResult(rawText, matchedName);
                }
            },
            cancellationToken);
    }

    private async Task ApplyAutoBattleSkillSelectionEnemyNameResultAsync(
        EncounterSeasonDefinition season,
        AutoBattleSkillSelectionEnemyNameResult result,
        CancellationToken cancellationToken)
    {
        var matchedName = result.MatchedName;
        LogDebugOncePerValue(
            CreateDebugLogKey("auto-battle-skill-selection-enemy", season.Id),
            string.Join(
                "|",
                CreateTextDebugFingerprint(result.RawText),
                CreateTextDebugFingerprint(matchedName)),
            "自动战斗技能选择精灵名筛选：EnemyNameRaw={EnemyNameRaw}, Matched={SpiritName}, MatchThreshold={MatchThreshold:P1}",
            FormatLogText(result.RawText),
            matchedName,
            season.SpiritNameMatchThreshold);

        if (TryGetPendingShinyDetection(season.Id, out _))
        {
            await RecordPendingShinyCaptureAsync(season, matchedName, cancellationToken);
            ClearPendingShinyDetection();
            return;
        }

        if (TryGetPendingEncounterDetection(
            season.Id,
            out var pendingDetectionMode,
            out var pendingDetectionSimilarity))
        {
            await RecordEncounterAsync(
                season,
                matchedName,
                DateTimeOffset.Now,
                pendingDetectionMode,
                pendingDetectionSimilarity,
                cancellationToken);
            ClearPendingEncounterDetection();
        }

        if (!UsesEnemyNameTransitionDetection(season))
        {
            return;
        }

        var hasSeenPlaceholderName = HasSeenEncounterPlaceholderName(season.Id);
        LogDebugOncePerValue(
            CreateDebugLogKey("auto-battle-skill-selection-transition", season.Id),
            string.Join(
                "|",
                CreateTextDebugFingerprint(result.RawText),
                CreateTextDebugFingerprint(matchedName),
                CreateBooleanDebugFingerprint(hasSeenPlaceholderName)),
            "自动战斗技能选择名称变化筛选：EnemyNameRaw={EnemyNameRaw}, Matched={SpiritName}, HasSeenPlaceholder={HasSeenPlaceholder}",
            FormatLogText(result.RawText),
            matchedName,
            hasSeenPlaceholderName);
        if (!hasSeenPlaceholderName)
        {
            return;
        }

        ApplyAutoBattleEncounterRelievedDetection("技能选择精灵名");
        await RecordEncounterAsync(
            season,
            matchedName,
            DateTimeOffset.Now,
            season.DetectionMode,
            1,
            cancellationToken);
    }

    private void UpdateAutoBattleLegacyTipEncounterSkillDelayState(
        EncounterSeasonDefinition season,
        AutoBattleSkillSelectionEnemyNameResult result)
    {
        if (_currentAutoBattleTurnNumber != 1
            || _shouldDelayAutoBattleSkillSelectionForLegacyTipEncounter
            || !UsesEnemyNameTransitionDetection(season)
            || string.IsNullOrWhiteSpace(season.TipText))
        {
            return;
        }

        _shouldDelayAutoBattleSkillSelectionForLegacyTipEncounter = true;
    }

    private void ResetAutoBattleSkillSelectionEnemyNameTask()
    {
        _autoBattleSkillSelectionEnemyNameTask = null;
        _autoBattleSkillSelectionEnemyNameTaskTurnNumber = 0;
    }

    private void BeginAutoBattlePetSwitchingTurn(AutoBattleSettings settings)
    {
        var releaseStep = GetCurrentAutoBattleReleaseStep(settings);
        _autoBattleTurnNumber++;
        _currentAutoBattleTurnNumber = _autoBattleTurnNumber;
        _hasLoggedCurrentAutoBattleTurnAction = false;
        _autoBattleSkillSelectionAction = AutoBattleSkillSelectionAction.None;

        LogAutoBattleTurnAction(
            $"切换精灵，本回合不释放技能，下回合继续 {GetAutoBattleReleaseStepDisplay(releaseStep)}");
        _logger.LogDebug(
            "自动战斗：进入第 {TurnNumber} 回合切换精灵，不推进释放顺序。ReleaseStep={ReleaseStep}, RoundIndex={RoundIndex}",
            _currentAutoBattleTurnNumber,
            GetAutoBattleReleaseStepDisplay(releaseStep),
            _autoBattleRoundIndex);
    }

    private async Task<bool> TryHandleAutoBattleSkillReleaseFailureAsync(
        RuntimeTaskState state,
        CapturedFrame frame,
        CancellationToken cancellationToken)
    {
        var settings = NormalizeAutoBattleSettings(_autoBattleSettings);
        if (!settings.IsEnabled
            || _isAutoBattleSuspendedForShiny
            || _autoBattleSkillSelectionAction != AutoBattleSkillSelectionAction.Skill)
        {
            return false;
        }

        if (!_lastAutoBattleSkillSelectionActionAt.HasValue)
        {
            return false;
        }

        var now = DateTimeOffset.Now;
        if (now - _lastAutoBattleSkillSelectionActionAt.Value < AutoBattleSkillReleaseFailureCheckDelay)
        {
            return false;
        }

        QueueAutoBattleSkillFailureTipRecognition(state, frame, _currentAutoBattleTurnNumber, cancellationToken);

        if (!await TrySendAutoBattleEnergyRecoveryAsync(state, cancellationToken))
        {
            return false;
        }

        _autoBattleSkillSelectionAction = AutoBattleSkillSelectionAction.EnergyRecovery;

        LogAutoBattleTurnAction(
            "技能未离开选择界面，临时回能 X，原技能延后",
            "X",
            forceInformation: true);
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

        _ = Task.Run(
            async () =>
            {
                using (frameReference)
                {
                    try
                    {
                        var tipText = await RecognizeRegionTextAsync(
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
    }

    private bool ShouldSkipAutoBattleKeyboardInput(RuntimeTaskState state, AutoBattleSettings settings)
    {
        if (!settings.IsEnabled
            || !_keyboardInputService.RequiresForeground(settings.KeyboardInputMethod))
        {
            return false;
        }

        if (!_keyboardInputService.IsWindowAvailable(state.TargetWindow.Hwnd))
        {
            return false;
        }

        if (_keyboardInputService.IsWindowForeground(state.TargetWindow.Hwnd))
        {
            return false;
        }

        _logger.LogDebug(
            "自动战斗按键未发送：{InputMethod} 需要游戏窗口处于前台。",
            settings.KeyboardInputMethod);
        return true;
    }

    private async Task<bool> TrySendAutoBattleEnergyRecoveryAsync(
        RuntimeTaskState state,
        CancellationToken cancellationToken)
    {
        if (_isAutoBattleSuspendedForShiny)
        {
            return false;
        }

        if (!await _autoBattleActionLock.WaitAsync(0, cancellationToken))
        {
            return false;
        }

        try
        {
            if (!_keyboardInputService.IsWindowAvailable(state.TargetWindow.Hwnd))
            {
                _logger.LogWarning("自动战斗回能未执行：目标游戏窗口句柄已失效。");
                return false;
            }

            var settings = NormalizeAutoBattleSettings(_autoBattleSettings);
            if (ShouldSkipAutoBattleKeyboardInput(state, settings))
            {
                return false;
            }

            await _keyboardInputService.SendSequenceAsync(
                state.TargetWindow.Hwnd,
                "X",
                CreateAutoBattleKeyboardInputOptions(settings),
                cancellationToken);
            _lastAutoBattleSkillSelectionActionAt = DateTimeOffset.Now;
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "自动战斗回能失败。");
            return false;
        }
        finally
        {
            _autoBattleActionLock.Release();
        }
    }

    private void LogAutoBattleTurnAction(
        string description,
        string? sequence = null,
        bool forceInformation = false)
    {
        var turnNumber = _currentAutoBattleTurnNumber > 0
            ? _currentAutoBattleTurnNumber
            : _autoBattleTurnNumber;
        if (turnNumber <= 0)
        {
            turnNumber = 1;
        }

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
        if (ShouldAdvanceAutoBattleReleaseSequence(_autoBattleSkillSelectionAction))
        {
            _autoBattleRoundIndex++;
        }

        ResetAutoBattleSkillSelectionState();
    }

    private static bool ShouldAdvanceAutoBattleReleaseSequence(AutoBattleSkillSelectionAction action)
    {
        return action == AutoBattleSkillSelectionAction.Skill;
    }

    private bool ShouldRunAutoBattleSkillSelectionAction(
        AutoBattleSettings settings,
        DateTimeOffset now)
    {
        if (!_autoBattleSkillSelectionVisibleSince.HasValue)
        {
            _autoBattleSkillSelectionVisibleSince = now;
            return false;
        }

        var actionDelayMs = settings.SkillSelectionActionDelayMs;
        if (_shouldDelayAutoBattleSkillSelectionForLegacyTipEncounter)
        {
            actionDelayMs += settings.LegacyTipEncounterExtraDelayMs;
        }

        var actionDelay = TimeSpan.FromMilliseconds(actionDelayMs);
        if (now - _autoBattleSkillSelectionVisibleSince.Value < actionDelay)
        {
            return false;
        }

        if (!_lastAutoBattleSkillSelectionActionAt.HasValue)
        {
            return true;
        }

        return now - _lastAutoBattleSkillSelectionActionAt.Value
            >= TimeSpan.FromMilliseconds(settings.SkillSelectionRetryDelayMs);
    }

    private void ResetAutoBattleBattleState()
    {
        _autoBattleRoundIndex = 0;
        _autoBattleTurnNumber = 0;
        ResetAutoBattleSkillSelectionState();
        ResetAutoBattleEncounterRelievedActionState();
        ResetAutoBattleShinySuspendState();
        _wasAutoBattlePetSwitchingVisible = false;
        _shouldDelayAutoBattleSkillSelectionForLegacyTipEncounter = false;
    }

    private void ResetAutoBattleEncounterRelievedActionState()
    {
        _isAutoBattleEncounterRelieved = false;
        _nextAutoBattleEncounterRelieveScanAt = DateTimeOffset.MinValue;
    }

    private void ResetAutoBattleShinySuspendState()
    {
        _isAutoBattleSuspendedForShiny = false;
        _nextAutoBattleShinySuspendScanAt = DateTimeOffset.MinValue;
    }

    private void ResetAutoBattleSkillSelectionState()
    {
        _wasAutoBattleSkillSelectionVisible = false;
        _autoBattleSkillSelectionVisibleSince = null;
        _lastAutoBattleSkillSelectionActionAt = null;
        _currentAutoBattleReleaseStep = null;
        _currentAutoBattleTurnNumber = 0;
        _hasLoggedCurrentAutoBattleTurnAction = false;
        _autoBattleSkillSelectionAction = AutoBattleSkillSelectionAction.None;
        ResetAutoBattleSkillSelectionEnemyNameTask();
        _hasAutoBattleSkillSelectionEnemyNameResult = false;
        _hasQueuedAutoBattleSkillFailureTipRecognitionForCurrentAction = false;
    }

    private sealed record AutoBattleSkillSelectionEnemyNameResult(
        string RawText,
        string MatchedName);
}
