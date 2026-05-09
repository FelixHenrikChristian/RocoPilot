using CommunityToolkit.Mvvm.ComponentModel;

using RocoPilot.Contracts.Services;
using RocoPilot.Contracts.Services.Encounters;
using RocoPilot.Models.Runtime;

namespace RocoPilot.ViewModels;

public partial class RealtimeViewModel : ObservableRecipient
{
    private readonly IRuntimeTaskService _runtimeTaskService;
    private readonly IEncounterSeasonConfigService _encounterSeasonConfigService;

    private bool _isEncounterStatisticsEnabled = true;
    private bool _isAutoBattleEnabled;
    private string _autoBattleRoundOrder = AutoBattleSettings.DefaultRoundOrder;
    private string _autoBattleTurnSequence = AutoBattleSettings.DefaultTurnSequence;
    private List<AutoBattleReleaseStep> _autoBattleReleaseSequence = AutoBattleSettings.CreateDefaultReleaseSequence();
    private List<AutoBattleTurnSequencePreset> _autoBattleTurnSequencePresets = [];
    private bool _isAutoBattleOnlyRecoverEnergyAfterEncounterRelieved;

    public bool IsEncounterStatisticsEnabled
    {
        get => _isEncounterStatisticsEnabled;
        set
        {
            if (SetProperty(ref _isEncounterStatisticsEnabled, value))
            {
                _runtimeTaskService.SetEncounterStatisticsEnabled(value);
                OnPropertyChanged(nameof(EncounterStatisticsStatus));
            }
        }
    }

    public string CurrentEncounterSeasonDisplay
    {
        get
        {
            var season = _encounterSeasonConfigService.GetCurrentSeason();
            if (season is null)
            {
                return "未配置赛季奇遇";
            }

            var parts = new[]
            {
                season.Id,
                season.EncounterTypeName,
                season.DateRange
            }.Where(part => !string.IsNullOrWhiteSpace(part));

            return string.Join(" · ", parts);
        }
    }

    public string EncounterStatisticsStatus => IsEncounterStatisticsEnabled
        ? "已开启"
        : "已关闭";

    public bool IsAutoBattleEnabled
    {
        get => _isAutoBattleEnabled;
        set
        {
            if (SetProperty(ref _isAutoBattleEnabled, value))
            {
                SaveAutoBattleSettings();
                OnPropertyChanged(nameof(AutoBattleStatus));
            }
        }
    }

    public string AutoBattleRoundOrder
    {
        get => _autoBattleRoundOrder;
        set
        {
            if (SetProperty(ref _autoBattleRoundOrder, value))
            {
                SaveAutoBattleSettings();
            }
        }
    }

    public string AutoBattleTurnSequence
    {
        get => _autoBattleTurnSequence;
        set
        {
            if (SetProperty(ref _autoBattleTurnSequence, value))
            {
                SaveAutoBattleSettings();
            }
        }
    }

    public string AutoBattleStatus => IsAutoBattleEnabled
        ? "已开启"
        : "已关闭";

    public string AutoBattleConfigurationSummary => BuildAutoBattleConfigurationSummary(
        _autoBattleReleaseSequence,
        _isAutoBattleOnlyRecoverEnergyAfterEncounterRelieved);

    public bool IsAutoBattleOnlyRecoverEnergyAfterEncounterRelieved
    {
        get => _isAutoBattleOnlyRecoverEnergyAfterEncounterRelieved;
        set
        {
            if (SetProperty(ref _isAutoBattleOnlyRecoverEnergyAfterEncounterRelieved, value))
            {
                SaveAutoBattleSettings();
                OnPropertyChanged(nameof(AutoBattleConfigurationSummary));
            }
        }
    }

    public AutoBattleSettings AutoBattleSettings => BuildAutoBattleSettings();

    public RealtimeViewModel(
        IRuntimeTaskService runtimeTaskService,
        IEncounterSeasonConfigService encounterSeasonConfigService)
    {
        _runtimeTaskService = runtimeTaskService;
        _encounterSeasonConfigService = encounterSeasonConfigService;
        _isEncounterStatisticsEnabled = _runtimeTaskService.EncounterStatisticsEnabled;
        ApplyAutoBattleSettings(_runtimeTaskService.AutoBattleSettings);
    }

