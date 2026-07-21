using System.Collections.ObjectModel;
using System.Collections.Specialized;

using CommunityToolkit.Mvvm.ComponentModel;

using Microsoft.UI.Xaml;

using RocoPilot.Contracts.Services;
using RocoPilot.Models.Runtime;

namespace RocoPilot.Views.Windows.AutoBattleConfigPages;

internal sealed class AutoBattleConfigEditor : ObservableObject
{
    private const string SkillPlaceholder = "{skill}";

    private readonly IKeyboardInputService _keyboardInputService;

    public ObservableCollection<AutoBattleReleaseEditorItem> NormalReleaseItems
    {
        get;
    } = [];

    public ObservableCollection<AutoBattleReleaseEditorItem> BossReleaseItems
    {
        get;
    } = [];

    public ObservableCollection<AutoBattlePresetEditorItem> SharedPresetItems
    {
        get;
    } = [];

    public ObservableCollection<AutoBattleReleaseEditorItem> BossComboItems
    {
        get;
    } = [];

    public Visibility NormalReleaseEmptyVisibility => NormalReleaseItems.Count == 0
        ? Visibility.Visible
        : Visibility.Collapsed;

    public Visibility BossReleaseEmptyVisibility => BossReleaseItems.Count == 0
        ? Visibility.Visible
        : Visibility.Collapsed;

    public Visibility BossComboEmptyVisibility => BossComboItems.Count == 0
        ? Visibility.Visible
        : Visibility.Collapsed;

    public Visibility SharedPresetEmptyVisibility => SharedPresetItems.Count == 0
        ? Visibility.Visible
        : Visibility.Collapsed;

    public string NormalReleaseSummary => BuildReleaseSummary(NormalReleaseItems);

    public string BossReleaseSummary => BuildReleaseSummary(BossReleaseItems);

    public string BossComboSummary => BuildReleaseSummary(BossComboItems, emptyText: "未配置首领连招");

    public string SharedPresetSummary => SharedPresetItems.Count == 0
        ? "尚未创建公共序列"
        : $"已创建 {SharedPresetItems.Count} 个公共序列";

    public AutoBattleConfigEditor(
        AutoBattleSettings settings,
        IKeyboardInputService keyboardInputService)
    {
        _keyboardInputService = keyboardInputService;

        NormalReleaseItems.CollectionChanged += NormalReleaseItems_CollectionChanged;
        BossReleaseItems.CollectionChanged += BossReleaseItems_CollectionChanged;
        BossComboItems.CollectionChanged += BossComboItems_CollectionChanged;
        SharedPresetItems.CollectionChanged += SharedPresetItems_CollectionChanged;

        LoadSettings(settings);
    }

    public void AppendNormalSkill(string? skillKey)
    {
        if (NormalizeSkillKey(skillKey) is { } normalizedSkillKey)
        {
            NormalReleaseItems.Add(AutoBattleReleaseEditorItem.CreateSkill(normalizedSkillKey));
        }
    }

    public void ResetNormalReleaseSequence()
    {
        NormalReleaseItems.Clear();
        foreach (var step in AutoBattleSettings.CreateDefaultReleaseSequence())
        {
            NormalReleaseItems.Add(AutoBattleReleaseEditorItem.CreateSkill(step.SkillKey));
        }
    }

    public void ClearNormalReleaseSequence()
    {
        NormalReleaseItems.Clear();
    }

    public void RemoveNormalReleaseItem(AutoBattleReleaseEditorItem item)
    {
        NormalReleaseItems.Remove(item);
    }

    public void AppendBossSkill(string? skillKey)
    {
        if (NormalizeSkillKey(skillKey) is { } normalizedSkillKey)
        {
            BossReleaseItems.Add(AutoBattleReleaseEditorItem.CreateSkill(normalizedSkillKey));
        }
    }

    public void ResetBossReleaseSequence()
    {
        BossReleaseItems.Clear();
        foreach (var step in AutoBattleSettings.CreateDefaultReleaseSequence())
        {
            BossReleaseItems.Add(AutoBattleReleaseEditorItem.CreateSkill(step.SkillKey));
        }
    }

    public void ClearBossReleaseSequence()
    {
        BossReleaseItems.Clear();
    }

    public void RemoveBossReleaseItem(AutoBattleReleaseEditorItem item)
    {
        BossReleaseItems.Remove(item);
    }

