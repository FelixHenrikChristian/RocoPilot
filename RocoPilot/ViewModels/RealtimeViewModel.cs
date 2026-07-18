using CommunityToolkit.Mvvm.ComponentModel;

using RocoPilot.Configuration;
using RocoPilot.Contracts.Services;
using RocoPilot.Contracts.Services.Encounters;
using RocoPilot.Contracts.Services.Spirits;
using RocoPilot.Models.Input;
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
    private readonly ILocalSettingsService _localSettingsService;
    private readonly DispatcherQueue? _dispatcherQueue;

    private bool _hasLoadedSettings;
    private bool _isApplyingSettings;
    private bool _isEncounterStatisticsEnabled = true;
    private bool _isSpiritCatalogSyncing;
    private SpiritCatalogSourceOption? _selectedSpiritCatalogSource;
    private string _spiritCatalogSourceSummary = string.Empty;
    private string _spiritCatalogSummary = "图鉴数据待加载";
    private string _spiritCatalogSyncStatus = "可手动同步 wiki 图鉴";
    private bool _isAutoBattleEnabled;
    private string _autoBattleRoundOrder = AutoBattleSettings.DefaultRoundOrder;
    private string _autoBattleTurnSequence = AutoBattleSettings.DefaultTurnSequence;
    private string _autoBattleBossComboSequence = AutoBattleSettings.DefaultBossComboSequence;
    private List<AutoBattleReleaseStep> _autoBattleReleaseSequence = AutoBattleSettings.CreateDefaultReleaseSequence();
    private List<AutoBattleTurnSequencePreset> _autoBattleTurnSequencePresets = [];
    private AutoBattleEncounterRelievedActionOption? _selectedAutoBattleEncounterRelievedActionOption;
    private AutoBattleKeyboardInputMethodOption? _selectedAutoBattleKeyboardInputMethodOption;
    private int _autoBattleSkillSelectionActionDelayMs = AutoBattleSettings.DefaultSkillSelectionActionDelayMs;
    private int _autoBattleSkillSelectionRetryDelayMs = AutoBattleSettings.DefaultSkillSelectionRetryDelayMs;
    private int _autoBattleKeyboardHoldDurationMs = AutoBattleSettings.DefaultKeyboardHoldDurationMs;
    private int _autoBattleKeyboardIntervalMs = AutoBattleSettings.DefaultKeyboardIntervalMs;
    private int _autoBattleCaptureKeyboardIntervalMs = AutoBattleSettings.DefaultCaptureKeyboardIntervalMs;

    public IReadOnlyList<AutoBattleEncounterRelievedActionOption> AutoBattleEncounterRelievedActionOptions
    {
        get;
    } = AutoBattleEncounterRelievedActionOption.CreateDefaultOptions();

    public IReadOnlyList<AutoBattleKeyboardInputMethodOption> AutoBattleKeyboardInputMethodOptions
    {
        get;
    } = AutoBattleKeyboardInputMethodOption.CreateDefaultOptions();

    public IReadOnlyList<SpiritCatalogSourceOption> SpiritCatalogSources
    {
        get;
    }

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

    public string SpiritCatalogSourceSummary
    {
        get => _spiritCatalogSourceSummary;
        private set => SetProperty(ref _spiritCatalogSourceSummary, value);
    }

    public string SpiritCatalogSyncStatus
    {
        get => _spiritCatalogSyncStatus;
        private set => SetProperty(ref _spiritCatalogSyncStatus, value);
    }

    public SpiritCatalogSourceOption? SelectedSpiritCatalogSource
    {
        get => _selectedSpiritCatalogSource;
        set => ApplySelectedSpiritCatalogSource(value, reloadSummary: true);
    }

    public string SelectedSpiritCatalogSourceId => SelectedSpiritCatalogSource?.Id ?? string.Empty;

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
        _autoBattleBossComboSequence,
        SelectedAutoBattleEncounterRelievedAction,
        SelectedAutoBattleKeyboardInputMethod);

    public string AutoBattleOtherConfigurationSummary =>
        "包含高级时序选项；如无明确需求，建议保持默认设置。";

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

    public AutoBattleKeyboardInputMethodOption? SelectedAutoBattleKeyboardInputMethodOption
    {
        get => _selectedAutoBattleKeyboardInputMethodOption;
        set
        {
            if (value is not null
                && SetProperty(ref _selectedAutoBattleKeyboardInputMethodOption, value))
            {
                SaveAutoBattleSettings();
                OnPropertyChanged(nameof(AutoBattleConfigurationSummary));
                OnPropertyChanged(nameof(AutoBattleKeyboardInputMethodDescription));
            }
        }
    }

    public string AutoBattleKeyboardInputMethodDescription =>
        SelectedAutoBattleKeyboardInputMethodOption?.Description ?? string.Empty;

    public AutoBattleSettings AutoBattleSettings => BuildAutoBattleSettings();

    private AutoBattleEncounterRelievedAction SelectedAutoBattleEncounterRelievedAction =>
        SelectedAutoBattleEncounterRelievedActionOption?.Action ?? AutoBattleEncounterRelievedAction.RecoverEnergy;

    private KeyboardInputMethod SelectedAutoBattleKeyboardInputMethod =>
        SelectedAutoBattleKeyboardInputMethodOption?.Method ?? KeyboardInputMethod.PostMessage;

    public RealtimeViewModel(
        IRuntimeTaskService runtimeTaskService,
        IEncounterSeasonConfigService encounterSeasonConfigService,
        ISpiritCatalogService spiritCatalogService,
        ILocalSettingsService localSettingsService)
    {
        _runtimeTaskService = runtimeTaskService;
        _encounterSeasonConfigService = encounterSeasonConfigService;
        _spiritCatalogService = spiritCatalogService;
        _localSettingsService = localSettingsService;
        SpiritCatalogSources = _spiritCatalogService.GetSources();
        _selectedSpiritCatalogSource = SpiritCatalogSources.FirstOrDefault();
        if (_selectedSpiritCatalogSource is not null)
        {
            _spiritCatalogSourceSummary = $"当前：{_selectedSpiritCatalogSource.Name}";
            _spiritCatalogSyncStatus = $"可手动同步 {_selectedSpiritCatalogSource.Name}";
        }

        _dispatcherQueue = DispatcherQueue.GetForCurrentThread();
        _runtimeTaskService.SettingsChanged += RuntimeTaskService_SettingsChanged;
        _isEncounterStatisticsEnabled = _runtimeTaskService.EncounterStatisticsEnabled;
        ApplyAutoBattleSettings(_runtimeTaskService.AutoBattleSettings);
    }

    public async Task LoadAsync()
    {
        await _runtimeTaskService.LoadSettingsAsync();
        ApplyRuntimeTaskSettings();
        await LoadSpiritCatalogSourceSelectionAsync();
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
        var source = SelectedSpiritCatalogSource ?? SpiritCatalogSources.FirstOrDefault();
        if (source is null)
        {
            SpiritCatalogSyncStatus = "同步失败：未配置图鉴源";
            IsSpiritCatalogSyncing = false;
            return;
        }

        SpiritCatalogSyncStatus = $"正在同步 {source.Name}";
        try
        {
            var progress = new Progress<SpiritCatalogSyncProgress>(UpdateSpiritCatalogSyncProgress);
            var document = await _spiritCatalogService.SyncAsync(source.Id, progress);
            ApplySpiritCatalogSummary(document);
            SpiritCatalogSyncStatus = $"同步完成：{document.Count} 个图鉴编号 · {document.Source.Name}";
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
            var source = SelectedSpiritCatalogSource ?? SpiritCatalogSources.FirstOrDefault();
            if (source is null)
            {
                SpiritCatalogSummary = "未配置图鉴源";
                return;
            }

            var document = await _spiritCatalogService.LoadAsync(source.Id);
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
        SpiritCatalogSummary = document.Source.ScrapedAt == default
            ? $"{document.Count} 个图鉴编号 · 未同步"
            : $"{document.Count} 个图鉴编号 · {document.Source.ScrapedAt.ToLocalTime():yyyy-MM-dd HH:mm}";
    }

    private async Task LoadSpiritCatalogSourceSelectionAsync()
    {
        try
        {
            var savedSourceId = await _localSettingsService.ReadSettingAsync<string>(SettingsKeys.SpiritCatalogSourceId);
            var source = ResolveSpiritCatalogSource(savedSourceId) ?? SpiritCatalogSources.FirstOrDefault();
            ApplySelectedSpiritCatalogSource(source, reloadSummary: false);
        }
        catch
        {
            ApplySelectedSpiritCatalogSource(SpiritCatalogSources.FirstOrDefault(), reloadSummary: false);
        }
    }

    private SpiritCatalogSourceOption? ResolveSpiritCatalogSource(string? sourceId)
    {
        return SpiritCatalogSources.FirstOrDefault(source =>
            string.Equals(source.Id, sourceId, StringComparison.OrdinalIgnoreCase));
    }

    private void ApplySelectedSpiritCatalogSource(
        SpiritCatalogSourceOption? source,
        bool reloadSummary)
    {
        if (source is null || !SetProperty(ref _selectedSpiritCatalogSource, source, nameof(SelectedSpiritCatalogSource)))
        {
            return;
        }

        OnPropertyChanged(nameof(SelectedSpiritCatalogSourceId));
        SpiritCatalogSourceSummary = $"当前：{source.Name}";
        if (!IsSpiritCatalogSyncing)
        {
            SpiritCatalogSyncStatus = $"可手动同步 {source.Name}";
        }

        if (CanPersistSettings)
        {
            _ = SaveSpiritCatalogSourceSelectionAsync(source.Id);
        }

        if (reloadSummary && _hasLoadedSettings)
        {
            _ = LoadSpiritCatalogSummaryAsync();
        }
    }

    private async Task SaveSpiritCatalogSourceSelectionAsync(string sourceId)
    {
        try
        {
            await _localSettingsService.SaveSettingAsync(SettingsKeys.SpiritCatalogSourceId, sourceId);
        }
        catch
        {
            // 选择源失败不影响当前会话的图鉴查看。
        }
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
        _autoBattleBossComboSequence = BossBattleComboSequence.NormalizeOrDefault(
            settings.BossComboSequence);
        _autoBattleReleaseSequence = (settings.ReleaseSequence ?? []).Select(step => step.Clone()).ToList();
        _autoBattleTurnSequencePresets = (settings.TurnSequencePresets ?? []).Select(preset => preset.Clone()).ToList();
        _selectedAutoBattleEncounterRelievedActionOption =
            FindAutoBattleEncounterRelievedActionOption(settings.EncounterRelievedAction);
        _selectedAutoBattleKeyboardInputMethodOption =
            FindAutoBattleKeyboardInputMethodOption(settings.KeyboardInputMethod);
        _autoBattleSkillSelectionActionDelayMs = settings.SkillSelectionActionDelayMs;
        _autoBattleSkillSelectionRetryDelayMs = settings.SkillSelectionRetryDelayMs;
        _autoBattleKeyboardHoldDurationMs = settings.KeyboardHoldDurationMs;
        _autoBattleKeyboardIntervalMs = settings.KeyboardIntervalMs;
        _autoBattleCaptureKeyboardIntervalMs = settings.CaptureKeyboardIntervalMs;

        OnPropertyChanged(nameof(IsAutoBattleEnabled));
        OnPropertyChanged(nameof(AutoBattleRoundOrder));
        OnPropertyChanged(nameof(AutoBattleTurnSequence));
        OnPropertyChanged(nameof(SelectedAutoBattleEncounterRelievedActionOption));
        OnPropertyChanged(nameof(AutoBattleEncounterRelievedActionDescription));
        OnPropertyChanged(nameof(SelectedAutoBattleKeyboardInputMethodOption));
        OnPropertyChanged(nameof(AutoBattleKeyboardInputMethodDescription));
        OnPropertyChanged(nameof(AutoBattleConfigurationSummary));
        OnPropertyChanged(nameof(AutoBattleOtherConfigurationSummary));
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
        OnPropertyChanged(nameof(AutoBattleOtherConfigurationSummary));
        OnPropertyChanged(nameof(AutoBattleSettings));
    }

    private AutoBattleSettings BuildAutoBattleSettings()
    {
        return new AutoBattleSettings
        {
            IsEnabled = IsAutoBattleEnabled,
            RoundOrder = AutoBattleRoundOrder,
            TurnSequence = AutoBattleTurnSequence,
            BossComboSequence = _autoBattleBossComboSequence,
            ReleaseSequence = _autoBattleReleaseSequence.Select(step => step.Clone()).ToList(),
            TurnSequencePresets = _autoBattleTurnSequencePresets.Select(preset => preset.Clone()).ToList(),
            EncounterRelievedAction = SelectedAutoBattleEncounterRelievedAction,
            KeyboardInputMethod = SelectedAutoBattleKeyboardInputMethod,
            SkillSelectionActionDelayMs = _autoBattleSkillSelectionActionDelayMs,
            SkillSelectionRetryDelayMs = _autoBattleSkillSelectionRetryDelayMs,
            KeyboardHoldDurationMs = _autoBattleKeyboardHoldDurationMs,
            KeyboardIntervalMs = _autoBattleKeyboardIntervalMs,
            CaptureKeyboardIntervalMs = _autoBattleCaptureKeyboardIntervalMs
        };
    }

    private static string BuildAutoBattleConfigurationSummary(
        IReadOnlyList<AutoBattleReleaseStep> releaseSequence,
        string bossComboSequence,
        AutoBattleEncounterRelievedAction encounterRelievedAction,
        KeyboardInputMethod inputMethod)
    {
        var encounterRelievedActionText = GetAutoBattleEncounterRelievedActionSummary(encounterRelievedAction);
        var inputMethodText = GetAutoBattleKeyboardInputMethodSummary(inputMethod);
        var bossComboText = string.Join(" → ", BossBattleComboSequence.ParseOrDefault(bossComboSequence));
        if (releaseSequence.Count == 0)
        {
            return $"未配置释放顺序 · 首领连招 {bossComboText} → Space · {encounterRelievedActionText} · {inputMethodText}";
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
        return $"{preview} · {suffix} · 首领连招 {bossComboText} → Space · {encounterRelievedActionText} · {inputMethodText}";
    }

    private AutoBattleEncounterRelievedActionOption FindAutoBattleEncounterRelievedActionOption(
        AutoBattleEncounterRelievedAction action)
    {
        return AutoBattleEncounterRelievedActionOptions.FirstOrDefault(option => option.Action == action)
            ?? AutoBattleEncounterRelievedActionOptions.First(option => option.Action == AutoBattleEncounterRelievedAction.RecoverEnergy);
    }

    private AutoBattleKeyboardInputMethodOption FindAutoBattleKeyboardInputMethodOption(
        KeyboardInputMethod method)
    {
        return AutoBattleKeyboardInputMethodOptions.FirstOrDefault(option => option.Method == method)
            ?? AutoBattleKeyboardInputMethodOptions.First(option => option.Method == KeyboardInputMethod.PostMessage);
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

    private static string GetAutoBattleKeyboardInputMethodSummary(KeyboardInputMethod method)
    {
        return method switch
        {
            KeyboardInputMethod.PostMessage => "PostMessage",
            KeyboardInputMethod.SendInput => "SendInput",
            KeyboardInputMethod.Interception => "Interception",
            _ => "PostMessage"
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

public sealed record AutoBattleKeyboardInputMethodOption(
    KeyboardInputMethod Method,
    string Name,
    string Description)
{
    public static IReadOnlyList<AutoBattleKeyboardInputMethodOption> CreateDefaultOptions()
    {
        return
        [
            new(
                KeyboardInputMethod.PostMessage,
                "PostMessage",
                "旧的后台窗口消息方式；不要求游戏前台，但可能被游戏屏蔽。"),
            new(
                KeyboardInputMethod.SendInput,
                "SendInput",
                "扫描码输入，类似 pydirectinput；需要游戏窗口前台，权限不能低于游戏。"),
            new(
                KeyboardInputMethod.Interception,
                "Interception",
                "驱动级键盘输入；需要安装 Interception 驱动并重启，游戏窗口需处于前台。")
        ];
    }
}
