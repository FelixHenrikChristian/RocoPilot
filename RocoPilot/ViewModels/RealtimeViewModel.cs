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

        OnPropertyChanged(nameof(IsAutoBattleEnabled));
        OnPropertyChanged(nameof(AutoBattleRoundOrder));
        OnPropertyChanged(nameof(AutoBattleTurnSequence));
        OnPropertyChanged(nameof(AutoBattleStatus));
    }

    private void SaveAutoBattleSettings()
    {
        _runtimeTaskService.SetAutoBattleSettings(new AutoBattleSettings
        {
            IsEnabled = IsAutoBattleEnabled,
            RoundOrder = AutoBattleRoundOrder,
            TurnSequence = AutoBattleTurnSequence
        });
    }
}
