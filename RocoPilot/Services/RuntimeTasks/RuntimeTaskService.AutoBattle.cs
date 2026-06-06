using Microsoft.Extensions.Logging;

using RocoPilot.Configuration;
using RocoPilot.Helpers;
using RocoPilot.Models.Capture;
using RocoPilot.Models.ImageMatching;
using RocoPilot.Models.Input;
using RocoPilot.Models.Runtime;

namespace RocoPilot.Services;

public sealed partial class RuntimeTaskService
{
    private const string BattleChatTemplateName = "battle-chat.png";
    private const string BattleSkillTemplateName = "battle-button-skill.png";
    private const string BattleChangeTemplateName = "battle-button-change.png";
    private const string AutoBattleCaptureSequence = "W, 1, Space";
    private const string AutoBattleSkillPlaceholder = "{skill}";

    private static readonly TimeSpan AutoBattleSkillSelectionActionDelay = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan AutoBattleSkillReleaseFailureCheckDelay = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan AutoBattleSkillSelectionRetryDelay = TimeSpan.FromSeconds(4);
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
    private const int AutoBattleKeyboardHoldDurationMs = 100;
    private const int AutoBattleKeyboardIntervalMs = 120;
    private const int AutoBattleCaptureKeyboardIntervalMs = 500;

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
    private bool _isAutoBattleEncounterRelieved;
    private DateTimeOffset _nextAutoBattleEncounterRelieveScanAt = DateTimeOffset.MinValue;
    private bool _isAutoBattleSuspendedForShiny;
    private DateTimeOffset _nextAutoBattleShinySuspendScanAt = DateTimeOffset.MinValue;

    public AutoBattleSettings AutoBattleSettings => _autoBattleSettings.Clone();

