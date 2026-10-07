using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;

using CommunityToolkit.Mvvm.ComponentModel;

using Microsoft.UI.Xaml;

using RocoPilot.Contracts.Services;
using RocoPilot.Models.Runtime;

namespace RocoPilot.Views.Windows.AutoBattleConfigPages;

internal sealed class AutoBattleConfigEditor : ObservableObject
{
    private const string SkillPlaceholder = "{skill}";

    private readonly IKeyboardInputService _keyboardInputService;
    private readonly HashSet<AutoBattlePresetEditorItem> _observedPresets = [];

    public AutoBattleReleaseSequenceEditor NormalRelease { get; } = new();

    public AutoBattleReleaseSequenceEditor FlowerSeedRelease { get; } = new();

    public ObservableCollection<AutoBattlePresetEditorItem> SharedPresetItems
    {
        get;
    } = [];

    private bool _bloodlineCaptureFilterEnabled = true;
    private bool _captureBloodlineQiYi = true;
    private bool _captureBloodlineHunXue;
    private bool _captureBloodlineWuRan = true;
    private bool _captureBloodlineNormal;
    private bool _captureBloodlineUnrecognized = true;

    public bool BloodlineCaptureFilterEnabled
    {
        get => _bloodlineCaptureFilterEnabled;
        set
        {
            if (SetProperty(ref _bloodlineCaptureFilterEnabled, value))
            {
                OnPropertyChanged(nameof(BloodlineCaptureFilterOptionsEnabled));
                OnPropertyChanged(nameof(BloodlineCaptureFilterSummary));
            }
        }
    }

    public bool CaptureBloodlineQiYi
    {
        get => _captureBloodlineQiYi;
        set
        {
            if (SetProperty(ref _captureBloodlineQiYi, value))
            {
                OnPropertyChanged(nameof(BloodlineCaptureFilterSummary));
            }
        }
    }

    public bool CaptureBloodlineHunXue
    {
        get => _captureBloodlineHunXue;
        set
        {
            if (SetProperty(ref _captureBloodlineHunXue, value))
            {
                OnPropertyChanged(nameof(BloodlineCaptureFilterSummary));
            }
        }
    }

    public bool CaptureBloodlineWuRan
    {
        get => _captureBloodlineWuRan;
        set
        {
            if (SetProperty(ref _captureBloodlineWuRan, value))
            {
                OnPropertyChanged(nameof(BloodlineCaptureFilterSummary));
            }
        }
    }

    public bool CaptureBloodlineNormal
    {
        get => _captureBloodlineNormal;
        set
        {
            if (SetProperty(ref _captureBloodlineNormal, value))
            {
                OnPropertyChanged(nameof(BloodlineCaptureFilterSummary));
            }
        }
    }

    public bool CaptureBloodlineUnrecognized
    {
        get => _captureBloodlineUnrecognized;
        set
        {
            if (SetProperty(ref _captureBloodlineUnrecognized, value))
            {
                OnPropertyChanged(nameof(BloodlineCaptureFilterSummary));
            }
        }
    }

    public bool BloodlineCaptureFilterOptionsEnabled => BloodlineCaptureFilterEnabled;

    public string BloodlineCaptureFilterSummary
    {
        get
        {
            if (!BloodlineCaptureFilterEnabled)
            {
                return "已关闭：奇遇解除选择捕捉时不按血脉筛选";
            }

            var selected = new List<string>();
            if (CaptureBloodlineQiYi)
            {
                selected.Add("奇异");
            }

            if (CaptureBloodlineHunXue)
            {
                selected.Add("混血");
            }

            if (CaptureBloodlineWuRan)
            {
                selected.Add("污染");
            }

            if (CaptureBloodlineNormal)
            {
                selected.Add("普通");
            }

            if (CaptureBloodlineUnrecognized)
            {
                selected.Add("未识别");
            }

            return selected.Count == 0
                ? "已启用：不捕捉任何血脉，一律释放战技"
                : $"已启用：仅捕捉 {string.Join("、", selected)}";
        }
    }

    public Visibility SharedPresetEmptyVisibility => SharedPresetItems.Count == 0
        ? Visibility.Visible
        : Visibility.Collapsed;

    public Visibility SharedPresetListVisibility => SharedPresetItems.Count > 0
        ? Visibility.Visible
        : Visibility.Collapsed;

    public string SharedPresetSummary => SharedPresetItems.Count == 0
        ? "尚未创建公共序列"
        : $"已创建 {SharedPresetItems.Count} 个公共序列";

