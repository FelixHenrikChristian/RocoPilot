using RocoPilot.Models.Input;
using RocoPilot.Models.Runtime;

namespace RocoPilot.Services;

public sealed partial class RuntimeTaskService
{
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
