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
    private const string BattleSpaceTemplateName = "battle-space.png";
    private const string AutoBattleSkillPlaceholder = "{skill}";

    private static readonly TimeSpan AutoBattleSkillSelectionActionDelay = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan AutoBattleSkillSelectionRetryDelay = TimeSpan.FromSeconds(4);
    private static readonly TimeSpan AutoBattlePetSwitchProbeDelay = TimeSpan.FromMilliseconds(1500);
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
    private static readonly string[] BattleMagicRegionIds =
    [
        "battle-magic"
    ];
    private static readonly string[] BattleTipEnergyRegionIds =
    [
        "battle-tip-energe"
    ];
    private static readonly string[] BattleSpaceRegionIds =
    [
        "battle-space"
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
    private static readonly ImageMatchOptions BattleSpaceMatchOptions = new()
    {
        MinimumScore = 0.88,
        AlphaThreshold = 16,
        SearchStep = 1
    };
    private static readonly KeyboardInputOptions AutoBattleKeyboardInputOptions = new()
    {
        HoldDurationMs = 45,
        IntervalMs = 120
    };

    private readonly SemaphoreSlim _autoBattleActionLock = new(1, 1);
    private AutoBattleSettings _autoBattleSettings = AutoBattleSettings.CreateDefault();
    private int _autoBattleRoundIndex;
    private bool _wasAutoBattleSkillSelectionVisible;
    private bool _wasAutoBattlePetSwitchingVisible;
    private DateTimeOffset? _autoBattleSkillSelectionVisibleSince;
    private DateTimeOffset? _lastAutoBattleSkillSelectionActionAt;
    private AutoBattleReleaseStep? _currentAutoBattleReleaseStep;
    private AutoBattleSkillSelectionAction _autoBattleSkillSelectionAction;

    public AutoBattleSettings AutoBattleSettings => _autoBattleSettings.Clone();

    public void SetAutoBattleSettings(AutoBattleSettings settings)
    {
        _autoBattleSettings = NormalizeAutoBattleSettings(settings);
        if (!_autoBattleSettings.IsEnabled)
        {
            ResetAutoBattleBattleState();
        }

        _settingsLoaded = true;
        _ = SaveAutoBattleSettingsAsync(_autoBattleSettings);
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
            BattleMagicRegionIds,
            MagicPointTemplateName,
            MagicPointMatchOptions,
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

        if (!_wasAutoBattleSkillSelectionVisible)
        {
            _wasAutoBattleSkillSelectionVisible = true;
            _autoBattleSkillSelectionVisibleSince = now;
            _lastAutoBattleSkillSelectionActionAt = null;
            _currentAutoBattleReleaseStep = GetCurrentAutoBattleReleaseStep(settings);
            _autoBattleSkillSelectionAction = AutoBattleSkillSelectionAction.None;
            _logger.LogDebug(
                "自动战斗：检测到技能选择界面，等待 {DelayMs}ms 后执行。ReleaseStep={ReleaseStep}, RoundIndex={RoundIndex}",
                AutoBattleSkillSelectionActionDelay.TotalMilliseconds,
                GetAutoBattleReleaseStepDisplay(_currentAutoBattleReleaseStep),
                _autoBattleRoundIndex);
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
            if (!_keyboardInputService.IsWindowAvailable(state.TargetWindow.Hwnd))
            {
                _logger.LogWarning("自动战斗未执行：目标游戏窗口句柄已失效。");
                return;
            }

            var isRetryingEnergyRecovery =
                _autoBattleSkillSelectionAction == AutoBattleSkillSelectionAction.EnergyRecovery;
            var releaseStep = _currentAutoBattleReleaseStep ?? GetCurrentAutoBattleReleaseStep(settings);
            var sequence = isRetryingEnergyRecovery
                ? "X"
                : BuildAutoBattleReleaseSequence(settings, releaseStep);
            if (!_keyboardInputService.TryParseSequence(sequence, out var keyStrokes, out var parseError)
                || keyStrokes.Count == 0)
            {
                _logger.LogWarning(
                    "自动战斗单回合序列无效。ReleaseStep={ReleaseStep}, Sequence={Sequence}, Error={Error}",
                    GetAutoBattleReleaseStepDisplay(releaseStep),
                    sequence,
                    parseError);
                keyStrokes = !releaseStep.IsCustom
                    && _keyboardInputService.TryParseSequence(releaseStep.SkillKey, out var fallbackStrokes, out _)
                    ? fallbackStrokes
                    : [];
            }

            if (keyStrokes.Count == 0)
            {
                return;
            }

            await _keyboardInputService.SendSequenceAsync(
                state.TargetWindow.Hwnd,
                keyStrokes,
                AutoBattleKeyboardInputOptions,
                cancellationToken);

            _autoBattleSkillSelectionAction = isRetryingEnergyRecovery
                ? AutoBattleSkillSelectionAction.EnergyRecovery
                : AutoBattleSkillSelectionAction.Skill;
            _lastAutoBattleSkillSelectionActionAt = DateTimeOffset.Now;

            var actionText = isRetryingEnergyRecovery ? "按回能键" : "按技能键";
            var pressedKey = isRetryingEnergyRecovery ? "X" : GetAutoBattleReleaseStepDisplay(releaseStep);
            _logger.LogInformation(
                "自动战斗：{Action} {Key}（序列 {Sequence}）",
                actionText,
                pressedKey,
                sequence);
            _logger.LogDebug(
                "自动战斗按键已发送：ReleaseStep={ReleaseStep}, Sequence={Sequence}, Action={Action}, KeyStrokeCount={KeyStrokeCount}, RoundIndex={RoundIndex}",
                pressedKey,
                sequence,
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
        if (!_autoBattleSettings.IsEnabled)
        {
            _wasAutoBattlePetSwitchingVisible = true;
            return;
        }

        if (_wasAutoBattlePetSwitchingVisible)
        {
            return;
        }

        _wasAutoBattlePetSwitchingVisible = true;
        if (!TemplateExists(BattleSpaceTemplateName))
        {
            _logger.LogWarning("自动战斗换精灵未执行：未找到 {TemplateName}。", BattleSpaceTemplateName);
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

            for (var slot = 1; slot <= 6; slot++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var slotKey = slot.ToString();
                _logger.LogDebug("自动战斗换精灵：尝试按 {SlotKey}", slotKey);
                await _keyboardInputService.SendSequenceAsync(
                    state.TargetWindow.Hwnd,
                    slotKey,
                    AutoBattleKeyboardInputOptions,
                    cancellationToken);

                await Task.Delay(AutoBattlePetSwitchProbeDelay, cancellationToken);

                using var frame = _screenCaptureService.Capture(state.TargetWindow, state.Options.CaptureMethod);
                if (frame is null || !await IsBattleSpaceVisibleAsync(state, frame, cancellationToken))
                {
                    continue;
                }

                await _keyboardInputService.SendSequenceAsync(
                    state.TargetWindow.Hwnd,
                    "Space",
                    AutoBattleKeyboardInputOptions,
                    cancellationToken);
                _logger.LogInformation("自动战斗：切换到第 {Slot} 只精灵，按 Space 确认", slot);
                return;
            }

            _logger.LogWarning("自动战斗换精灵失败：已尝试 1-6，但未检测到 battle-space 确认提示。");
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

    private async Task<bool> TryDetectAutoBattleEnergyShortageAsync(
        RuntimeTaskState state,
        CapturedFrame frame,
        CancellationToken cancellationToken)
    {
        if (!_autoBattleSettings.IsEnabled
            || _autoBattleSkillSelectionAction != AutoBattleSkillSelectionAction.Skill)
        {
            return false;
        }

        var tipText = await RecognizeRegionTextAsync(
            state,
            frame,
            BattleTipEnergyRegionIds,
            cancellationToken,
            "自动战斗");
        var isEnergyShortage = IsEnergyShortageTip(tipText);
        _logger.LogDebug(
            "自动战斗回能筛选：TipText={TipText}, IsEnergyShortage={IsEnergyShortage}",
            FormatLogText(tipText),
            isEnergyShortage);
        if (!isEnergyShortage)
        {
            return false;
        }

        if (!await TrySendAutoBattleEnergyRecoveryAsync(state, cancellationToken))
        {
            return false;
        }

        _autoBattleSkillSelectionAction = AutoBattleSkillSelectionAction.EnergyRecovery;
        _logger.LogInformation("自动战斗：检测到能量不足，按 X 回能");
        return true;
    }

    private async Task<bool> TrySendAutoBattleEnergyRecoveryAsync(
        RuntimeTaskState state,
        CancellationToken cancellationToken)
    {
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

            await _keyboardInputService.SendSequenceAsync(
                state.TargetWindow.Hwnd,
                "X",
                AutoBattleKeyboardInputOptions,
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

    private async Task<bool> IsBattleSpaceVisibleAsync(
        RuntimeTaskState state,
        CapturedFrame frame,
        CancellationToken cancellationToken)
    {
        return await MatchRuntimeTemplateAsync(
            state,
            frame,
            BattleSpaceRegionIds,
            BattleSpaceTemplateName,
            BattleSpaceMatchOptions,
            "自动战斗",
            "换精灵确认提示",
            cancellationToken);
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

    private void CompleteAutoBattleSkillSelectionState()
    {
        if (_autoBattleSkillSelectionAction == AutoBattleSkillSelectionAction.Skill)
        {
            _autoBattleRoundIndex++;
        }

        ResetAutoBattleSkillSelectionState();
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

    private static bool IsEnergyShortageTip(string tipText)
    {
        if (string.IsNullOrWhiteSpace(tipText))
        {
            return false;
        }

        var normalized = new string(tipText.Where(character => !char.IsWhiteSpace(character)).ToArray());
        return normalized.Contains("能量不足", StringComparison.Ordinal)
            || TextMatchingHelper.IsSimilar(tipText, "能量不足", 0.65, out _);
    }

    private void ResetAutoBattleBattleState()
    {
        _autoBattleRoundIndex = 0;
        ResetAutoBattleSkillSelectionState();
        _wasAutoBattlePetSwitchingVisible = false;
    }

    private void ResetAutoBattleSkillSelectionState()
    {
        _wasAutoBattleSkillSelectionVisible = false;
        _autoBattleSkillSelectionVisibleSince = null;
        _lastAutoBattleSkillSelectionActionAt = null;
        _currentAutoBattleReleaseStep = null;
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

        return normalized;
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

    private enum AutoBattleSkillSelectionAction
    {
        None,
        Skill,
        EnergyRecovery
    }
}