    public AutoBattleConfigEditor(
        AutoBattleSettings settings,
        IKeyboardInputService keyboardInputService)
    {
        _keyboardInputService = keyboardInputService;

        SharedPresetItems.CollectionChanged += SharedPresetItems_CollectionChanged;

        LoadSettings(AutoBattleSettingsRules.Normalize(settings));
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

    public bool TryInsertSharedPreset(
        AutoBattlePresetEditorItem preset,
        AutoBattleReleaseSequenceEditor releaseEditor,
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

        releaseEditor.Items.Add(AutoBattleReleaseEditorItem.CreateCustom(
            preset.Name.Trim(),
            preset.Sequence.Trim(),
            preset.Id));
        return true;
    }

    public bool TryBuildSettings(
        AutoBattleSettings source,
        out AutoBattleSettings settings,
        out AutoBattleConfigValidationError error)
    {
        settings = source.Clone();

        if (!TryBuildReleaseSequence(
                NormalRelease.Items,
                "普通战斗",
                AutoBattleConfigSection.Normal,
                out var releaseSequence,
                out error))
        {
            return false;
        }

        if (!TryBuildReleaseSequence(
                FlowerSeedRelease.Items,
                "花种战斗",
                AutoBattleConfigSection.FlowerSeed,
                out var flowerSeedReleaseSequence,
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
                Id = preset.Id,
                Name = name,
                Sequence = sequence
            });
        }

        settings.RoundOrder = BuildRoundOrder(releaseSequence);
        settings.TurnSequence = AutoBattleSettings.DefaultTurnSequence;
        settings.ReleaseSequence = releaseSequence;
        settings.FlowerSeedReleaseSequence = flowerSeedReleaseSequence;
        settings.TurnSequencePresets = presets;
        settings.BloodlineCaptureFilter = new BloodlineCaptureFilterSettings
        {
            IsEnabled = BloodlineCaptureFilterEnabled,
            CaptureQiYi = CaptureBloodlineQiYi,
            CaptureHunXue = CaptureBloodlineHunXue,
            CaptureWuRan = CaptureBloodlineWuRan,
            CaptureNormal = CaptureBloodlineNormal,
            CaptureUnrecognized = CaptureBloodlineUnrecognized
        };
        error = default;
        return true;
    }

    private void LoadSettings(AutoBattleSettings settings)
    {
        foreach (var step in settings.ReleaseSequence)
        {
            NormalRelease.Items.Add(CreateReleaseEditorItem(step, settings.TurnSequence));
        }

        foreach (var step in settings.FlowerSeedReleaseSequence)
        {
            FlowerSeedRelease.Items.Add(CreateReleaseEditorItem(step, AutoBattleSettings.DefaultTurnSequence));
        }

        foreach (var preset in settings.TurnSequencePresets ?? [])
        {
            SharedPresetItems.Add(new AutoBattlePresetEditorItem
            {
                Id = preset.Id,
                Name = preset.Name,
                Sequence = preset.Sequence
            });
        }

        var bloodlineFilter = settings.BloodlineCaptureFilter
            ?? BloodlineCaptureFilterSettings.CreateDefault();
        BloodlineCaptureFilterEnabled = bloodlineFilter.IsEnabled;
        CaptureBloodlineQiYi = bloodlineFilter.CaptureQiYi;
        CaptureBloodlineHunXue = bloodlineFilter.CaptureHunXue;
        CaptureBloodlineWuRan = bloodlineFilter.CaptureWuRan;
        CaptureBloodlineNormal = bloodlineFilter.CaptureNormal;
        CaptureBloodlineUnrecognized = bloodlineFilter.CaptureUnrecognized;
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
                        SharedPresetItems.Any(preset => preset.Id == item.PresetId)
                            ? AutoBattleConfigSection.SharedSequences
                            : section,
                        out error))
                {
                    return false;
                }

                releaseSequence.Add(AutoBattleReleaseStep.CreateCustom(
                    item.Name.Trim(),
                    item.Sequence.Trim(),
                    item.PresetId));
                continue;
            }

            releaseSequence.Add(AutoBattleReleaseStep.CreateSkill(item.SkillKey));
        }

        error = default;
        return true;
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

    private void SharedPresetItems_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        foreach (var removed in _observedPresets.Where(preset => !SharedPresetItems.Contains(preset)).ToArray())
        {
            removed.PropertyChanged -= SharedPreset_PropertyChanged;
            _observedPresets.Remove(removed);
        }

        foreach (var preset in SharedPresetItems)
        {
            if (_observedPresets.Add(preset))
            {
                preset.PropertyChanged += SharedPreset_PropertyChanged;
            }
        }

        OnPropertyChanged(nameof(SharedPresetEmptyVisibility));
        OnPropertyChanged(nameof(SharedPresetListVisibility));
        OnPropertyChanged(nameof(SharedPresetSummary));
    }

    private void SharedPreset_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is not AutoBattlePresetEditorItem preset)
        {
            return;
        }

        NormalRelease.UpdatePreset(preset);
        FlowerSeedRelease.UpdatePreset(preset);
    }

    private static AutoBattleReleaseEditorItem CreateReleaseEditorItem(
        AutoBattleReleaseStep step,
        string turnSequence)
    {
        if (step.IsCustom)
        {
            return AutoBattleReleaseEditorItem.CreateCustom(step.Name, step.Sequence, step.PresetId);
        }

        var skillKey = AutoBattleSettingsRules.NormalizeSkillKey(step.SkillKey) ?? "1";
        if (!string.IsNullOrWhiteSpace(turnSequence)
            && !string.Equals(
                turnSequence.Trim(),
                AutoBattleSettings.DefaultTurnSequence,
                StringComparison.Ordinal))
        {
            return AutoBattleReleaseEditorItem.CreateCustom(
                skillKey,
                AutoBattleSettingsRules.BuildTurnSequence(turnSequence, skillKey));
        }

        return AutoBattleReleaseEditorItem.CreateSkill(skillKey);
    }

    private static string BuildRoundOrder(IEnumerable<AutoBattleReleaseStep> releaseSequence)
    {
        var skillKeys = releaseSequence
            .Where(step => !step.IsCustom)
            .Select(step => step.SkillKey)
            .Where(skillKey => AutoBattleSettingsRules.NormalizeSkillKey(skillKey) is not null)
            .ToArray();

        return skillKeys.Length == 0
            ? AutoBattleSettings.DefaultRoundOrder
            : string.Join(", ", skillKeys);
    }

}

