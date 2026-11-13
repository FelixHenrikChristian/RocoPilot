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
            .Where(key => key is "1" or "2" or "3" or "4" or "X" or "S")
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
            var preset = FindPreset(settings.TurnSequencePresets, releaseStep.PresetId);
            return (preset?.Sequence ?? releaseStep.Sequence).Trim();
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

        normalized.TurnSequencePresets = NormalizePresets(normalized.TurnSequencePresets);
        normalized.ReleaseSequence = ResolveReleaseSequence(normalized)
            .Select(step => step.Clone())
            .ToList();
        normalized.FlowerSeedReleaseSequence = NormalizeChallengeSequence(normalized.FlowerSeedReleaseSequence);
        normalized.LegendaryReleaseSequence = NormalizeChallengeSequence(normalized.LegendaryReleaseSequence);
        // 三种战斗类型各管各的配置：已有配置列表直接使用，没有则由各自扁平序列生成默认配置。
        normalized.NormalReleaseConfigs = NormalizeReleaseConfigs(
            MigratePerTypeConfigs(normalized.NormalReleaseConfigs, normalized.ReleaseSequence),
            normalized.ReleaseSequence).Configs;
        normalized.FlowerSeedReleaseConfigs = NormalizeReleaseConfigs(
            MigratePerTypeConfigs(normalized.FlowerSeedReleaseConfigs, normalized.FlowerSeedReleaseSequence),
            normalized.FlowerSeedReleaseSequence).Configs;
        normalized.LegendaryReleaseConfigs = NormalizeReleaseConfigs(
            MigratePerTypeConfigs(normalized.LegendaryReleaseConfigs, normalized.LegendaryReleaseSequence),
            normalized.LegendaryReleaseSequence).Configs;
        ResolvePresetReferences(normalized);
        normalized.SelectedNormalReleaseConfigId = SelectReleaseConfigId(
            normalized.NormalReleaseConfigs, normalized.SelectedNormalReleaseConfigId);
        normalized.SelectedFlowerSeedReleaseConfigId = SelectReleaseConfigId(
            normalized.FlowerSeedReleaseConfigs, normalized.SelectedFlowerSeedReleaseConfigId);
        normalized.SelectedLegendaryReleaseConfigId = SelectReleaseConfigId(
            normalized.LegendaryReleaseConfigs, normalized.SelectedLegendaryReleaseConfigId);
        // 各类型的运行时扁平序列 = 该类型选中配置的步骤。
        normalized.ReleaseSequence = CloneSteps(
            FindSelectedConfig(normalized.NormalReleaseConfigs, normalized.SelectedNormalReleaseConfigId));
        normalized.ReleaseSequence.RemoveAll(AutoBattleController.IsSkipTurnStep);
        normalized.FlowerSeedReleaseSequence = CloneSteps(
            FindSelectedConfig(normalized.FlowerSeedReleaseConfigs, normalized.SelectedFlowerSeedReleaseConfigId));
        normalized.LegendaryReleaseSequence = CloneSteps(
            FindSelectedConfig(normalized.LegendaryReleaseConfigs, normalized.SelectedLegendaryReleaseConfigId));
        if (!Enum.IsDefined(normalized.EncounterRelievedAction))
        {
            normalized.EncounterRelievedAction = AutoBattleEncounterRelievedAction.RecoverEnergy;
        }

        normalized.PlayerNumber = Math.Clamp(normalized.PlayerNumber, 1, 4);

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
        normalized.EncounterCapturePolicy = NormalizeEncounterCapturePolicy(
            normalized.EncounterCapturePolicy ?? EncounterCapturePolicySettings.CreateDefault());

        return normalized;
    }

    private static EncounterCapturePolicySettings NormalizeEncounterCapturePolicy(EncounterCapturePolicySettings policy)
    {
        var normalized = policy.Clone();
        normalized.Rules =
        [
            .. normalized.Rules
                .Select(rule =>
                {
                    var clone = rule.Clone();
                    clone.SpiritName = clone.SpiritName?.Trim() ?? string.Empty;
                    return clone;
                })
                .Where(rule => rule.SpiritName.Length > 0)
                .DistinctBy(rule => rule.SpiritName, StringComparer.OrdinalIgnoreCase)
        ];
        return normalized;
    }

    private static List<AutoBattleReleaseStep> NormalizeChallengeSequence(IEnumerable<AutoBattleReleaseStep> sequence)
    {
        var steps = sequence.Select(NormalizeReleaseStep).OfType<AutoBattleReleaseStep>().ToList();
        return steps.Count > 0 ? steps : AutoBattleSettings.CreateDefaultReleaseSequence();
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
            return AutoBattleReleaseStep.CreateCustom(name, sequence, step.PresetId);
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

        var ids = new HashSet<string>(StringComparer.Ordinal);
        return presets
            .Where(preset => !string.IsNullOrWhiteSpace(preset.Name)
                && !string.IsNullOrWhiteSpace(preset.Sequence))
            .Select(preset => new AutoBattleTurnSequencePreset
            {
                Id = !string.IsNullOrWhiteSpace(preset.Id) && ids.Add(preset.Id.Trim())
                    ? preset.Id.Trim()
                    : CreatePresetId(ids),
                Name = preset.Name.Trim(),
                Sequence = preset.Sequence.Trim()
            })
            .ToList();
    }

    private static string CreatePresetId(HashSet<string> ids)
    {
        var id = Guid.NewGuid().ToString("N");
        ids.Add(id);
        return id;
    }

    private static AutoBattleTurnSequencePreset? FindPreset(
        IEnumerable<AutoBattleTurnSequencePreset> presets, string? presetId)
    {
        return string.IsNullOrWhiteSpace(presetId)
            ? null
            : presets.FirstOrDefault(preset => preset.Id == presetId);
    }

    private static void ResolvePresetReferences(AutoBattleSettings settings)
    {
        var allSteps = settings.ReleaseSequence.Concat(settings.FlowerSeedReleaseSequence)
            .Concat(settings.LegendaryReleaseSequence)
            .Concat(settings.NormalReleaseConfigs.SelectMany(config => config.Steps))
            .Concat(settings.FlowerSeedReleaseConfigs.SelectMany(config => config.Steps))
            .Concat(settings.LegendaryReleaseConfigs.SelectMany(config => config.Steps));
        foreach (var step in allSteps.Where(step => step.IsCustom))
        {
            var preset = FindPreset(settings.TurnSequencePresets, step.PresetId);
            if (string.IsNullOrWhiteSpace(step.PresetId))
            {
                // 旧配置只保存副本；仅在名称和按键能唯一匹配时建立关联。
                var matches = settings.TurnSequencePresets.Where(candidate =>
                    candidate.Name == step.Name?.Trim()
                    && candidate.Sequence == step.Sequence?.Trim()).Take(2).ToArray();
                preset = matches.Length == 1 ? matches[0] : null;
            }

            // 已删除的关联保留标识和最后保存的内容，避免被同名新序列重新关联。
            if (preset is not null)
            {
                step.PresetId = preset.Id;
                step.Name = preset.Name;
                step.Sequence = preset.Sequence;
            }
        }
    }

    private sealed record ReleaseConfigsResult(List<AutoBattleReleaseConfig> Configs);

    /// <summary>
    /// 规范化某战斗类型的配置列表：至少保留一个配置；空列表时由扁平序列迁移生成「配置1」。
    /// </summary>
    private static ReleaseConfigsResult NormalizeReleaseConfigs(
        IEnumerable<AutoBattleReleaseConfig>? configs,
        IReadOnlyList<AutoBattleReleaseStep> fallbackSequence)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var usedNames = new HashSet<string>(StringComparer.Ordinal);
        var normalized = new List<AutoBattleReleaseConfig>();
        foreach (var config in configs ?? [])
        {
            if (config is null)
            {
                continue;
            }

            var name = config.Name?.Trim();
            if (string.IsNullOrWhiteSpace(name))
            {
                name = CreateDefaultConfigName(usedNames);
            }

            usedNames.Add(name);
            var steps = (config.Steps ?? [])
                .Select(NormalizeReleaseStep)
                .OfType<AutoBattleReleaseStep>()
                .ToList();
            if (steps.Count == 0)
            {
                steps = AutoBattleSettings.CreateDefaultReleaseSequence();
            }

            normalized.Add(new AutoBattleReleaseConfig
            {
                Id = !string.IsNullOrWhiteSpace(config.Id) && ids.Add(config.Id.Trim())
                    ? config.Id.Trim()
                    : CreatePresetId(ids),
                Name = name,
                Steps = steps
            });
        }

        if (normalized.Count == 0)
        {
            // 兜底：由扁平序列生成一个默认配置（正常流程不会走到，迁移逻辑总会产出配置）。
            normalized.Add(new AutoBattleReleaseConfig
            {
                Id = CreatePresetId(ids),
                Name = "默认配置",
                Steps = fallbackSequence.Select(step => step.Clone()).ToList()
            });
        }

        return new ReleaseConfigsResult(normalized);
    }

    /// <summary>取未被占用的最小「配置x」名称。</summary>
    public static string CreateDefaultConfigName(IEnumerable<string>? existingNames)
    {
        var used = (existingNames ?? []).Select(name => name?.Trim()).Where(name => !string.IsNullOrEmpty(name)).ToHashSet(StringComparer.Ordinal);
        for (var index = 1; ; index++)
        {
            var candidate = $"配置{index}";
            if (used.Add(candidate))
            {
                return candidate;
            }
        }
    }

    /// <summary>迁移到各类型的配置列表：已有配置直接使用；没有则由该类型的旧扁平序列生成「默认配置」。</summary>
    private static List<AutoBattleReleaseConfig> MigratePerTypeConfigs(
        IEnumerable<AutoBattleReleaseConfig>? perTypeConfigs,
        IReadOnlyList<AutoBattleReleaseStep> flatFallback)
    {
        var own = (perTypeConfigs ?? []).Where(config => config is not null).ToList();
        if (own.Count > 0)
        {
            return own;
        }

        return
        [
            new AutoBattleReleaseConfig
            {
                Name = "默认配置",
                Steps = flatFallback.Select(step => step.Clone()).ToList()
            }
        ];
    }

    /// <summary>选中配置校验：无效时回退到第一个配置。</summary>
    private static string SelectReleaseConfigId(
        IReadOnlyList<AutoBattleReleaseConfig> configs,
        string? selectedId)
    {
        var id = selectedId?.Trim();
        return configs.Any(config => config.Id == id)
            ? id!
            : configs[0].Id;
    }

    private static AutoBattleReleaseConfig FindSelectedConfig(
        IReadOnlyList<AutoBattleReleaseConfig> configs,
        string selectedId)
    {
        return configs.FirstOrDefault(config => config.Id == selectedId)
            ?? configs[0];
    }

    private static List<AutoBattleReleaseStep> CloneSteps(AutoBattleReleaseConfig config)
    {
        return config.Steps.Select(step => step.Clone()).ToList();
    }

    public static string? NormalizeSkillKey(string? skillKey)
    {
        if (string.IsNullOrWhiteSpace(skillKey))
        {
            return null;
        }

        var normalized = skillKey.Trim().ToUpperInvariant();
        return normalized is "1" or "2" or "3" or "4" or "X" or "S"
            ? normalized
            : null;
    }

    /// <summary>跳过回合步骤：等待轮到本玩家（P 角标）后，立即继续执行后续步骤。</summary>
    public const string SkipTurnSkillKey = "S";

    public static string GetSkillKeyDisplayName(string? skillKey)
    {
        return string.Equals(skillKey, SkipTurnSkillKey, StringComparison.OrdinalIgnoreCase)
            ? "跳过回合"
            : skillKey ?? string.Empty;
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