    public void AddSharedPreset()
    {
        SharedPresetItems.Add(new AutoBattlePresetEditorItem
        {
            Name = $"序列 {SharedPresetItems.Count + 1}",
            Sequence = "1"
        });
    }

    public void RemoveSharedPreset(AutoBattlePresetEditorItem preset)
    {
        SharedPresetItems.Remove(preset);
    }

    public bool TryInsertSharedPresetIntoNormal(
        AutoBattlePresetEditorItem preset,
        out AutoBattleConfigValidationError error)
    {
        if (!TryValidateNamedSequence(
                preset.Name,
                preset.Sequence,
                "公共单回合执行序列",
                AutoBattleConfigSection.SharedSequences,
                out error))
        {
            return false;
        }

        NormalReleaseItems.Add(AutoBattleReleaseEditorItem.CreateCustom(
            preset.Name.Trim(),
            preset.Sequence.Trim()));
        return true;
    }

    public bool TryInsertSharedPresetIntoBossRelease(
        AutoBattlePresetEditorItem preset,
        out AutoBattleConfigValidationError error)
    {
        if (!TryValidateNamedSequence(
                preset.Name,
                preset.Sequence,
                "公共单回合执行序列",
                AutoBattleConfigSection.SharedSequences,
                out error))
        {
            return false;
        }

        BossReleaseItems.Add(AutoBattleReleaseEditorItem.CreateCustom(
            preset.Name.Trim(),
            preset.Sequence.Trim()));
        return true;
    }

    public void AppendBossComboSkill(string? skillKey)
    {
        if (NormalizeSkillKey(skillKey) is { } normalizedSkillKey)
        {
            BossComboItems.Add(AutoBattleReleaseEditorItem.CreateSkill(normalizedSkillKey));
        }
    }

    public void ResetBossComboSequence()
    {
        ApplyBossComboSequence(AutoBattleSettings.DefaultBossComboSequence);
    }

    public void ClearBossComboSequence()
    {
        BossComboItems.Clear();
    }

    public void RemoveBossComboItem(AutoBattleReleaseEditorItem item)
    {
        BossComboItems.Remove(item);
    }

    public bool TryBuildSettings(
        AutoBattleSettings source,
        out AutoBattleSettings settings,
        out AutoBattleConfigValidationError error)
    {
        settings = source.Clone();

        if (!TryBuildReleaseSequence(
                NormalReleaseItems,
                "普通战斗",
                AutoBattleConfigSection.Normal,
                out var releaseSequence,
                out error)
            || !TryBuildReleaseSequence(
                BossReleaseItems,
                "首领战斗",
                AutoBattleConfigSection.Boss,
                out var bossReleaseSequence,
                out error))
        {
            return false;
        }

        var presets = new List<AutoBattleTurnSequencePreset>();
        foreach (var preset in SharedPresetItems)
        {
            var name = preset.Name.Trim();
            var sequence = preset.Sequence.Trim();
            if (string.IsNullOrWhiteSpace(name) && string.IsNullOrWhiteSpace(sequence))
            {
                continue;
            }

            if (!TryValidateNamedSequence(
                    name,
                    sequence,
                    "公共单回合执行序列",
                    AutoBattleConfigSection.SharedSequences,
                    out error))
            {
                return false;
            }

            presets.Add(new AutoBattleTurnSequencePreset
            {
                Name = name,
                Sequence = sequence
            });
        }

        var bossComboSkillKeys = BossComboItems
            .Where(item => !item.IsCustom)
            .Select(item => item.SkillKey)
            .ToArray();
        if (bossComboSkillKeys.Length == 0
            || !BossBattleComboSequence.TryNormalize(
                string.Join(", ", bossComboSkillKeys),
                out var bossComboSequence))
        {
            error = new AutoBattleConfigValidationError(
                "首领连招无效",
                "请至少追加一个技能 1-4 或 X 回能。",
                AutoBattleConfigSection.Boss);
            return false;
        }

        settings.RoundOrder = BuildRoundOrder(releaseSequence);
        settings.TurnSequence = AutoBattleSettings.DefaultTurnSequence;
        settings.ReleaseSequence = releaseSequence;
        settings.BossReleaseSequence = bossReleaseSequence;
        settings.TurnSequencePresets = presets;
        settings.BossComboSequence = bossComboSequence;
        error = default;
        return true;
    }