    public void SetAutoBattleSettings(AutoBattleSettings settings)
    {
        var previousIsEnabled = _autoBattleSettings.IsEnabled;
        _autoBattleSettings = NormalizeAutoBattleSettings(settings);
        if (!_autoBattleSettings.IsEnabled)
        {
            ResetAutoBattleBattleState();
        }
        else if (!RequiresAutoBattleEncounterRelieveDetection(_autoBattleSettings.EncounterRelievedAction))
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

    private async Task HandleAutoBattleSkillSelectionAsync(
        RuntimeTaskState state,
        CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.Now;
        if (!_autoBattleSettings.IsEnabled)
        {
            _wasAutoBattleSkillSelectionVisible = true;
            return;
        }

        var settings = NormalizeAutoBattleSettings(_autoBattleSettings);
        if (!settings.IsEnabled)
        {
            return;
        }

        if (_isAutoBattleSuspendedForShiny)
        {
            _wasAutoBattleSkillSelectionVisible = true;
            return;
        }

        if (!_wasAutoBattleSkillSelectionVisible)
        {
            BeginAutoBattleSkillSelectionTurn(settings, now);
            return;
        }

        if (!ShouldRunAutoBattleSkillSelectionAction(now))
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
            AutoBattleSkillSelectionActionDelay.TotalMilliseconds,
            GetAutoBattleReleaseStepDisplay(_currentAutoBattleReleaseStep),
            _autoBattleRoundIndex);
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

        if (!await TrySendAutoBattleEnergyRecoveryAsync(state, cancellationToken))
        {
            return false;
        }

        _autoBattleSkillSelectionAction = AutoBattleSkillSelectionAction.EnergyRecovery;
        var tipText = await RecognizeRegionTextAsync(
            state,
            frame,
            BattleTipRegionIds,
            cancellationToken,
            "自动战斗技能失败");
        var failureReason = string.IsNullOrWhiteSpace(tipText)
            ? "未识别到提示"
            : FormatLogText(tipText);

        LogAutoBattleTurnAction(
            $"技能未释放成功，原因：{failureReason}，临时回能 X，原技能延后",
            "X",
            forceInformation: true);
        return true;
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

    private AutoBattleReleaseStep GetCurrentAutoBattleReleaseStep(AutoBattleSettings settings)
    {
        var releaseSequence = NormalizeAutoBattleReleaseSequence(settings);
        if (_autoBattleRoundIndex >= releaseSequence.Count)
        {
            _autoBattleRoundIndex = 0;
        }

        return releaseSequence[_autoBattleRoundIndex].Clone();
    }

    private static IReadOnlyList<string> ParseAutoBattleRoundOrder(string roundOrder)
    {
        if (string.IsNullOrWhiteSpace(roundOrder))
        {
            return AutoBattleDefaultRoundOrder;
        }

        var keys = roundOrder
            .Replace("\r", "\n", StringComparison.Ordinal)
            .Replace(';', ',')
            .Split([',', '\n', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(key => key.ToUpperInvariant())
            .Where(key => key is "1" or "2" or "3" or "4" or "X")
            .ToArray();
        return keys.Length == 0 ? AutoBattleDefaultRoundOrder : keys;
    }

    private static string BuildAutoBattleTurnSequence(AutoBattleSettings settings, string skillKey)
    {
        var turnSequence = string.IsNullOrWhiteSpace(settings.TurnSequence)
            ? AutoBattleSettings.DefaultTurnSequence
            : settings.TurnSequence.Trim();

        return turnSequence.Contains(AutoBattleSkillPlaceholder, StringComparison.OrdinalIgnoreCase)
            ? turnSequence.Replace(AutoBattleSkillPlaceholder, skillKey, StringComparison.OrdinalIgnoreCase)
            : turnSequence;
    }

    private static string BuildAutoBattleReleaseSequence(AutoBattleSettings settings, AutoBattleReleaseStep releaseStep)
    {
        if (releaseStep.IsCustom)
        {
            return releaseStep.Sequence.Trim();
        }

        return BuildAutoBattleTurnSequence(settings, releaseStep.SkillKey);
    }

    private AutoBattleSkillSelectionPlan BuildAutoBattleSkillSelectionPlan(
        AutoBattleSettings settings,
        AutoBattleReleaseStep releaseStep)
    {
        if (_isAutoBattleEncounterRelieved
            && RequiresAutoBattleEncounterRelieveDetection(settings.EncounterRelievedAction))
        {
            return settings.EncounterRelievedAction switch
            {
                AutoBattleEncounterRelievedAction.NoAction => new AutoBattleSkillSelectionPlan(
                    AutoBattleSkillSelectionAction.NoAction,
                    ShouldSendKeys: false,
                    Sequence: string.Empty,
                    InputOptions: CreateAutoBattleKeyboardInputOptions(settings),
                    Description: "无操作，检测到奇遇效果解除，等待手动释放技能",
                    DisplayKey: "-"),
                AutoBattleEncounterRelievedAction.RecoverEnergy => new AutoBattleSkillSelectionPlan(
                    AutoBattleSkillSelectionAction.EnergyRecovery,
                    ShouldSendKeys: true,
                    Sequence: "X",
                    InputOptions: CreateAutoBattleKeyboardInputOptions(settings),
                    Description: "奇遇解除后回能 X",
                    DisplayKey: "X"),
                AutoBattleEncounterRelievedAction.Capture => new AutoBattleSkillSelectionPlan(
                    AutoBattleSkillSelectionAction.Capture,
                    ShouldSendKeys: true,
                    Sequence: AutoBattleCaptureSequence,
                    InputOptions: CreateAutoBattleCaptureKeyboardInputOptions(settings),
                    Description: "奇遇解除后捕捉 W, 1, Space",
                    DisplayKey: "W, 1, Space"),
                _ => BuildAutoBattleReleaseSkillPlan(settings, releaseStep)
            };
        }

        return BuildAutoBattleReleaseSkillPlan(settings, releaseStep);
    }

    private static AutoBattleSkillSelectionPlan BuildAutoBattleReleaseSkillPlan(
        AutoBattleSettings settings,
        AutoBattleReleaseStep releaseStep)
    {
        var sequence = BuildAutoBattleReleaseSequence(settings, releaseStep);
        var displayKey = GetAutoBattleReleaseStepDisplay(releaseStep);
        return new AutoBattleSkillSelectionPlan(
            AutoBattleSkillSelectionAction.Skill,
            ShouldSendKeys: true,
            sequence,
            CreateAutoBattleKeyboardInputOptions(settings),
            releaseStep.IsCustom
                ? $"执行自定义序列 {displayKey}"
                : $"释放技能 {displayKey}",
            displayKey);
    }

    private static string GetAutoBattleReleaseStepDisplay(AutoBattleReleaseStep? releaseStep)
    {
        if (releaseStep is null)
        {
            return "-";
        }

        if (!releaseStep.IsCustom)
        {
            return releaseStep.SkillKey;
        }

        return string.IsNullOrWhiteSpace(releaseStep.Name)
            ? releaseStep.Sequence
            : releaseStep.Name;
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

    private bool ShouldRunAutoBattleSkillSelectionAction(DateTimeOffset now)
    {
        if (!_autoBattleSkillSelectionVisibleSince.HasValue)
        {
            _autoBattleSkillSelectionVisibleSince = now;
            return false;
        }

        if (now - _autoBattleSkillSelectionVisibleSince.Value < AutoBattleSkillSelectionActionDelay)
        {
            return false;
        }

        if (!_lastAutoBattleSkillSelectionActionAt.HasValue)
        {
            return true;
        }

        return now - _lastAutoBattleSkillSelectionActionAt.Value >= AutoBattleSkillSelectionRetryDelay;
    }

    private void ResetAutoBattleBattleState()
    {
        _autoBattleRoundIndex = 0;
        _autoBattleTurnNumber = 0;
        ResetAutoBattleSkillSelectionState();
        ResetAutoBattleEncounterRelievedActionState();
        ResetAutoBattleShinySuspendState();
        _wasAutoBattlePetSwitchingVisible = false;
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
    }

    private static AutoBattleSettings NormalizeAutoBattleSettings(AutoBattleSettings? settings)
    {
        var normalized = settings?.Clone() ?? AutoBattleSettings.CreateDefault();
        if (string.IsNullOrWhiteSpace(normalized.RoundOrder))
        {
            normalized.RoundOrder = AutoBattleSettings.DefaultRoundOrder;
        }

        if (string.IsNullOrWhiteSpace(normalized.TurnSequence))
        {
            normalized.TurnSequence = AutoBattleSettings.DefaultTurnSequence;
        }

        normalized.ReleaseSequence = NormalizeAutoBattleReleaseSequence(normalized)
            .Select(step => step.Clone())
            .ToList();
        normalized.TurnSequencePresets = NormalizeAutoBattleTurnSequencePresets(normalized.TurnSequencePresets);
        if (!Enum.IsDefined(normalized.EncounterRelievedAction))
        {
            normalized.EncounterRelievedAction = AutoBattleEncounterRelievedAction.RecoverEnergy;
        }

        if (!Enum.IsDefined(normalized.KeyboardInputMethod))
        {
            normalized.KeyboardInputMethod = KeyboardInputMethod.PostMessage;
        }

        return normalized;
    }

    private static KeyboardInputOptions CreateAutoBattleKeyboardInputOptions(AutoBattleSettings settings)
    {
        return new KeyboardInputOptions
        {
            Method = settings.KeyboardInputMethod,
            HoldDurationMs = AutoBattleKeyboardHoldDurationMs,
            IntervalMs = AutoBattleKeyboardIntervalMs
        };
    }

    private static KeyboardInputOptions CreateAutoBattleCaptureKeyboardInputOptions(AutoBattleSettings settings)
    {
        return new KeyboardInputOptions
        {
            Method = settings.KeyboardInputMethod,
            HoldDurationMs = AutoBattleKeyboardHoldDurationMs,
            IntervalMs = AutoBattleCaptureKeyboardIntervalMs
        };
    }

    private static IReadOnlyList<AutoBattleReleaseStep> NormalizeAutoBattleReleaseSequence(AutoBattleSettings settings)
    {
        var releaseSequence = (settings.ReleaseSequence ?? [])
            .Select(NormalizeAutoBattleReleaseStep)
            .OfType<AutoBattleReleaseStep>()
            .ToArray();

        if (releaseSequence.Length > 0
            && (!IsDefaultAutoBattleReleaseSequence(releaseSequence)
                || IsDefaultAutoBattleRoundOrder(settings.RoundOrder)))
        {
            return releaseSequence;
        }

        return ParseAutoBattleRoundOrder(settings.RoundOrder)
            .Select(AutoBattleReleaseStep.CreateSkill)
            .ToArray();
    }

    private static bool IsDefaultAutoBattleReleaseSequence(IReadOnlyList<AutoBattleReleaseStep> releaseSequence)
    {
        return releaseSequence.Count == AutoBattleDefaultRoundOrder.Length
            && releaseSequence
                .Select(step => step.IsCustom ? string.Empty : step.SkillKey)
                .SequenceEqual(AutoBattleDefaultRoundOrder, StringComparer.OrdinalIgnoreCase);
    }

    private static bool IsDefaultAutoBattleRoundOrder(string roundOrder)
    {
        return ParseAutoBattleRoundOrder(roundOrder)
            .SequenceEqual(AutoBattleDefaultRoundOrder, StringComparer.OrdinalIgnoreCase);
    }

    private static AutoBattleReleaseStep? NormalizeAutoBattleReleaseStep(AutoBattleReleaseStep? step)
    {
        if (step is null)
        {
            return null;
        }

        if (step.IsCustom)
        {
            var sequence = step.Sequence?.Trim();
            if (string.IsNullOrWhiteSpace(sequence))
            {
                return null;
            }

            var name = string.IsNullOrWhiteSpace(step.Name)
                ? "自定义序列"
                : step.Name.Trim();
            return AutoBattleReleaseStep.CreateCustom(name, sequence);
        }

        var skillKey = NormalizeAutoBattleSkillKey(step.SkillKey);
        return string.IsNullOrWhiteSpace(skillKey)
            ? null
            : AutoBattleReleaseStep.CreateSkill(skillKey);
    }

    private static List<AutoBattleTurnSequencePreset> NormalizeAutoBattleTurnSequencePresets(
        IEnumerable<AutoBattleTurnSequencePreset>? presets)
    {
        if (presets is null)
        {
            return [];
        }

        return presets
            .Where(preset => !string.IsNullOrWhiteSpace(preset.Name)
                && !string.IsNullOrWhiteSpace(preset.Sequence))
            .Select(preset => new AutoBattleTurnSequencePreset
            {
                Name = preset.Name.Trim(),
                Sequence = preset.Sequence.Trim()
            })
            .ToList();
    }

    private static string? NormalizeAutoBattleSkillKey(string? skillKey)
    {
        if (string.IsNullOrWhiteSpace(skillKey))
        {
            return null;
        }

        var normalized = skillKey.Trim().ToUpperInvariant();
        return normalized is "1" or "2" or "3" or "4" or "X"
            ? normalized
            : null;
    }

    private static bool RequiresAutoBattleEncounterRelieveDetection(AutoBattleEncounterRelievedAction action)
    {
        return action is AutoBattleEncounterRelievedAction.NoAction
            or AutoBattleEncounterRelievedAction.RecoverEnergy
            or AutoBattleEncounterRelievedAction.Capture;
    }

    private static string GetAutoBattleEncounterRelievedActionLogText(AutoBattleEncounterRelievedAction action)
    {
        return action switch
        {
            AutoBattleEncounterRelievedAction.NoAction => "无操作",
            AutoBattleEncounterRelievedAction.RecoverEnergy => "回能",
            AutoBattleEncounterRelievedAction.ReleaseSkill => "战技",
            AutoBattleEncounterRelievedAction.Capture => "捕捉",
            _ => "回能"
        };
    }

    private enum AutoBattleSkillSelectionAction
    {
        None,
        Skill,
        EnergyRecovery,
        NoAction,
        Capture
    }

    private sealed record AutoBattleSkillSelectionPlan(
        AutoBattleSkillSelectionAction Action,
        bool ShouldSendKeys,
        string Sequence,
        KeyboardInputOptions InputOptions,
        string Description,
        string DisplayKey);
}
