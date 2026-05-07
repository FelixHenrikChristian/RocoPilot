using CommunityToolkit.Mvvm.ComponentModel;

using RocoPilot.Contracts.Services;
using RocoPilot.Contracts.Services.Encounters;

namespace RocoPilot.ViewModels;

public partial class RealtimeViewModel : ObservableRecipient
{
    private readonly IRuntimeTaskService _runtimeTaskService;
    private readonly IEncounterSeasonConfigService _encounterSeasonConfigService;

    private bool _isEncounterStatisticsEnabled = true;

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

    public RealtimeViewModel(
        IRuntimeTaskService runtimeTaskService,
        IEncounterSeasonConfigService encounterSeasonConfigService)
    {
        _runtimeTaskService = runtimeTaskService;
        _encounterSeasonConfigService = encounterSeasonConfigService;
        _isEncounterStatisticsEnabled = _runtimeTaskService.EncounterStatisticsEnabled;
    }

    public async Task LoadAsync()
    {
        await _runtimeTaskService.LoadSettingsAsync();
        IsEncounterStatisticsEnabled = _runtimeTaskService.EncounterStatisticsEnabled;
    }
}
