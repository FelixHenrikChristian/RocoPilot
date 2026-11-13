using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;

using CommunityToolkit.Mvvm.ComponentModel;

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media.Imaging;

using RocoPilot.Contracts.Services;
using RocoPilot.Contracts.Services.Spirits;
using RocoPilot.Models.Runtime;

namespace RocoPilot.Views.Windows.AutoBattleConfigPages;

internal sealed class AutoBattleConfigEditor : ObservableObject
{
    private const string SkillPlaceholder = "{skill}";

    private readonly IKeyboardInputService _keyboardInputService;
    private readonly ISpiritCatalogService _spiritCatalogService;
    private readonly HashSet<AutoBattlePresetEditorItem> _observedPresets = [];

    internal IKeyboardInputService KeyboardInputService => _keyboardInputService;

    /// <summary>三种战斗类型各管各的配置，互不干扰。普通战斗无出招队列，跳过步骤仅展示不执行。</summary>
    public AutoBattleReleaseConfigSet NormalConfigs { get; } = new(supportsSkipTurn: false);

    public AutoBattleReleaseConfigSet FlowerSeedConfigs { get; } = new(supportsSkipTurn: true);

    public AutoBattleReleaseConfigSet LegendaryConfigs { get; } = new(supportsSkipTurn: true);

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
    private bool _capturePolicyEnabled;
    private bool _captureOnlyListedSpirits = true;

    /// <summary>奇遇捕捉名单（白名单/黑名单）。</summary>
    public ObservableCollection<EncounterCaptureRuleEditorItem> CaptureRuleItems
    {
        get;
    } = [];

    public bool CapturePolicyEnabled
    {
        get => _capturePolicyEnabled;
        set
        {
            if (SetProperty(ref _capturePolicyEnabled, value))
            {
                OnPropertyChanged(nameof(CapturePolicyOptionsEnabled));
                OnPropertyChanged(nameof(CapturePolicySummary));
            }
        }
    }

    public bool CaptureOnlyListedSpirits
    {
        get => _captureOnlyListedSpirits;
        set
        {
            if (SetProperty(ref _captureOnlyListedSpirits, value))
            {
                OnPropertyChanged(nameof(CapturePolicySummary));
            }
        }
    }

    public bool CapturePolicyOptionsEnabled => CapturePolicyEnabled;

    public string CapturePolicySummary
    {
        get
        {
            if (!CapturePolicyEnabled)
            {
                return "已关闭：奇遇解除捕捉不按精灵名单筛选";
            }

            var captures = CaptureRuleItems.Count(item => !item.IsDefeat);
            var defeats = CaptureRuleItems.Count(item => item.IsDefeat);
            var capturePart = CaptureOnlyListedSpirits
                ? $"仅捕捉白名单 {captures} 个精灵"
                : $"白名单 {captures} 个精灵优先捕捉";
            return defeats > 0
                ? $"{capturePart}，黑名单 {defeats} 个直接击败，其余{(CaptureOnlyListedSpirits ? "不捕捉" : "按血脉筛选")}"
                : $"{capturePart}，其余{(CaptureOnlyListedSpirits ? "不捕捉" : "按血脉筛选")}";
        }
    }

    /// <summary>添加名单条目；重名时不重复添加。返回是否添加成功。</summary>
    public bool AddCaptureRule(string spiritName, bool isDefeat = false)
    {
        var name = spiritName.Trim();
        if (name.Length == 0) return false;
        if (CaptureRuleItems.Any(item => string.Equals(item.SpiritName, name, StringComparison.OrdinalIgnoreCase))) return false;
        var item = new EncounterCaptureRuleEditorItem
        {
            SpiritName = name,
            IsDefeat = isDefeat
        };
        CaptureRuleItems.Add(item);
        _ = LoadCaptureRuleAvatarsAsync([item]);
        OnPropertyChanged(nameof(CapturePolicySummary));
        return true;
    }

    public void RemoveCaptureRule(EncounterCaptureRuleEditorItem item)
    {
        if (CaptureRuleItems.Remove(item))
        {
            OnPropertyChanged(nameof(CapturePolicySummary));
        }
    }

