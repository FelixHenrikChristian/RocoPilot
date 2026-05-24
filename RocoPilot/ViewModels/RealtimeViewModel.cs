using CommunityToolkit.Mvvm.ComponentModel;

using RocoPilot.Contracts.Services;
using RocoPilot.Contracts.Services.Encounters;
using RocoPilot.Contracts.Services.Spirits;
using RocoPilot.Models.Runtime;
using RocoPilot.Models.Spirits;

using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace RocoPilot.ViewModels;

public partial class RealtimeViewModel : ObservableRecipient
{
    private readonly IRuntimeTaskService _runtimeTaskService;
    private readonly IEncounterSeasonConfigService _encounterSeasonConfigService;
    private readonly ISpiritCatalogService _spiritCatalogService;
    private readonly DispatcherQueue? _dispatcherQueue;

    private bool _hasLoadedSettings;
    private bool _isApplyingSettings;
    private bool _isEncounterStatisticsEnabled = true;
    private bool _isSpiritCatalogSyncing;
    private string _spiritCatalogSummary = "图鉴数据待加载";
    private string _spiritCatalogSyncStatus = "可手动同步 wiki 图鉴";
    private bool _isAutoBattleEnabled;
    private string _autoBattleRoundOrder = AutoBattleSettings.DefaultRoundOrder;
    private string _autoBattleTurnSequence = AutoBattleSettings.DefaultTurnSequence;
    private List<AutoBattleReleaseStep> _autoBattleReleaseSequence = AutoBattleSettings.CreateDefaultReleaseSequence();
    private List<AutoBattleTurnSequencePreset> _autoBattleTurnSequencePresets = [];
    private AutoBattleEncounterRelievedActionOption? _selectedAutoBattleEncounterRelievedActionOption;

    public IReadOnlyList<AutoBattleEncounterRelievedActionOption> AutoBattleEncounterRelievedActionOptions
    {
        get;
    } = AutoBattleEncounterRelievedActionOption.CreateDefaultOptions();