    private void LoadSettings(AutoBattleSettings settings)
    {
        var releaseSequence = settings.ReleaseSequence is { Count: > 0 }
            ? settings.ReleaseSequence
            : AutoBattleSettings.CreateDefaultReleaseSequence();
        foreach (var step in releaseSequence)
        {
            NormalReleaseItems.Add(CreateReleaseEditorItem(step, settings.TurnSequence));
        }

        var bossReleaseSequence = settings.BossReleaseSequence is { Count: > 0 }
            ? settings.BossReleaseSequence
            : releaseSequence;
        foreach (var step in bossReleaseSequence)
        {
            BossReleaseItems.Add(CreateReleaseEditorItem(step, settings.TurnSequence));
        }

        foreach (var preset in settings.TurnSequencePresets ?? [])
        {
            SharedPresetItems.Add(new AutoBattlePresetEditorItem
            {
                Name = preset.Name,
                Sequence = preset.Sequence
            });
        }

        ApplyBossComboSequence(settings.BossComboSequence);
        RefreshReleaseIndexes(NormalReleaseItems);
        RefreshReleaseIndexes(BossReleaseItems);
    }

    private bool TryBuildReleaseSequence(
        IReadOnlyCollection<AutoBattleReleaseEditorItem> editorItems,
        string battleTypeName,
        AutoBattleConfigSection section,
        out List<AutoBattleReleaseStep> releaseSequence,
        out AutoBattleConfigValidationError error)
    {
        releaseSequence = [];
        if (editorItems.Count == 0)
        {
            error = new AutoBattleConfigValidationError(
                "释放顺序为空",
                $"请为{battleTypeName}至少保留一个释放动作。",
                section);
            return false;
        }

        foreach (var item in editorItems)
        {
            if (item.IsCustom)
            {
                if (!TryValidateNamedSequence(
                        item.Name,
                        item.Sequence,
                        item.DisplayText,
                        section,
                        out error))
                {
                    return false;
                }

                releaseSequence.Add(AutoBattleReleaseStep.CreateCustom(
                    item.Name.Trim(),
                    item.Sequence.Trim()));
                continue;
            }

            releaseSequence.Add(AutoBattleReleaseStep.CreateSkill(item.SkillKey));
        }

        error = default;
        return true;
    }

    private void ApplyBossComboSequence(string? sequence)
    {
        BossComboItems.Clear();
        foreach (var skillKey in BossBattleComboSequence.ParseOrDefault(sequence))
        {
            BossComboItems.Add(AutoBattleReleaseEditorItem.CreateSkill(skillKey));
        }
    }