    /// <summary>搜索图鉴精灵名，供联想下拉使用。</summary>
    public async Task<IReadOnlyList<SpiritSearchSuggestionItem>> SearchSpiritsAsync(
        string text, CancellationToken cancellationToken = default)
    {
        if (_spiritCatalogService is null || string.IsNullOrWhiteSpace(text)) return [];
        try
        {
            var keyword = text.Trim();
            var document = await _spiritCatalogService.LoadAsync(cancellationToken);
            return document.Spirits
                .Where(item => !string.IsNullOrWhiteSpace(item.Name)
                    && (item.Name.Contains(keyword, StringComparison.OrdinalIgnoreCase)
                        || (!string.IsNullOrWhiteSpace(item.BaseName)
                            && item.BaseName.Contains(keyword, StringComparison.OrdinalIgnoreCase))))
                .DistinctBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
                .Take(12)
                .Select(item => new SpiritSearchSuggestionItem
                {
                    Name = item.Name,
                    AvatarPath = _spiritCatalogService.ResolveAvatarPath(item.AvatarPath) ?? string.Empty
                })
                .ToList();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return [];
        }
    }

    /// <summary>异步解析名单条目的图鉴头像并回填。</summary>
    private async Task LoadCaptureRuleAvatarsAsync(IReadOnlyList<EncounterCaptureRuleEditorItem> items)
    {
        if (_spiritCatalogService is null || items.Count == 0) return;
        try
        {
            var document = await _spiritCatalogService.LoadAsync();
            foreach (var item in items)
            {
                var match = document.Spirits.FirstOrDefault(candidate =>
                        string.Equals(candidate.Name, item.SpiritName, StringComparison.OrdinalIgnoreCase))
                    ?? document.Spirits.FirstOrDefault(candidate =>
                        string.Equals(candidate.BaseName, item.SpiritName, StringComparison.OrdinalIgnoreCase));
                item.AvatarPath = match is null
                    ? string.Empty
                    : _spiritCatalogService.ResolveAvatarPath(match.AvatarPath) ?? string.Empty;
            }
        }
        catch
        {
            // 头像加载失败不影响名单编辑。
        }
    }

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
        IKeyboardInputService keyboardInputService,
        ISpiritCatalogService? spiritCatalogService = null)
    {
        _keyboardInputService = keyboardInputService;
        _spiritCatalogService = spiritCatalogService ?? App.GetService<ISpiritCatalogService>();

        SharedPresetItems.CollectionChanged += SharedPresetItems_CollectionChanged;

        LoadSettings(AutoBattleSettingsRules.Normalize(settings));
        // 头像异步填充，避免 UI 线程死锁。
        _ = LoadCaptureRuleAvatarsAsync(CaptureRuleItems.ToArray());
    }

    public void AddSharedPreset()
    {
        SharedPresetItems.Add(new AutoBattlePresetEditorItem
        {
            Name = $"序列 {SharedPresetItems.Count + 1}",
            Sequence = "1"
        });
    }

    /// <summary>导入的配置加入指定战斗类型的列表并选中；名称为空时取默认「配置x」。</summary>
    public AutoBattleReleaseConfigEditorItem AddImportedConfig(
        AutoBattleReleaseConfigSet set,
        AutoBattleReleaseConfig config)
    {
        var item = set.AddConfig(string.IsNullOrWhiteSpace(config.Name) ? null : config.Name);
        item.Sequence.Items.Clear();
        foreach (var step in config.Steps)
        {
            item.Sequence.Items.Add(CreateReleaseEditorItem(
                step,
                AutoBattleSettings.DefaultTurnSequence,
                set.SupportsSkipTurn));
        }

        return item;
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

        if (!TryBuildReleaseConfigs(
                NormalConfigs,
                "普通战斗",
                AutoBattleConfigSection.Normal,
                out var normalConfigs,
                out var normalSelectedId,
                out var releaseSequence,
                out error))
        {
            return false;
        }

        if (!TryBuildReleaseConfigs(
                FlowerSeedConfigs,
                "花种战斗",
                AutoBattleConfigSection.FlowerSeed,
                out var flowerSeedConfigs,
                out var flowerSeedSelectedId,
                out var flowerSeedReleaseSequence,
                out error))
        {
            return false;
        }

        if (!TryBuildReleaseConfigs(
                LegendaryConfigs,
                "传说精灵战斗",
                AutoBattleConfigSection.Legendary,
                out var legendaryConfigs,
                out var legendarySelectedId,
                out var legendaryReleaseSequence,
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
        settings.LegendaryReleaseSequence = legendaryReleaseSequence;
        settings.NormalReleaseConfigs = normalConfigs;
        settings.SelectedNormalReleaseConfigId = normalSelectedId;
        settings.FlowerSeedReleaseConfigs = flowerSeedConfigs;
        settings.SelectedFlowerSeedReleaseConfigId = flowerSeedSelectedId;
        settings.LegendaryReleaseConfigs = legendaryConfigs;
        settings.SelectedLegendaryReleaseConfigId = legendarySelectedId;
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
        settings.EncounterCapturePolicy = new EncounterCapturePolicySettings
        {
            IsEnabled = CapturePolicyEnabled,
            OnlyListedSpirits = CaptureOnlyListedSpirits,
            Rules = CaptureRuleItems
                .Where(item => !string.IsNullOrWhiteSpace(item.SpiritName))
                .Select(item => new EncounterCaptureRuleSettings
                {
                    SpiritName = item.SpiritName.Trim(),
                    Action = item.Action
                })
                .ToList()
        };
        error = default;
        return true;
    }

    private void LoadSettings(AutoBattleSettings settings)
    {
        LoadReleaseConfigs(
            settings.NormalReleaseConfigs,
            settings.SelectedNormalReleaseConfigId,
            NormalConfigs,
            settings.TurnSequence);
        LoadReleaseConfigs(
            settings.FlowerSeedReleaseConfigs,
            settings.SelectedFlowerSeedReleaseConfigId,
            FlowerSeedConfigs,
            AutoBattleSettings.DefaultTurnSequence);
        LoadReleaseConfigs(
            settings.LegendaryReleaseConfigs,
            settings.SelectedLegendaryReleaseConfigId,
            LegendaryConfigs,
            AutoBattleSettings.DefaultTurnSequence);

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
        var capturePolicy = settings.EncounterCapturePolicy ?? EncounterCapturePolicySettings.CreateDefault();
        CapturePolicyEnabled = capturePolicy.IsEnabled;
        CaptureOnlyListedSpirits = capturePolicy.OnlyListedSpirits;
        foreach (var rule in capturePolicy.Rules)
        {
            CaptureRuleItems.Add(new EncounterCaptureRuleEditorItem
            {
                SpiritName = rule.SpiritName,
                IsDefeat = rule.Action == EncounterCaptureRuleAction.Defeat
            });
        }
    }

    private static void LoadReleaseConfigs(
        IEnumerable<AutoBattleReleaseConfig> configs,
        string selectedId,
        AutoBattleReleaseConfigSet set,
        string turnSequence)
    {
        foreach (var config in configs)
        {
            var item = new AutoBattleReleaseConfigEditorItem(set.SupportsSkipTurn)
            {
                Id = config.Id,
                Name = config.Name
            };
            foreach (var step in config.Steps)
            {
                item.Sequence.Items.Add(CreateReleaseEditorItem(step, turnSequence, set.SupportsSkipTurn));
            }

            set.Items.Add(item);
        }

        set.Selected = set.Items.FirstOrDefault(item => item.Id == selectedId) ?? set.Items[0];
    }

    /// <summary>构建某战斗类型的配置列表；返回选中配置的步骤作为该类型的运行时扁平序列。</summary>
    private bool TryBuildReleaseConfigs(
        AutoBattleReleaseConfigSet set,
        string battleTypeName,
        AutoBattleConfigSection section,
        out List<AutoBattleReleaseConfig> configs,
        out string selectedConfigId,
        out List<AutoBattleReleaseStep> selectedSteps,
        out AutoBattleConfigValidationError error)
    {
        configs = [];
        selectedSteps = [];
        selectedConfigId = string.Empty;
        if (set.Items.Count == 0)
        {
            error = new AutoBattleConfigValidationError(
                "配置为空",
                $"请为{battleTypeName}至少保留一个配置。",
                section);
            return false;
        }

        foreach (var item in set.Items)
        {
            var name = item.Name.Trim();
            if (string.IsNullOrWhiteSpace(name))
            {
                error = new AutoBattleConfigValidationError(
                    "配置名称为空",
                    $"请为{battleTypeName}的配置填写名称。",
                    section);
                return false;
            }

            if (!item.Sequence.Items.Any(step => section != AutoBattleConfigSection.Normal || !step.IsSkipStep))
            {
                error = new AutoBattleConfigValidationError(
                    "释放顺序为空",
                    $"请为{battleTypeName}的配置「{name}」至少保留一个可执行的释放动作。",
                    section);
                return false;
            }

            if (!TryBuildReleaseSequence(
                    item.Sequence.Items,
                    battleTypeName,
                    section,
                    out var steps,
                    out error))
            {
                return false;
            }

            configs.Add(new AutoBattleReleaseConfig
            {
                Id = item.Id,
                Name = name,
                Steps = steps
            });
        }

        var selected = set.Selected ?? set.Items[0];
        // out 参数不能被 lambda 捕获，先拷贝到局部变量。
        var selectedId = selected.Id;
        selectedConfigId = selectedId;
        selectedSteps = configs
            .First(config => config.Id == selectedId)
            .Steps.Select(step => step.Clone())
            .ToList();
        error = default;
        return true;
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

        foreach (var set in new[] { NormalConfigs, FlowerSeedConfigs, LegendaryConfigs })
        {
            foreach (var item in set.Items)
            {
                item.Sequence.UpdatePreset(preset);
            }
        }
    }

    private static AutoBattleReleaseEditorItem CreateReleaseEditorItem(
        AutoBattleReleaseStep step,
        string turnSequence,
        bool supportsSkipTurn)
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

        return AutoBattleReleaseEditorItem.CreateSkill(
            skillKey,
            skipIgnored: !supportsSkipTurn && skillKey == RocoPilot.Core.Battle.AutoBattleSettingsRules.SkipTurnSkillKey);
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
    private readonly bool _supportsSkipTurn;

    public ObservableCollection<AutoBattleReleaseEditorItem> Items { get; } = [];

    public Visibility EmptyVisibility => Items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

    public string Summary => Items.Count == 0
        ? "未配置释放顺序"
        : $"{string.Join(" → ", Items.Take(8).Select(item => item.DisplayText))} · "
            + (Items.Count > 8 ? $"等 {Items.Count} 步" : $"{Items.Count} 步");

    /// <param name="supportsSkipTurn">普通战斗传 false：跳过步骤仅展示并标注执行时忽略。</param>
    public AutoBattleReleaseSequenceEditor(bool supportsSkipTurn)
    {
        _supportsSkipTurn = supportsSkipTurn;
        Items.CollectionChanged += Items_CollectionChanged;
    }

    public void AppendSkill(string? skillKey)
    {
        if (AutoBattleSettingsRules.NormalizeSkillKey(skillKey) is { } key)
        {
            Items.Add(AutoBattleReleaseEditorItem.CreateSkill(
                key,
                skipIgnored: !_supportsSkipTurn
                    && key == RocoPilot.Core.Battle.AutoBattleSettingsRules.SkipTurnSkillKey));
        }
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
        }

        OnPropertyChanged(nameof(EmptyVisibility));
        OnPropertyChanged(nameof(Summary));
    }
}

