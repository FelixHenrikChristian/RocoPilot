using System.Text.Json;
using System.Text.Json.Serialization;

using CommunityToolkit.Mvvm.ComponentModel;

using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

using RocoPilot.Contracts.Services.Statistics;
using RocoPilot.Helpers;
using RocoPilot.Models.Statistics;

namespace RocoPilot.ViewModels;

public partial class StatisticsViewModel : ObservableRecipient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true
    };

    private readonly IStatisticsService _statisticsService;
    private readonly ILogger<StatisticsViewModel> _logger;
    private readonly DispatcherQueue? _dispatcherQueue;

    private StatisticsDocument _document;
    private bool _isLoaded;

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
            if (SetProperty(ref _selectedAccount, value))
            {
                _statisticsService.SetSelectedAccountUid(value?.Uid);
                OnPropertyChanged(nameof(SelectedAccountDisplayName));
                RefreshSelectedAccount();
            }
        }
    }

    public string SelectedAccountDisplayName => SelectedAccount?.DisplayName ?? "未选择账号";

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

    public StatisticsViewModel(
        IStatisticsService statisticsService,
        ILogger<StatisticsViewModel> logger)
    {
        _statisticsService = statisticsService;
        _logger = logger;
        _dispatcherQueue = DispatcherQueue.GetForCurrentThread();
        _document = _statisticsService.CurrentDocument;
        _statisticsService.DocumentChanged += StatisticsService_DocumentChanged;
        ApplyDocument(_document);
    }

    public async Task LoadAsync()
    {
        if (_isLoaded)
        {
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

        ApplyDocument(await _statisticsService.AddAccountAsync(uid), uid);
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
        return StatisticsProjection.BuildShinyCaptureDetails(FindSelectedAccount(), SelectedShinyScopeSeasonId, item.Name);
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

    public void ShowOperationFailed(string title, Exception exception)
    {
        _logger.LogWarning(exception, "{Title}", title);
        ShowNotification(InfoBarSeverity.Error, title, exception.Message);
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

    private void ApplyDocument(StatisticsDocument document, string? preferredUid = null)
    {
        _document = CloneDocument(document);

        var previousUid = preferredUid ?? SelectedAccount?.Uid;
        Accounts = StatisticsProjection.BuildAccounts(_document);

        var nextSelectedAccount = Accounts.FirstOrDefault(account => string.Equals(account.Uid, previousUid, StringComparison.OrdinalIgnoreCase))
            ?? Accounts.FirstOrDefault();

        _selectedAccount = nextSelectedAccount;
        _statisticsService.SetSelectedAccountUid(nextSelectedAccount?.Uid);
        OnPropertyChanged(nameof(SelectedAccount));
        OnPropertyChanged(nameof(SelectedAccountDisplayName));
        RefreshSelectedAccount();
    }

    private void RefreshSelectedAccount()
    {
        var selectedAccount = FindSelectedAccount();

        Seasons = StatisticsProjection.BuildSeasons(selectedAccount);
        ShinyScopes = StatisticsProjection.BuildShinyScopes(Seasons);
        AllShinyCounts = StatisticsProjection.BuildAllShinyCounts(selectedAccount);
        PendingShinyCaptures = StatisticsProjection.BuildPendingShinyCaptures(selectedAccount);
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
        OnPropertyChanged(nameof(PendingShinyQueueDisplay));
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

    private static StatisticsDocument CloneDocument(StatisticsDocument document)
    {
        var json = JsonSerializer.Serialize(document, JsonOptions);
        return JsonSerializer.Deserialize<StatisticsDocument>(json, JsonOptions) ?? new StatisticsDocument();
    }
}
