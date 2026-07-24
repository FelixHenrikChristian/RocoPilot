using System.Text.Json;
using System.Text.Json.Serialization;

using CommunityToolkit.Mvvm.ComponentModel;

using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;

using RocoPilot.Contracts.Services.Encounters;
using RocoPilot.Contracts.Services.Spirits;
using RocoPilot.Contracts.Services.Statistics;
using RocoPilot.Helpers;
using RocoPilot.Models.Encounters;
using RocoPilot.Models.Statistics;
using RocoPilot.Models.Spirits;

namespace RocoPilot.ViewModels;

public partial class StatisticsViewModel : ObservableRecipient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true
    };

    private readonly IStatisticsService _statisticsService;
    private readonly IStatisticsUidCoordinatorService _statisticsUidCoordinatorService;
    private readonly IStatisticsSyncService _statisticsSyncService;
    private readonly IEncounterSeasonConfigService _encounterSeasonConfigService;
    private readonly ISpiritCatalogService _spiritCatalogService;
    private readonly ILogger<StatisticsViewModel> _logger;
    private readonly DispatcherQueue? _dispatcherQueue;

    private StatisticsDocument _document;
    private bool _isLoaded;
    private IReadOnlyDictionary<string, string> _spiritAvatarPaths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    private IReadOnlyList<AccountStatisticsOption> _accounts = [];
    private AccountStatisticsOption? _selectedAccount;
    private IReadOnlyList<ShinyScopeOption> _shinyScopes = [new("全部")];
    private int _selectedSeasonIndex;
    private int _selectedShinyScopeIndex;
    private IReadOnlyList<SeasonStatisticsGroup> _seasons = [];
    private IReadOnlyList<SpiritCountItem> _allShinyCounts = [];
    private IReadOnlyList<PendingShinyCaptureItem> _pendingShinyCaptures = [];
    private string? _editingPendingShinyId;
    private string _pendingShinyEditName = string.Empty;
    private double _pendingShinyEditEncounterCount;

    public IReadOnlyList<AccountStatisticsOption> Accounts
    {
        get => _accounts;
        private set => SetProperty(ref _accounts, value);
    }

    public AccountStatisticsOption? SelectedAccount
    {
        get => _selectedAccount;
        set
        {
            if (value is not null && _statisticsService.IsActiveAccountSelectionRequired)
            {
                _statisticsService.SetSelectedAccountUid(value.Uid);
            }

            if (SetProperty(ref _selectedAccount, value))
            {
                _statisticsService.SetSelectedAccountUid(value?.Uid);
                OnPropertyChanged(nameof(SelectedAccountDisplayName));
                RefreshSelectedAccount();
            }
        }
    }

    public string SelectedAccountDisplayName => SelectedAccount?.DisplayName ?? "未选择账号";

    public StatisticsUidConfirmationRequest? PendingUidConfirmation =>
        _statisticsUidCoordinatorService.PendingConfirmation;

    public Visibility UidConfirmationWarningVisibility =>
        PendingUidConfirmation is null ? Visibility.Collapsed : Visibility.Visible;

    public string UidConfirmationWarningToolTip =>
        PendingUidConfirmation?.Message ?? "统计账号尚未确认";

    public event EventHandler? UidConfirmationChanged;

    public IReadOnlyList<ShinyScopeOption> ShinyScopes
    {
        get => _shinyScopes;
        private set => SetProperty(ref _shinyScopes, value);
    }

    public int SelectedSeasonIndex
    {
        get => _selectedSeasonIndex;
        set
        {
            var nextIndex = Seasons.Count == 0
                ? 0
                : Math.Clamp(value, 0, Seasons.Count - 1);
            if (SetProperty(ref _selectedSeasonIndex, nextIndex))
            {
                OnPropertyChanged(nameof(SelectedSeason));
                OnPropertyChanged(nameof(DefaultShinyAddSeasonId));
            }
        }
    }

    public int SelectedShinyScopeIndex
    {
        get => _selectedShinyScopeIndex;
        set
        {
            var nextIndex = ShinyScopes.Count == 0
                ? 0
                : Math.Clamp(value, 0, ShinyScopes.Count - 1);
            if (SetProperty(ref _selectedShinyScopeIndex, nextIndex))
            {
                OnPropertyChanged(nameof(SelectedShinyScopeSeasonId));
                OnPropertyChanged(nameof(DefaultShinyAddSeasonId));
                NotifySelectedShinyChanged();
            }
        }
    }

    public IReadOnlyList<SpiritCountItem> AllShinyCounts
    {
        get => _allShinyCounts;
        private set => SetProperty(ref _allShinyCounts, value);
    }

    public IReadOnlyList<PendingShinyCaptureItem> PendingShinyCaptures
    {
        get => _pendingShinyCaptures;
        private set => SetProperty(ref _pendingShinyCaptures, value);
    }

    public int PendingShinyCount => PendingShinyCaptures.Count;

    public PendingShinyCaptureItem? LatestPendingShinyCapture => PendingShinyCaptures.FirstOrDefault();

    public Visibility PendingShinyBadgeVisibility => PendingShinyCount > 0
        ? Visibility.Visible
        : Visibility.Collapsed;

    public Visibility PendingShinyConfirmationVisibility => PendingShinyCount > 0
        ? Visibility.Visible
        : Visibility.Collapsed;

    public string PendingShinyEditName
    {
        get => _pendingShinyEditName;
        set
        {
            if (SetProperty(ref _pendingShinyEditName, value))
            {
                UpdatePendingShinyEncounterCountFromName();
            }
        }
    }

    public double PendingShinyEditEncounterCount
    {
        get => _pendingShinyEditEncounterCount;
        set
        {
            var nextValue = double.IsNaN(value) ? 0 : Math.Max(0, value);
            SetProperty(ref _pendingShinyEditEncounterCount, nextValue);
        }
    }

    public string PendingShinySeasonDisplay => LatestPendingShinyCapture?.SeasonDisplay ?? "--";

    public string PendingShinyDetectedAtDisplay => LatestPendingShinyCapture?.DetectedAtDisplay ?? "--";

    public BitmapImage? LatestPendingShinyAvatar => LatestPendingShinyCapture?.Avatar;

    public Visibility LatestPendingShinyAvatarVisibility => LatestPendingShinyAvatar is null
        ? Visibility.Collapsed
        : Visibility.Visible;

    public Visibility LatestPendingShinyAvatarFallbackVisibility => LatestPendingShinyAvatar is null
        ? Visibility.Visible
        : Visibility.Collapsed;

    public string PendingShinyQueueDisplay => PendingShinyCount > 1
        ? $"还有 {PendingShinyCount - 1} 条待确认"
        : "当前仅此一条";

    public int TotalAllShiny => AllShinyCounts.Sum(item => item.Count);

    public IReadOnlyList<SpiritCountItem> SelectedShinyCounts => SelectedShinyScopeIndex == 0
        ? AllShinyCounts
        : Seasons.ElementAtOrDefault(SelectedShinyScopeIndex - 1)?.ShinyCounts ?? [];

    public int TotalSelectedShiny => SelectedShinyCounts.Sum(item => item.Count);

    public string SelectedShinyDateDisplay => SelectedShinyScopeIndex == 0
        ? StatisticsProjection.BuildAllSeasonDateDisplay(Seasons)
        : Seasons.ElementAtOrDefault(SelectedShinyScopeIndex - 1)?.SeasonDateDisplay ?? "无记录";

    public IReadOnlyList<SeasonStatisticsGroup> Seasons
    {
        get => _seasons;
        private set => SetProperty(ref _seasons, value);
    }

    public SeasonStatisticsGroup? SelectedSeason => Seasons.ElementAtOrDefault(SelectedSeasonIndex);

    public string? SelectedShinyScopeSeasonId => SelectedShinyScopeIndex == 0
        ? null
        : Seasons.ElementAtOrDefault(SelectedShinyScopeIndex - 1)?.Id;

    public string? DefaultShinyAddSeasonId => SelectedShinyScopeSeasonId
        ?? SelectedSeason?.Id
        ?? Seasons.FirstOrDefault()?.Id;

    private bool _isNotificationOpen;
    private InfoBarSeverity _notificationSeverity = InfoBarSeverity.Informational;
    private string _notificationTitle = string.Empty;
    private string _notificationMessage = string.Empty;
    private StatisticsSyncStatus _syncStatus = new();

    public bool IsNotificationOpen
    {
        get => _isNotificationOpen;
        set => SetProperty(ref _isNotificationOpen, value);
    }

    public InfoBarSeverity NotificationSeverity
    {
        get => _notificationSeverity;
        private set => SetProperty(ref _notificationSeverity, value);
    }

    public string NotificationTitle
    {
        get => _notificationTitle;
        private set => SetProperty(ref _notificationTitle, value);
    }

    public string NotificationMessage
    {
        get => _notificationMessage;
        private set => SetProperty(ref _notificationMessage, value);
    }

    public string SyncStatusSummary => BuildSyncStatusSummary(_syncStatus);

    public string SyncStatusToolTip => string.IsNullOrWhiteSpace(_syncStatus.Message)
        ? SyncStatusSummary
        : $"{SyncStatusSummary}\n{_syncStatus.Message}";

    public Visibility SyncEnabledIconVisibility => _syncStatus.IsEnabled
        ? Visibility.Visible
        : Visibility.Collapsed;

    public Visibility SyncDisabledIconVisibility => _syncStatus.IsEnabled
        ? Visibility.Collapsed
        : Visibility.Visible;

    public bool IsSyncBusy => _syncStatus.IsBusy;

    public IReadOnlyList<StatisticsSyncProviderOption> SyncProviders => _statisticsSyncService.GetProviders();

    public StatisticsViewModel(
        IStatisticsService statisticsService,
        IStatisticsUidCoordinatorService statisticsUidCoordinatorService,
        IStatisticsSyncService statisticsSyncService,
        IEncounterSeasonConfigService encounterSeasonConfigService,
        ISpiritCatalogService spiritCatalogService,
        ILogger<StatisticsViewModel> logger)
    {
        _statisticsService = statisticsService;
        _statisticsUidCoordinatorService = statisticsUidCoordinatorService;
        _statisticsSyncService = statisticsSyncService;
        _encounterSeasonConfigService = encounterSeasonConfigService;
        _spiritCatalogService = spiritCatalogService;
        _logger = logger;
        _dispatcherQueue = DispatcherQueue.GetForCurrentThread();
        _document = _statisticsService.CurrentDocument;
        _statisticsService.DocumentChanged += StatisticsService_DocumentChanged;
        _statisticsService.SelectedAccountChanged += StatisticsService_SelectedAccountChanged;
        _statisticsUidCoordinatorService.PendingConfirmationChanged +=
            StatisticsUidCoordinatorService_PendingConfirmationChanged;
        _statisticsSyncService.StatusChanged += StatisticsSyncService_StatusChanged;
        ApplyDocument(_document);
        ApplySyncStatus(_statisticsSyncService.CurrentStatus);
    }

    public void MarkPendingUidConfirmationPresented()
    {
        _statisticsUidCoordinatorService.MarkPendingConfirmationPresented();
    }

    public Task<StatisticsUidDetectionResult> RetryUidDetectionAsync(
        CancellationToken cancellationToken = default)
    {
        return _statisticsUidCoordinatorService.RetryDetectionAsync(cancellationToken);
    }

    public Task<string> ConfirmUidAsync(
        string uid,
        CancellationToken cancellationToken = default)
    {
        return _statisticsUidCoordinatorService.ConfirmUidAsync(uid, cancellationToken);
    }

    public async Task LoadAsync()
    {
        if (_isLoaded)
        {
            await LoadSpiritAvatarPathsAsync();
            ApplySyncStatus(await _statisticsSyncService.LoadStatusAsync());
            return;
        }

        _isLoaded = true;
        try
        {
            ApplyDocument(await _statisticsService.LoadAsync());
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "读取统计数据失败。");
            ShowNotification(InfoBarSeverity.Warning, "读取统计失败", "已使用当前内存统计数据。");
        }

        await LoadSpiritAvatarPathsAsync();
        ApplySyncStatus(await _statisticsSyncService.LoadStatusAsync());
    }

    public string ExportToJson()
    {
        var exportDocument = CloneDocument(_document);
        exportDocument.Info = new StatisticsDocumentInfo
        {
            Format = StatisticsDocumentFormats.RocoPilotStatistics,
            Version = StatisticsDocumentFormats.CurrentVersion,
            ExportApp = "RocoPilot",
            ExportedAt = DateTimeOffset.Now
        };

        return JsonSerializer.Serialize(exportDocument, JsonOptions);
    }

    public async Task ImportFromJsonAsync(string json)
    {
        var document = JsonSerializer.Deserialize<StatisticsDocument>(json, JsonOptions)
            ?? throw new InvalidOperationException("统计文件为空或格式不正确。");

        if (!string.Equals(
                document.Info?.Format,
                StatisticsDocumentFormats.RocoPilotStatistics,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("不是 RocoPilot 统计数据文件。");
        }

        ApplyDocument(await _statisticsService.ReplaceAsync(document));
        ShowNotification(
            InfoBarSeverity.Success,
            "导入完成",
            $"已导入 {Accounts.Count} 个账号，{Seasons.Count} 个赛季。");
    }

    public async Task<bool> AddAccountAsync(string uid)
    {
        uid = uid.Trim();
        if (string.IsNullOrWhiteSpace(uid))
        {
            ShowNotification(InfoBarSeverity.Warning, "添加失败", "UID 不能为空。");
            return false;
        }

        if (_document.Accounts.Any(account => string.Equals(account.Uid, uid, StringComparison.OrdinalIgnoreCase)))
        {
            ShowNotification(InfoBarSeverity.Warning, "添加失败", $"账号 {uid} 已存在。");
            return false;
        }

        var document = await _statisticsService.AddAccountAsync(uid);
        _statisticsService.SetSelectedAccountUid(uid);
        ApplyDocument(document, uid);
        ShowNotification(InfoBarSeverity.Success, "已添加账号", $"已添加账号 {uid}。");
        return true;
    }

    public async Task DeleteAccountAsync(string uid)
    {
        var exists = _document.Accounts.Any(account =>
            string.Equals(account.Uid, uid, StringComparison.OrdinalIgnoreCase));
        if (!exists)
        {
            return;
        }

        ApplyDocument(await _statisticsService.DeleteAccountAsync(uid));
        ShowNotification(InfoBarSeverity.Success, "已删除账号", $"已删除账号 {uid} 及其统计记录。");
    }

    public async Task ClearAllAsync()
    {
        ApplyDocument(await _statisticsService.ClearAsync());
        ShowNotification(InfoBarSeverity.Success, "已清空", "已清空所有账号和统计记录。");
    }

    public async Task<bool> AddEncounterAsync(string seasonId, string name, int count)
    {
        name = CleanSpiritName(name);
        if (!ValidateStatisticInput(seasonId, name, count))
        {
            return false;
        }

        ApplyDocument(await _statisticsService.UpsertEncounterAsync(seasonId, name, count, DateTimeOffset.Now));
        ShowNotification(InfoBarSeverity.Success, "已添加奇遇", $"已添加 {name} x{count}。");
        return true;
    }

    public async Task<bool> EditEncounterAsync(string seasonId, SpiritCountItem item, string nextName, int nextCount)
    {
        nextName = CleanSpiritName(nextName);
        if (!ValidateStatisticInput(seasonId, nextName, nextCount))
        {
            return false;
        }

        ApplyDocument(await _statisticsService.EditEncounterAsync(
            seasonId,
            item.Name,
            nextName,
            nextCount,
            DateTimeOffset.Now));
        ShowNotification(InfoBarSeverity.Success, "已更新奇遇", $"已更新 {nextName}。");
        return true;
    }

    public async Task DeleteEncounterAsync(string seasonId, SpiritCountItem item)
    {
        ApplyDocument(await _statisticsService.DeleteEncounterAsync(seasonId, item.Name));
        ShowNotification(InfoBarSeverity.Success, "已删除奇遇", $"已删除 {item.Name}。");
    }

    public async Task<bool> AddShinyAsync(
        string? seasonId,
        string name,
        int count,
        DateTimeOffset? capturedAt = null,
        bool resetEncounterCount = false,
        int? encounterCountBeforeCapture = null)
    {
        seasonId = string.IsNullOrWhiteSpace(seasonId) ? DefaultShinyAddSeasonId : seasonId.Trim();
        name = CleanSpiritName(name);
        if (!ValidateStatisticInput(seasonId, name, count))
        {
            return false;
        }

        ApplyDocument(await _statisticsService.AddShinyCapturesAsync(
            seasonId!,
            name,
            count,
            capturedAt ?? DateTimeOffset.Now,
            resetEncounterCount,
            encounterCountBeforeCapture));
        var message = resetEncounterCount
            ? $"已添加 {name} x{count}，并清空对应奇遇计数。"
            : $"已添加 {name} x{count}。";
        ShowNotification(InfoBarSeverity.Success, "已添加异色", message);
        return true;
    }

    public async Task<bool> EditShinyCaptureAsync(
        ShinyCaptureDetailItem item,
        string nextName,
        int encounterCountBeforeCapture,
        DateTimeOffset capturedAt)
    {
        nextName = CleanSpiritName(nextName);
        if (SelectedAccount is null)
        {
            ShowNotification(InfoBarSeverity.Warning, "操作失败", "请先添加或选择账号。");
            return false;
        }

        if (string.IsNullOrWhiteSpace(nextName))
        {
            ShowNotification(InfoBarSeverity.Warning, "操作失败", "精灵名不能为空。");
            return false;
        }

        ApplyDocument(await _statisticsService.EditShinyCaptureAsync(
            item.Id,
            nextName,
            Math.Max(0, encounterCountBeforeCapture),
            capturedAt));
        ShowNotification(InfoBarSeverity.Success, "已更新异色", $"已更新 {nextName}。");
        return true;
    }

    public async Task DeleteShinyCaptureAsync(ShinyCaptureDetailItem item)
    {
        ApplyDocument(await _statisticsService.DeleteShinyCaptureAsync(item.Id));
        ShowNotification(InfoBarSeverity.Success, "已删除异色", $"已删除 {item.Name}。");
    }

    public IReadOnlyList<ShinyCaptureDetailItem> GetShinyCaptureDetails(SpiritCountItem item)
    {
        return StatisticsProjection.BuildShinyCaptureDetails(
            FindSelectedAccount(),
            SelectedShinyScopeSeasonId,
            item.Name,
            ResolveSpiritAvatar);
    }

    public async Task ConfirmLatestPendingShinyAsync()
    {
        var pendingCapture = LatestPendingShinyCapture;
        if (pendingCapture is null)
        {
            return;
        }

        var spiritName = CleanSpiritName(PendingShinyEditName);
        if (string.IsNullOrWhiteSpace(spiritName))
        {
            ShowNotification(InfoBarSeverity.Warning, "确认失败", "精灵名不能为空。");
            return;
        }

        var encounterCount = (int)Math.Round(PendingShinyEditEncounterCount);
        ApplyDocument(await _statisticsService.ConfirmPendingShinyCaptureAsync(
            pendingCapture.Id,
            spiritName,
            encounterCount,
            DateTimeOffset.Now));
        ShowNotification(
            InfoBarSeverity.Success,
            "已确认异色",
            $"已计入 {spiritName}，并清空对应赛季 {encounterCount} 次奇遇计数。");
    }

    public async Task DiscardLatestPendingShinyAsync()
    {
        var pendingCapture = LatestPendingShinyCapture;
        if (pendingCapture is null)
        {
            return;
        }

        ApplyDocument(await _statisticsService.DiscardPendingShinyCaptureAsync(pendingCapture.Id));
        ShowNotification(InfoBarSeverity.Informational, "已忽略待确认异色", pendingCapture.Name);
    }

    public void ShowExported(string path)
    {
        ShowNotification(InfoBarSeverity.Success, "导出完成", path);
    }

    public async Task<StatisticsSyncSettings> LoadSyncSettingsAsync()
    {
        var settings = await _statisticsSyncService.LoadSettingsAsync();
        ApplySyncStatus(await _statisticsSyncService.LoadStatusAsync());
        return settings;
    }

    public async Task SaveSyncSettingsAsync(StatisticsSyncSettings settings, string? password)
    {
        ApplySyncStatus(await _statisticsSyncService.SaveSettingsAsync(settings, password));
        ShowNotification(InfoBarSeverity.Success, "云同步设置已保存", SyncStatusSummary);
    }

    public async Task TestSyncConnectionAsync()
    {
        await _statisticsSyncService.TestConnectionAsync();
        ShowNotification(InfoBarSeverity.Success, "云同步连接成功", SyncStatusSummary);
    }

    public async Task RefreshSyncRemoteInfoAsync()
    {
        var info = await _statisticsSyncService.RefreshRemoteInfoAsync();
        var message = info.Exists
            ? $"云端更新时间：{FormatSyncDate(info.LastModifiedAt)}"
            : "云端暂无统计数据。";
        ShowNotification(InfoBarSeverity.Success, "已刷新云端时间", message);
    }

    public async Task UploadStatisticsToCloudAsync()
    {
        await _statisticsSyncService.UploadAsync();
        ShowNotification(InfoBarSeverity.Success, "上传完成", SyncStatusSummary);
    }

    public async Task DownloadStatisticsFromCloudAsync()
    {
        await _statisticsSyncService.DownloadAsync();
        ShowNotification(InfoBarSeverity.Success, "合并完成", "已将云端统计数据合并到本地记录。");
    }

    public void ShowOperationFailed(string title, Exception exception)
    {
        _logger.LogWarning(exception, "{Title}", title);
        ShowNotification(InfoBarSeverity.Error, title, exception.Message);
    }

    private void StatisticsSyncService_StatusChanged(object? sender, StatisticsSyncStatusChangedEventArgs e)
    {
        if (_dispatcherQueue is null || _dispatcherQueue.HasThreadAccess)
        {
            ApplySyncStatus(e.Status);
            return;
        }

        _dispatcherQueue.TryEnqueue(() => ApplySyncStatus(e.Status));
    }

    private void StatisticsService_DocumentChanged(object? sender, StatisticsDocumentChangedEventArgs e)
    {
        if (_dispatcherQueue is null || _dispatcherQueue.HasThreadAccess)
        {
            ApplyDocument(e.Document);
            return;
        }

        _dispatcherQueue.TryEnqueue(() => ApplyDocument(e.Document));
    }

    private void StatisticsService_SelectedAccountChanged(object? sender, EventArgs e)
    {
        var selectedUid = _statisticsService.SelectedAccountUid;
        if (_dispatcherQueue is null || _dispatcherQueue.HasThreadAccess)
        {
            ApplySelectedAccountUid(selectedUid);
            return;
        }

        _dispatcherQueue.TryEnqueue(() => ApplySelectedAccountUid(selectedUid));
    }

    private void StatisticsUidCoordinatorService_PendingConfirmationChanged(
        object? sender,
        EventArgs e)
    {
        if (_dispatcherQueue is null || _dispatcherQueue.HasThreadAccess)
        {
            NotifyUidConfirmationChanged();
            return;
        }

        _dispatcherQueue.TryEnqueue(NotifyUidConfirmationChanged);
    }

    private void NotifyUidConfirmationChanged()
    {
        OnPropertyChanged(nameof(PendingUidConfirmation));
        OnPropertyChanged(nameof(UidConfirmationWarningVisibility));
        OnPropertyChanged(nameof(UidConfirmationWarningToolTip));
        UidConfirmationChanged?.Invoke(this, EventArgs.Empty);
    }

    private void ApplySelectedAccountUid(string? uid)
    {
        var account = Accounts.FirstOrDefault(item =>
            string.Equals(item.Uid, uid, StringComparison.OrdinalIgnoreCase));
        if (account is null
            || string.Equals(_selectedAccount?.Uid, account.Uid, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _selectedAccount = account;
        OnPropertyChanged(nameof(SelectedAccount));
        OnPropertyChanged(nameof(SelectedAccountDisplayName));
        RefreshSelectedAccount();
    }

    private void ApplyDocument(StatisticsDocument document, string? preferredUid = null)
    {
        _document = CloneDocument(document);

        var previousUid = preferredUid ?? SelectedAccount?.Uid ?? _statisticsService.ActiveAccountUid;
        Accounts = StatisticsProjection.BuildAccounts(_document);

        var nextSelectedAccount = Accounts.FirstOrDefault(account => string.Equals(account.Uid, previousUid, StringComparison.OrdinalIgnoreCase))
            ?? Accounts.FirstOrDefault();

        _selectedAccount = nextSelectedAccount;
        if (!_statisticsService.IsActiveAccountSelectionRequired)
        {
            _statisticsService.SetSelectedAccountUid(nextSelectedAccount?.Uid);
        }
        OnPropertyChanged(nameof(SelectedAccount));
        OnPropertyChanged(nameof(SelectedAccountDisplayName));
        RefreshSelectedAccount();
    }

    private void ApplySyncStatus(StatisticsSyncStatus status)
    {
        _syncStatus = status;
        OnPropertyChanged(nameof(SyncStatusSummary));
        OnPropertyChanged(nameof(SyncStatusToolTip));
        OnPropertyChanged(nameof(SyncEnabledIconVisibility));
        OnPropertyChanged(nameof(SyncDisabledIconVisibility));
        OnPropertyChanged(nameof(IsSyncBusy));
    }

    private void RefreshSelectedAccount()
    {
        var selectedAccount = FindSelectedAccount();
        var seasonConfig = LoadEncounterSeasonConfig();

        Seasons = StatisticsProjection.BuildSeasons(selectedAccount, seasonConfig, ResolveSpiritAvatar);
        ShinyScopes = StatisticsProjection.BuildShinyScopes(Seasons);
        AllShinyCounts = StatisticsProjection.BuildAllShinyCounts(selectedAccount, ResolveSpiritAvatar);
        PendingShinyCaptures = StatisticsProjection.BuildPendingShinyCaptures(selectedAccount, ResolveSpiritAvatar);
        SyncPendingShinyEditor();

        SelectedSeasonIndex = Math.Min(SelectedSeasonIndex, Math.Max(0, Seasons.Count - 1));
        SelectedShinyScopeIndex = Math.Min(SelectedShinyScopeIndex, Math.Max(0, ShinyScopes.Count - 1));

        OnPropertyChanged(nameof(SelectedSeasonIndex));
        OnPropertyChanged(nameof(SelectedShinyScopeIndex));
        OnPropertyChanged(nameof(SelectedSeason));
        OnPropertyChanged(nameof(SelectedShinyScopeSeasonId));
        OnPropertyChanged(nameof(DefaultShinyAddSeasonId));
        OnPropertyChanged(nameof(TotalAllShiny));
        NotifyPendingShinyChanged();
        NotifySelectedShinyChanged();
    }

    private EncounterSeasonConfig LoadEncounterSeasonConfig()
    {
        try
        {
            return _encounterSeasonConfigService.Load();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "读取赛季奇遇配置失败，统计页将仅显示已有统计赛季。");
            return new EncounterSeasonConfig();
        }
    }

    private AccountStatisticsData? FindSelectedAccount()
    {
        return SelectedAccount is null
            ? null
            : _document.Accounts.FirstOrDefault(account => string.Equals(account.Uid, SelectedAccount.Uid, StringComparison.OrdinalIgnoreCase));
    }

    private void NotifySelectedShinyChanged()
    {
        OnPropertyChanged(nameof(SelectedShinyCounts));
        OnPropertyChanged(nameof(TotalSelectedShiny));
        OnPropertyChanged(nameof(SelectedShinyDateDisplay));
    }

    private void NotifyPendingShinyChanged()
    {
        OnPropertyChanged(nameof(PendingShinyCount));
        OnPropertyChanged(nameof(LatestPendingShinyCapture));
        OnPropertyChanged(nameof(PendingShinyBadgeVisibility));
        OnPropertyChanged(nameof(PendingShinyConfirmationVisibility));
        OnPropertyChanged(nameof(PendingShinySeasonDisplay));
        OnPropertyChanged(nameof(PendingShinyDetectedAtDisplay));
        OnPropertyChanged(nameof(LatestPendingShinyAvatar));
        OnPropertyChanged(nameof(LatestPendingShinyAvatarVisibility));
        OnPropertyChanged(nameof(LatestPendingShinyAvatarFallbackVisibility));
        OnPropertyChanged(nameof(PendingShinyQueueDisplay));
    }

    private async Task LoadSpiritAvatarPathsAsync()
    {
        try
        {
            _spiritAvatarPaths = BuildSpiritAvatarPaths(await _spiritCatalogService.LoadAsync());
            RefreshSelectedAccount();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "读取精灵头像失败。");
        }
    }

    private IReadOnlyDictionary<string, string> BuildSpiritAvatarPaths(SpiritCatalogDocument document)
    {
        var avatarPaths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var item in document.Spirits)
        {
            var path = _spiritCatalogService.ResolveAvatarPath(item.AvatarPath);
            if (string.IsNullOrWhiteSpace(path))
            {
                continue;
            }

            AddSpiritAvatarName(avatarPaths, item.Name, path);
            AddSpiritAvatarName(avatarPaths, item.WikiName, path);
            AddSpiritAvatarName(avatarPaths, item.BaseName, path);
            foreach (var alias in item.Aliases)
            {
                AddSpiritAvatarName(avatarPaths, alias, path);
            }
        }

        return avatarPaths;
    }

    private BitmapImage? ResolveSpiritAvatar(string spiritName)
    {
        var key = TextMatchingHelper.NormalizeSpiritNameForMatching(spiritName);
        if (key.Length == 0 || !_spiritAvatarPaths.TryGetValue(key, out var path))
        {
            return null;
        }

        return new BitmapImage(new Uri(path, UriKind.Absolute));
    }

    private static void AddSpiritAvatarName(Dictionary<string, string> avatarPaths, string? name, string path)
    {
        var key = TextMatchingHelper.NormalizeSpiritNameForMatching(name);
        if (key.Length > 0)
        {
            avatarPaths.TryAdd(key, path);
        }
    }

    private void SyncPendingShinyEditor()
    {
        var pendingCapture = LatestPendingShinyCapture;
        if (pendingCapture is null)
        {
            _editingPendingShinyId = null;
            PendingShinyEditName = string.Empty;
            PendingShinyEditEncounterCount = 0;
            return;
        }

        if (string.Equals(_editingPendingShinyId, pendingCapture.Id, StringComparison.OrdinalIgnoreCase))
        {
            UpdatePendingShinyEncounterCountFromName();
            return;
        }

        _editingPendingShinyId = pendingCapture.Id;
        PendingShinyEditName = pendingCapture.Name;
        PendingShinyEditEncounterCount = pendingCapture.EncounterCount;
    }

    private void UpdatePendingShinyEncounterCountFromName()
    {
        var pendingCapture = LatestPendingShinyCapture;
        var selectedAccount = FindSelectedAccount();
        var spiritName = CleanSpiritName(PendingShinyEditName);
        if (pendingCapture is null
            || selectedAccount is null
            || string.IsNullOrWhiteSpace(spiritName))
        {
            PendingShinyEditEncounterCount = 0;
            return;
        }

        PendingShinyEditEncounterCount = StatisticsProjection.FindEncounterCount(
            selectedAccount,
            pendingCapture.Season,
            spiritName);
    }

    private bool ValidateStatisticInput(string? seasonId, string name, int count)
    {
        if (SelectedAccount is null)
        {
            ShowNotification(InfoBarSeverity.Warning, "操作失败", "请先添加或选择账号。");
            return false;
        }

        if (string.IsNullOrWhiteSpace(seasonId))
        {
            ShowNotification(InfoBarSeverity.Warning, "操作失败", "请先选择一个赛季。");
            return false;
        }

        if (string.IsNullOrWhiteSpace(name))
        {
            ShowNotification(InfoBarSeverity.Warning, "操作失败", "精灵名不能为空。");
            return false;
        }

        if (count <= 0)
        {
            ShowNotification(InfoBarSeverity.Warning, "操作失败", "计数必须大于 0。");
            return false;
        }

        return true;
    }

    private static string CleanSpiritName(string name)
    {
        return TextMatchingHelper.NormalizeSpiritNameInput(name);
    }

    private void ShowNotification(InfoBarSeverity severity, string title, string message)
    {
        NotificationSeverity = severity;
        NotificationTitle = title;
        NotificationMessage = message;
        IsNotificationOpen = false;
        IsNotificationOpen = true;
    }

    private static string BuildSyncStatusSummary(StatisticsSyncStatus status)
    {
        if (!status.IsEnabled)
        {
            return "云同步：未启用";
        }

        if (!status.IsConfigured)
        {
            return "云同步：配置不完整";
        }

        if (status.RemoteLastModifiedAt is not null)
        {
            return $"{status.ProviderName} · 云端 {FormatSyncDate(status.RemoteLastModifiedAt)}";
        }

        return $"{status.ProviderName} · {status.Message}";
    }

    private static string FormatSyncDate(DateTimeOffset? value)
    {
        return value is null
            ? "未同步"
            : value.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
    }

    private static StatisticsDocument CloneDocument(StatisticsDocument document)
    {
        var json = JsonSerializer.Serialize(document, JsonOptions);
        return JsonSerializer.Deserialize<StatisticsDocument>(json, JsonOptions) ?? new StatisticsDocument();
    }
}