internal enum AutoBattleConfigSection
{
    Normal,
    FlowerSeed,
    Legendary,
    SharedSequences,
    BloodlineCapture,
    SpiritCapturePolicy
}

/// <summary>奇遇捕捉名单条目的编辑草稿；动作区分白名单（捕捉）与黑名单（击败）。</summary>
internal sealed class EncounterCaptureRuleEditorItem : ObservableObject
{
    private string _spiritName = string.Empty;
    private bool _isDefeat;
    private string _avatarPath = string.Empty;

    public string SpiritName
    {
        get => _spiritName;
        set => SetProperty(ref _spiritName, value);
    }

    /// <summary>true=黑名单（直接击败）；false=白名单（捕捉）。</summary>
    public bool IsDefeat
    {
        get => _isDefeat;
        set
        {
            if (SetProperty(ref _isDefeat, value))
            {
                OnPropertyChanged(nameof(ActionDisplay));
            }
        }
    }

    public EncounterCaptureRuleAction Action => IsDefeat ? EncounterCaptureRuleAction.Defeat : EncounterCaptureRuleAction.Capture;

    public string ActionDisplay => IsDefeat ? "击败" : "捕捉";

    /// <summary>图鉴头像本地路径。</summary>
    public string AvatarPath
    {
        get => _avatarPath;
        set
        {
            if (SetProperty(ref _avatarPath, value))
            {
                OnPropertyChanged(nameof(AvatarImage));
            }
        }
    }