    public bool IsEncounterStatisticsEnabled
    {
        get => _isEncounterStatisticsEnabled;
        set
        {
            if (SetProperty(ref _isEncounterStatisticsEnabled, value))
            {
                if (CanPersistSettings)
                {
                    _runtimeTaskService.SetEncounterStatisticsEnabled(value);
                }
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

    public string SpiritCatalogSummary
    {
        get => _spiritCatalogSummary;
        private set => SetProperty(ref _spiritCatalogSummary, value);
    }

    public string SpiritCatalogSyncStatus
    {
        get => _spiritCatalogSyncStatus;
        private set => SetProperty(ref _spiritCatalogSyncStatus, value);
    }

    public bool IsSpiritCatalogSyncing
    {
        get => _isSpiritCatalogSyncing;
        private set
        {
            if (SetProperty(ref _isSpiritCatalogSyncing, value))
            {
                OnPropertyChanged(nameof(CanSyncSpiritCatalog));
                OnPropertyChanged(nameof(SpiritCatalogSyncProgressVisibility));
            }
        }
    }

    public bool CanSyncSpiritCatalog => !IsSpiritCatalogSyncing;

    public Visibility SpiritCatalogSyncProgressVisibility => IsSpiritCatalogSyncing
        ? Visibility.Visible
        : Visibility.Collapsed;

    public bool IsAutoBattleEnabled
    {
        get => _isAutoBattleEnabled;
        set
        {
            if (SetProperty(ref _isAutoBattleEnabled, value))
            {
                SaveAutoBattleSettings();
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

    public string AutoBattleConfigurationSummary => BuildAutoBattleConfigurationSummary(
        _autoBattleReleaseSequence,
        SelectedAutoBattleEncounterRelievedAction);

    public AutoBattleEncounterRelievedActionOption? SelectedAutoBattleEncounterRelievedActionOption
    {
        get => _selectedAutoBattleEncounterRelievedActionOption;
        set
        {
            if (value is not null
                && SetProperty(ref _selectedAutoBattleEncounterRelievedActionOption, value))
            {
                SaveAutoBattleSettings();
                OnPropertyChanged(nameof(AutoBattleConfigurationSummary));
                OnPropertyChanged(nameof(AutoBattleEncounterRelievedActionDescription));
            }
        }
    }

    public string AutoBattleEncounterRelievedActionDescription =>
        SelectedAutoBattleEncounterRelievedActionOption?.Description ?? string.Empty;

    public AutoBattleSettings AutoBattleSettings => BuildAutoBattleSettings();

    private AutoBattleEncounterRelievedAction SelectedAutoBattleEncounterRelievedAction =>
        SelectedAutoBattleEncounterRelievedActionOption?.Action ?? AutoBattleEncounterRelievedAction.RecoverEnergy;

    public RealtimeViewModel(
        IRuntimeTaskService runtimeTaskService,
        IEncounterSeasonConfigService encounterSeasonConfigService,
        ISpiritCatalogService spiritCatalogService)
    {
        _runtimeTaskService = runtimeTaskService;
        _encounterSeasonConfigService = encounterSeasonConfigService;
        _spiritCatalogService = spiritCatalogService;
        _dispatcherQueue = DispatcherQueue.GetForCurrentThread();
        _runtimeTaskService.SettingsChanged += RuntimeTaskService_SettingsChanged;
        _isEncounterStatisticsEnabled = _runtimeTaskService.EncounterStatisticsEnabled;
        ApplyAutoBattleSettings(_runtimeTaskService.AutoBattleSettings);
    }

    public async Task LoadAsync()
    {
        await _runtimeTaskService.LoadSettingsAsync();
        ApplyRuntimeTaskSettings();
        _hasLoadedSettings = true;
        await LoadSpiritCatalogSummaryAsync();
    }

    public async Task SyncSpiritCatalogAsync()
    {
        if (IsSpiritCatalogSyncing)
        {
            return;
        }

        IsSpiritCatalogSyncing = true;
        SpiritCatalogSyncStatus = "正在同步 wiki 图鉴";
        try
        {
            var progress = new Progress<SpiritCatalogSyncProgress>(UpdateSpiritCatalogSyncProgress);
            var document = await _spiritCatalogService.SyncAsync(progress);
            ApplySpiritCatalogSummary(document);
            SpiritCatalogSyncStatus = $"同步完成：{document.Count} 个图鉴编号";
        }
        catch (Exception ex)
        {
            SpiritCatalogSyncStatus = $"同步失败：{ex.Message}";
        }
        finally
        {
            IsSpiritCatalogSyncing = false;
        }
    }

    private async Task LoadSpiritCatalogSummaryAsync()
    {
        try
        {
            var document = await _spiritCatalogService.LoadAsync();
            ApplySpiritCatalogSummary(document);
        }
        catch (Exception ex)
        {
            SpiritCatalogSummary = "图鉴数据加载失败";
            SpiritCatalogSyncStatus = ex.Message;
        }
    }

    private void ApplySpiritCatalogSummary(SpiritCatalogDocument document)
    {
        SpiritCatalogSummary = $"{document.Count} 个图鉴编号";
    }

    private void UpdateSpiritCatalogSyncProgress(SpiritCatalogSyncProgress progress)
    {
        SpiritCatalogSyncStatus = progress.Total > 0
            ? $"{progress.Message}：{progress.Completed}/{progress.Total}"
            : progress.Message;
    }

    private bool CanPersistSettings => _hasLoadedSettings && !_isApplyingSettings;

    private void ApplyRuntimeTaskSettings()
    {
        _isApplyingSettings = true;
        try
        {
            _isEncounterStatisticsEnabled = _runtimeTaskService.EncounterStatisticsEnabled;
            OnPropertyChanged(nameof(IsEncounterStatisticsEnabled));
            ApplyAutoBattleSettings(_runtimeTaskService.AutoBattleSettings);
        }
        finally
        {
            _isApplyingSettings = false;
        }
    }

    private void RuntimeTaskService_SettingsChanged(object? sender, EventArgs e)
    {
        if (_dispatcherQueue is null || _dispatcherQueue.HasThreadAccess)
        {
            ApplyRuntimeTaskSettings();
            return;
        }

        _dispatcherQueue.TryEnqueue(ApplyRuntimeTaskSettings);
    }

    private void ApplyAutoBattleSettings(AutoBattleSettings settings)
    {
        _isAutoBattleEnabled = settings.IsEnabled;
        _autoBattleRoundOrder = settings.RoundOrder;
        _autoBattleTurnSequence = settings.TurnSequence;
        _autoBattleReleaseSequence = (settings.ReleaseSequence ?? []).Select(step => step.Clone()).ToList();
        _autoBattleTurnSequencePresets = (settings.TurnSequencePresets ?? []).Select(preset => preset.Clone()).ToList();
        _selectedAutoBattleEncounterRelievedActionOption =
            FindAutoBattleEncounterRelievedActionOption(settings.EncounterRelievedAction);

        OnPropertyChanged(nameof(IsAutoBattleEnabled));
        OnPropertyChanged(nameof(AutoBattleRoundOrder));
        OnPropertyChanged(nameof(AutoBattleTurnSequence));
        OnPropertyChanged(nameof(SelectedAutoBattleEncounterRelievedActionOption));
        OnPropertyChanged(nameof(AutoBattleEncounterRelievedActionDescription));
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
        if (CanPersistSettings)
        {
            _runtimeTaskService.SetAutoBattleSettings(BuildAutoBattleSettings());
        }

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
            EncounterRelievedAction = SelectedAutoBattleEncounterRelievedAction
        };
    }

    private static string BuildAutoBattleConfigurationSummary(
        IReadOnlyList<AutoBattleReleaseStep> releaseSequence,
        AutoBattleEncounterRelievedAction encounterRelievedAction)
    {
        var encounterRelievedActionText = GetAutoBattleEncounterRelievedActionSummary(encounterRelievedAction);
        if (releaseSequence.Count == 0)
        {
            return $"未配置释放顺序 · {encounterRelievedActionText}";
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
        return $"{preview} · {suffix} · {encounterRelievedActionText}";
    }

    private AutoBattleEncounterRelievedActionOption FindAutoBattleEncounterRelievedActionOption(
        AutoBattleEncounterRelievedAction action)
    {
        return AutoBattleEncounterRelievedActionOptions.FirstOrDefault(option => option.Action == action)
            ?? AutoBattleEncounterRelievedActionOptions.First(option => option.Action == AutoBattleEncounterRelievedAction.RecoverEnergy);
    }

    private static string GetAutoBattleEncounterRelievedActionSummary(AutoBattleEncounterRelievedAction action)
    {
        return action switch
        {
            AutoBattleEncounterRelievedAction.NoAction => "奇遇解除后无操作",
            AutoBattleEncounterRelievedAction.RecoverEnergy => "奇遇解除后回能",
            AutoBattleEncounterRelievedAction.ReleaseSkill => "始终战技",
            AutoBattleEncounterRelievedAction.Capture => "奇遇解除后捕捉",
            _ => "奇遇解除后回能"
        };
    }
}

public sealed record AutoBattleEncounterRelievedActionOption(
    AutoBattleEncounterRelievedAction Action,
    string Name,
    string Description)
{
    public static IReadOnlyList<AutoBattleEncounterRelievedActionOption> CreateDefaultOptions()
    {
        return
        [
            new(
                AutoBattleEncounterRelievedAction.NoAction,
                "无操作",
                "识别到奇遇解除后不再按键，等待手动释放技能。"),
            new(
                AutoBattleEncounterRelievedAction.RecoverEnergy,
                "回能",
                "识别到奇遇解除后只按 X 回能，直到退出战斗。"),
            new(
                AutoBattleEncounterRelievedAction.ReleaseSkill,
                "战技",
                "不等待奇遇解除，始终按战斗配置释放技能。"),
            new(
                AutoBattleEncounterRelievedAction.Capture,
                "捕捉",
                "识别到奇遇解除后进入技能选择界面会依次按 W、1、Space。")
        ];
    }
}