    public async Task LoadAsync()
    {
        await _runtimeTaskService.LoadSettingsAsync();
        IsEncounterStatisticsEnabled = _runtimeTaskService.EncounterStatisticsEnabled;
        ApplyAutoBattleSettings(_runtimeTaskService.AutoBattleSettings);
    }

    private void ApplyAutoBattleSettings(AutoBattleSettings settings)
    {
        _isAutoBattleEnabled = settings.IsEnabled;
        _autoBattleRoundOrder = settings.RoundOrder;
        _autoBattleTurnSequence = settings.TurnSequence;
        _autoBattleReleaseSequence = (settings.ReleaseSequence ?? []).Select(step => step.Clone()).ToList();
        _autoBattleTurnSequencePresets = (settings.TurnSequencePresets ?? []).Select(preset => preset.Clone()).ToList();
        _isAutoBattleOnlyRecoverEnergyAfterEncounterRelieved = settings.OnlyRecoverEnergyAfterEncounterRelieved;

        OnPropertyChanged(nameof(IsAutoBattleEnabled));
        OnPropertyChanged(nameof(AutoBattleRoundOrder));
        OnPropertyChanged(nameof(AutoBattleTurnSequence));
        OnPropertyChanged(nameof(IsAutoBattleOnlyRecoverEnergyAfterEncounterRelieved));
        OnPropertyChanged(nameof(AutoBattleStatus));
        OnPropertyChanged(nameof(AutoBattleConfigurationSummary));
        OnPropertyChanged(nameof(AutoBattleSettings));
    }

    public void UpdateAutoBattleSettings(AutoBattleSettings settings)
    {
        ApplyAutoBattleSettings(settings.Clone());
        SaveAutoBattleSettings();
    }

    private void SaveAutoBattleSettings()
    {
        _runtimeTaskService.SetAutoBattleSettings(BuildAutoBattleSettings());
        OnPropertyChanged(nameof(AutoBattleConfigurationSummary));
        OnPropertyChanged(nameof(AutoBattleSettings));
    }

    private AutoBattleSettings BuildAutoBattleSettings()
    {
        return new AutoBattleSettings
        {
            IsEnabled = IsAutoBattleEnabled,
            RoundOrder = AutoBattleRoundOrder,
            TurnSequence = AutoBattleTurnSequence,
            ReleaseSequence = _autoBattleReleaseSequence.Select(step => step.Clone()).ToList(),
            TurnSequencePresets = _autoBattleTurnSequencePresets.Select(preset => preset.Clone()).ToList(),
            OnlyRecoverEnergyAfterEncounterRelieved = IsAutoBattleOnlyRecoverEnergyAfterEncounterRelieved
        };
    }

    private static string BuildAutoBattleConfigurationSummary(
        IReadOnlyList<AutoBattleReleaseStep> releaseSequence,
        bool onlyRecoverEnergyAfterEncounterRelieved)
    {
        if (releaseSequence.Count == 0)
        {
            return onlyRecoverEnergyAfterEncounterRelieved
                ? "未配置释放顺序 · 奇遇解除后仅回能"
                : "未配置释放顺序";
        }

        var previewItems = releaseSequence
            .Take(6)
            .Select(step => step.IsCustom
                ? $"[{(string.IsNullOrWhiteSpace(step.Name) ? "自定义" : step.Name)}]"
                : step.SkillKey);
        var preview = string.Join(" → ", previewItems);
        var suffix = releaseSequence.Count > 6
            ? $"等 {releaseSequence.Count} 步"
            : $"{releaseSequence.Count} 步";
        var encounterEnergyRecoveryText = onlyRecoverEnergyAfterEncounterRelieved
            ? " · 奇遇解除后仅回能"
            : string.Empty;
        return $"{preview} · {suffix}{encounterEnergyRecoveryText}";
    }
}