    private bool TryValidateNamedSequence(
        string name,
        string sequence,
        string label,
        AutoBattleConfigSection section,
        out AutoBattleConfigValidationError error)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            error = new AutoBattleConfigValidationError(
                "名称为空",
                $"{label}需要填写名称。",
                section);
            return false;
        }

        if (string.IsNullOrWhiteSpace(sequence))
        {
            error = new AutoBattleConfigValidationError(
                "按键序列为空",
                $"{label}需要填写按键序列。",
                section);
            return false;
        }

        if (sequence.Contains(SkillPlaceholder, StringComparison.OrdinalIgnoreCase))
        {
            error = new AutoBattleConfigValidationError(
                "按键序列无效",
                "请写入实际按键，例如 1, Space。",
                section);
            return false;
        }

        if (!_keyboardInputService.TryParseSequence(sequence, out _, out var parseError))
        {
            error = new AutoBattleConfigValidationError(
                "按键序列无效",
                parseError,
                section);
            return false;
        }

        error = default;
        return true;
    }

    private void NormalReleaseItems_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        RefreshReleaseIndexes(NormalReleaseItems);
        OnPropertyChanged(nameof(NormalReleaseEmptyVisibility));
        OnPropertyChanged(nameof(NormalReleaseSummary));
    }

    private void BossReleaseItems_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        RefreshReleaseIndexes(BossReleaseItems);
        OnPropertyChanged(nameof(BossReleaseEmptyVisibility));
        OnPropertyChanged(nameof(BossReleaseSummary));
    }

    private void BossComboItems_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        RefreshReleaseIndexes(BossComboItems);
        OnPropertyChanged(nameof(BossComboEmptyVisibility));
        OnPropertyChanged(nameof(BossComboSummary));
    }

    private void SharedPresetItems_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        OnPropertyChanged(nameof(SharedPresetEmptyVisibility));
        OnPropertyChanged(nameof(SharedPresetSummary));
    }

    private static void RefreshReleaseIndexes(
        IReadOnlyList<AutoBattleReleaseEditorItem> releaseItems)
    {
        for (var index = 0; index < releaseItems.Count; index++)
        {
            releaseItems[index].Position = index + 1;
        }
    }

    private static string BuildReleaseSummary(
        IReadOnlyCollection<AutoBattleReleaseEditorItem> releaseItems,
        string emptyText = "未配置释放顺序")
    {
        if (releaseItems.Count == 0)
        {
            return emptyText;
        }

        var preview = string.Join(" → ", releaseItems.Take(8).Select(item => item.DisplayText));
        var suffix = releaseItems.Count > 8
            ? $"等 {releaseItems.Count} 步"
            : $"{releaseItems.Count} 步";
        return $"{preview} · {suffix}";
    }

    private static AutoBattleReleaseEditorItem CreateReleaseEditorItem(
        AutoBattleReleaseStep step,
        string turnSequence)
    {
        if (step.IsCustom)
        {
            return AutoBattleReleaseEditorItem.CreateCustom(step.Name, step.Sequence);
        }

        var skillKey = NormalizeSkillKey(step.SkillKey) ?? "1";
        if (!string.IsNullOrWhiteSpace(turnSequence)
            && !string.Equals(
                turnSequence.Trim(),
                AutoBattleSettings.DefaultTurnSequence,
                StringComparison.Ordinal))
        {
            return AutoBattleReleaseEditorItem.CreateCustom(
                skillKey,
                ApplyTurnSequence(turnSequence, skillKey));
        }

        return AutoBattleReleaseEditorItem.CreateSkill(skillKey);
    }

    private static string BuildRoundOrder(IEnumerable<AutoBattleReleaseStep> releaseSequence)
    {
        var skillKeys = releaseSequence
            .Where(step => !step.IsCustom)
            .Select(step => step.SkillKey)
            .Where(skillKey => NormalizeSkillKey(skillKey) is not null)
            .ToArray();

        return skillKeys.Length == 0
            ? AutoBattleSettings.DefaultRoundOrder
            : string.Join(", ", skillKeys);
    }

    private static string ApplyTurnSequence(string turnSequence, string skillKey)
    {
        var normalized = string.IsNullOrWhiteSpace(turnSequence)
            ? AutoBattleSettings.DefaultTurnSequence
            : turnSequence.Trim();
        return normalized.Contains(SkillPlaceholder, StringComparison.OrdinalIgnoreCase)
            ? normalized.Replace(
                SkillPlaceholder,
                skillKey,
                StringComparison.OrdinalIgnoreCase)
            : normalized;
    }

    private static string? NormalizeSkillKey(string? skillKey)
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
}

internal enum AutoBattleConfigSection
{
    Normal,
    Boss,
    Legendary,
    SharedSequences
}

internal readonly record struct AutoBattleConfigValidationError(
    string Title,
    string Message,
    AutoBattleConfigSection Section);

internal sealed class AutoBattleReleaseEditorItem : ObservableObject
{
    private int _position;

    public bool IsCustom
    {
        get;
        private init;
    }

    public string SkillKey
    {
        get;
        private init;
    } = string.Empty;

    public string Name
    {
        get;
        private init;
    } = string.Empty;

    public string Sequence
    {
        get;
        private init;
    } = string.Empty;

    public int Position
    {
        get => _position;
        set
        {
            if (SetProperty(ref _position, value))
            {
                OnPropertyChanged(nameof(PositionText));
            }
        }
    }

    public string PositionText => $"#{Position}";

    public string DisplayText => IsCustom ? Name : SkillKey;

    public string DetailText => IsCustom ? Sequence : "技能键";

    public static AutoBattleReleaseEditorItem CreateSkill(string skillKey)
    {
        return new AutoBattleReleaseEditorItem
        {
            IsCustom = false,
            SkillKey = skillKey,
            Name = skillKey,
            Sequence = string.Empty
        };
    }

    public static AutoBattleReleaseEditorItem CreateCustom(string name, string sequence)
    {
        return new AutoBattleReleaseEditorItem
        {
            IsCustom = true,
            SkillKey = string.Empty,
            Name = string.IsNullOrWhiteSpace(name) ? "自定义" : name.Trim(),
            Sequence = sequence.Trim()
        };
    }
}

internal sealed class AutoBattlePresetEditorItem : ObservableObject
{
    private string _name = string.Empty;
    private string _sequence = string.Empty;

    public string Name
    {
        get => _name;
        set => SetProperty(ref _name, value);
    }

    public string Sequence
    {
        get => _sequence;
        set => SetProperty(ref _sequence, value);
    }
}