internal sealed class AutoBattleReleaseSequenceEditor : ObservableObject
{
    public ObservableCollection<AutoBattleReleaseEditorItem> Items { get; } = [];

    public Visibility EmptyVisibility => Items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

    public string Summary => Items.Count == 0
        ? "未配置释放顺序"
        : $"{string.Join(" → ", Items.Take(8).Select(item => item.DisplayText))} · "
            + (Items.Count > 8 ? $"等 {Items.Count} 步" : $"{Items.Count} 步");

    public AutoBattleReleaseSequenceEditor()
    {
        Items.CollectionChanged += Items_CollectionChanged;
    }

    public void AppendSkill(string? skillKey)
    {
        if (AutoBattleSettingsRules.NormalizeSkillKey(skillKey) is { } key)
        {
            Items.Add(AutoBattleReleaseEditorItem.CreateSkill(key));
        }
    }

    public void MoveEarlier(AutoBattleReleaseEditorItem item)
    {
        var index = Items.IndexOf(item);
        if (index > 0) Items.Move(index, index - 1);
    }

    public void MoveLater(AutoBattleReleaseEditorItem item)
    {
        var index = Items.IndexOf(item);
        if (index >= 0 && index < Items.Count - 1) Items.Move(index, index + 1);
    }

    public void UpdatePreset(AutoBattlePresetEditorItem preset)
    {
        foreach (var item in Items.Where(item => item.PresetId == preset.Id))
        {
            item.UpdatePresetValues(preset.Name, preset.Sequence);
        }

        OnPropertyChanged(nameof(Summary));
    }

    private void Items_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        for (var index = 0; index < Items.Count; index++)
        {
            Items[index].Position = index + 1;
            Items[index].UpdateMoveAvailability(Items.Count);
        }

        OnPropertyChanged(nameof(EmptyVisibility));
        OnPropertyChanged(nameof(Summary));
    }
}

internal enum AutoBattleConfigSection
{
    Normal,
    FlowerSeed,
    SharedSequences,
    BloodlineCapture
}

internal readonly record struct AutoBattleConfigValidationError(
    string Title,
    string Message,
    AutoBattleConfigSection Section);

internal sealed class AutoBattleReleaseEditorItem : ObservableObject
{
    private int _position;
    private string _name = string.Empty;
    private string _sequence = string.Empty;

    public string PresetId { get; private set; } = string.Empty;

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
        get => _name;
        private set
        {
            if (SetProperty(ref _name, value))
            {
                OnPropertyChanged(nameof(StepTitle));
                OnPropertyChanged(nameof(DisplayText));
            }
        }
    }

    public string Sequence
    {
        get => _sequence;
        private set
        {
            if (SetProperty(ref _sequence, value))
            {
                OnPropertyChanged(nameof(DetailText));
            }
        }
    }

    public void UpdatePresetValues(string name, string sequence)
    {
        Name = name.Trim();
        Sequence = sequence.Trim();
    }

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

    private bool _canMoveEarlier;
    private bool _canMoveLater;

    public bool CanMoveEarlier
    {
        get => _canMoveEarlier;
        private set => SetProperty(ref _canMoveEarlier, value);
    }

    public bool CanMoveLater
    {
        get => _canMoveLater;
        private set => SetProperty(ref _canMoveLater, value);
    }

    public void UpdateMoveAvailability(int count)
    {
        CanMoveEarlier = Position > 1;
        CanMoveLater = Position < count;
    }

    public string StepTitle => IsCustom ? $"公共序列 · {Name}" : $"技能 {SkillKey}";

    public Visibility SequenceVisibility => IsCustom ? Visibility.Visible : Visibility.Collapsed;

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

    public static AutoBattleReleaseEditorItem CreateCustom(string name, string sequence, string presetId = "")
    {
        return new AutoBattleReleaseEditorItem
        {
            IsCustom = true,
            PresetId = presetId,
            SkillKey = string.Empty,
            Name = string.IsNullOrWhiteSpace(name) ? "自定义" : name.Trim(),
            Sequence = sequence.Trim()
        };
    }
}

internal sealed class AutoBattlePresetEditorItem : ObservableObject
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");

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

