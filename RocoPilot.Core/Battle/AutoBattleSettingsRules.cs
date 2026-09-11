#nullable enable

using RocoPilot.Core.Input;

namespace RocoPilot.Core.Battle;

/// <summary>配置在加载和编辑提交时统一规范化，运行流程只使用规范化后的执行序列。</summary>
public static class AutoBattleSettingsRules
{
    private static readonly string[] DefaultRoundOrder = ["1", "2", "3", "4", "X"];
    private const string SkillPlaceholder = "{skill}";

    private static IReadOnlyList<string> ParseRoundOrder(string roundOrder)
    {
        if (string.IsNullOrWhiteSpace(roundOrder))
        {
            return DefaultRoundOrder;
        }

        var keys = roundOrder
            .Replace("\r", "\n", StringComparison.Ordinal)
            .Replace(';', ',')
            .Split([',', '\n', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(key => key.ToUpperInvariant())
            .Where(key => key is "1" or "2" or "3" or "4" or "X")
            .ToArray();
        return keys.Length == 0 ? DefaultRoundOrder : keys;
    }

    public static string BuildTurnSequence(string template, string skillKey)
    {
        var turnSequence = string.IsNullOrWhiteSpace(template)
            ? AutoBattleSettings.DefaultTurnSequence
            : template.Trim();

        return turnSequence.Contains(SkillPlaceholder, StringComparison.OrdinalIgnoreCase)
            ? turnSequence.Replace(SkillPlaceholder, skillKey, StringComparison.OrdinalIgnoreCase)
            : turnSequence;
    }

    public static string BuildReleaseSequence(AutoBattleSettings settings, AutoBattleReleaseStep releaseStep)
    {
        if (releaseStep.IsCustom)
        {
            return releaseStep.Sequence.Trim();
        }

        return BuildTurnSequence(settings.TurnSequence, releaseStep.SkillKey);
    }

    public static string GetReleaseStepDisplay(AutoBattleReleaseStep? releaseStep)
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

    public static AutoBattleSettings Normalize(AutoBattleSettings? settings)
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

        normalized.ReleaseSequence = ResolveReleaseSequence(normalized)
            .Select(step => step.Clone())
            .ToList();
        normalized.TurnSequencePresets = NormalizePresets(normalized.TurnSequencePresets);
        if (!Enum.IsDefined(normalized.EncounterRelievedAction))
        {
            normalized.EncounterRelievedAction = AutoBattleEncounterRelievedAction.RecoverEnergy;
        }

        if (!Enum.IsDefined(normalized.KeyboardInputMethod))
        {
            normalized.KeyboardInputMethod = KeyboardInputMethod.PostMessage;
        }

        normalized.SkillSelectionActionDelayMs = Math.Clamp(
            normalized.SkillSelectionActionDelayMs,
            AutoBattleSettings.MinimumDelayMs,
            AutoBattleSettings.MaximumDelayMs);
        normalized.SkillSelectionRetryDelayMs = Math.Clamp(
            normalized.SkillSelectionRetryDelayMs,
            AutoBattleSettings.MinimumRetryDelayMs,
            AutoBattleSettings.MaximumDelayMs);
        normalized.KeyboardHoldDurationMs = Math.Clamp(
            normalized.KeyboardHoldDurationMs,
            AutoBattleSettings.MinimumKeyboardHoldDurationMs,
            AutoBattleSettings.MaximumKeyboardHoldDurationMs);
        normalized.KeyboardIntervalMs = Math.Clamp(
            normalized.KeyboardIntervalMs,
            AutoBattleSettings.MinimumDelayMs,
            AutoBattleSettings.MaximumDelayMs);
        normalized.CaptureKeyboardIntervalMs = Math.Clamp(
            normalized.CaptureKeyboardIntervalMs,
            AutoBattleSettings.MinimumDelayMs,
            AutoBattleSettings.MaximumDelayMs);
        normalized.BloodlineCaptureFilter =
            (normalized.BloodlineCaptureFilter ?? BloodlineCaptureFilterSettings.CreateDefault())
            .Clone();

        return normalized;
    }

    private static IReadOnlyList<AutoBattleReleaseStep> ResolveReleaseSequence(AutoBattleSettings settings)
    {
        var releaseSequence = (settings.ReleaseSequence ?? [])
            .Select(NormalizeReleaseStep)
            .OfType<AutoBattleReleaseStep>()
            .ToArray();

        if (releaseSequence.Length > 0
            && (!IsDefaultReleaseSequence(releaseSequence)
                || IsDefaultRoundOrder(settings.RoundOrder)))
        {
            return releaseSequence;
        }

        return ParseRoundOrder(settings.RoundOrder)
            .Select(AutoBattleReleaseStep.CreateSkill)
            .ToArray();
    }

    private static bool IsDefaultReleaseSequence(IReadOnlyList<AutoBattleReleaseStep> releaseSequence)
    {
        return releaseSequence.Count == DefaultRoundOrder.Length
            && releaseSequence
                .Select(step => step.IsCustom ? string.Empty : step.SkillKey)
                .SequenceEqual(DefaultRoundOrder, StringComparer.OrdinalIgnoreCase);
    }

    private static bool IsDefaultRoundOrder(string roundOrder)
    {
        return ParseRoundOrder(roundOrder)
            .SequenceEqual(DefaultRoundOrder, StringComparer.OrdinalIgnoreCase);
    }

    private static AutoBattleReleaseStep? NormalizeReleaseStep(AutoBattleReleaseStep? step)
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

        var skillKey = NormalizeSkillKey(step.SkillKey);
        return string.IsNullOrWhiteSpace(skillKey)
            ? null
            : AutoBattleReleaseStep.CreateSkill(skillKey);
    }

    private static List<AutoBattleTurnSequencePreset> NormalizePresets(
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

    public static string? NormalizeSkillKey(string? skillKey)
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

    public static bool RequiresReliefDetection(AutoBattleEncounterRelievedAction action)
    {
        return action is AutoBattleEncounterRelievedAction.NoAction
            or AutoBattleEncounterRelievedAction.RecoverEnergy
            or AutoBattleEncounterRelievedAction.Capture;
    }

    public static string GetRelievedActionDisplay(AutoBattleEncounterRelievedAction action)
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
}