    public BitmapImage? AvatarImage => CreateAvatarImage(AvatarPath);

    private static BitmapImage? CreateAvatarImage(string? path)
    {
        return string.IsNullOrWhiteSpace(path) || !File.Exists(path)
            ? null
            : new BitmapImage(new Uri(path, UriKind.Absolute));
    }
}

/// <summary>精灵搜索联想条目。</summary>
internal sealed class SpiritSearchSuggestionItem
{
    public string Name
    {
        get;
        init;
    } = string.Empty;

    public string AvatarPath
    {
        get;
        init;
    } = string.Empty;

    public BitmapImage? AvatarImage => string.IsNullOrWhiteSpace(AvatarPath) || !File.Exists(AvatarPath)
        ? null
        : new BitmapImage(new Uri(AvatarPath, UriKind.Absolute));

    public override string ToString() => Name;
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

    /// <summary>是否为跳过回合步骤（技能键 S）。</summary>
    public bool IsSkipStep => !IsCustom
        && SkillKey == RocoPilot.Core.Battle.AutoBattleSettingsRules.SkipTurnSkillKey;

    /// <summary>普通战斗上下文中该跳过步骤仅展示、不执行。</summary>
    public bool SkipIgnored
    {
        get;
        private set;
    }

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

    public string StepTitle => IsCustom
        ? $"公共序列 · {Name}"
        : SkillKey == RocoPilot.Core.Battle.AutoBattleSettingsRules.SkipTurnSkillKey
            ? SkipIgnored ? "跳过回合（普通战斗忽略）" : "跳过回合"
            : $"技能 {SkillKey}";

    /// <summary>普通战斗中被忽略的跳过步骤整卡置灰。</summary>
    public double CardOpacity => SkipIgnored ? 0.45 : 1.0;

    public Visibility SequenceVisibility => IsCustom ? Visibility.Visible : Visibility.Collapsed;

    public string PositionText => $"#{Position}";

    public string DisplayText => IsCustom ? Name : SkillKey;

    public string DetailText => IsCustom
        ? Sequence
        : SkillKey == RocoPilot.Core.Battle.AutoBattleSettingsRules.SkipTurnSkillKey
            ? SkipIgnored
                ? "普通战斗无出招队列，执行时自动跳过"
                : "等待轮到本玩家（P 角标）后继续"
            : "技能键";

    public static AutoBattleReleaseEditorItem CreateSkill(string skillKey, bool skipIgnored = false)
    {
        return new AutoBattleReleaseEditorItem
        {
            IsCustom = false,
            SkillKey = skillKey,
            Name = skillKey,
            Sequence = string.Empty,
            SkipIgnored = skipIgnored
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

/// <summary>一个命名战斗配置的编辑草稿；各类型配置独立，切换配置不丢失未保存修改。</summary>
internal sealed class AutoBattleReleaseConfigEditorItem : ObservableObject
{
    private string _name = string.Empty;

    public string Id { get; init; } = Guid.NewGuid().ToString("N");

    public string Name
    {
        get => _name;
        set => SetProperty(ref _name, value);
    }

    public AutoBattleReleaseSequenceEditor Sequence { get; }

    public AutoBattleReleaseConfigEditorItem(bool supportsSkipTurn)
    {
        Sequence = new AutoBattleReleaseSequenceEditor(supportsSkipTurn);
    }
}

/// <summary>某战斗类型下的配置集合，与其他类型互不干扰；选中配置即该类型运行时应用的配置。</summary>
internal sealed class AutoBattleReleaseConfigSet : ObservableObject
{
    private readonly bool _supportsSkipTurn;
    private AutoBattleReleaseConfigEditorItem _selected = null!;

    public ObservableCollection<AutoBattleReleaseConfigEditorItem> Items { get; } = [];

    public AutoBattleReleaseConfigEditorItem Selected
    {
        get => _selected;
        set
        {
            // ComboBox 的 SelectedItem 在列表变化时会短暂回写 null，忽略以避免绑定链路出现空引用。
            if (value is null || SetProperty(ref _selected, value))
            {
                OnPropertyChanged(nameof(SelectedSequence));
            }
        }
    }

    public AutoBattleReleaseSequenceEditor SelectedSequence => Selected.Sequence;

    public bool SupportsSkipTurn => _supportsSkipTurn;

    /// <summary>至少保留一个配置，否则不允许删除。</summary>
    public bool CanRemove => Items.Count > 1;

    public AutoBattleReleaseConfigSet(bool supportsSkipTurn)
    {
        _supportsSkipTurn = supportsSkipTurn;
        Items.CollectionChanged += (_, _) => OnPropertyChanged(nameof(CanRemove));
    }

    public AutoBattleReleaseConfigEditorItem AddConfig(string? name = null)
    {
        var item = new AutoBattleReleaseConfigEditorItem(_supportsSkipTurn)
        {
            Name = string.IsNullOrWhiteSpace(name)
                ? AutoBattleSettingsRules.CreateDefaultConfigName(Items.Select(existing => existing.Name))
                : name.Trim()
        };
        item.Sequence.AppendSkill("1");
        Items.Add(item);
        Selected = item;
        return item;
    }

    public void RemoveConfig(AutoBattleReleaseConfigEditorItem item)
    {
        var index = Items.IndexOf(item);
        if (index < 0 || Items.Count <= 1)
        {
            return;
        }

        // 先把选中项切走再删除：删除触发 ComboBox 更新时会将 SelectedItem 回写 null，
        // 若此时被删项仍是选中项，绑定链路会出现空引用导致崩溃。
        if (ReferenceEquals(Selected, item))
        {
            Selected = Items[index + 1 < Items.Count ? index + 1 : index - 1];
        }

        Items.RemoveAt(index);
    }
}

