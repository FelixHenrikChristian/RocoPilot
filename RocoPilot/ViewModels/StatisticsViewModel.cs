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
    private readonly Action<Action> _dispatch;

    private Task? _loadTask;
    private bool _isLoaded;
    private int _statisticsRefreshQueued;
    private EncounterSeasonConfig _seasonConfig = new();

    private IReadOnlyDictionary<string, string> _spiritAvatarPaths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    public StatisticsOverviewViewModel Overview { get; } = new();

    public StatisticsUidConfirmationRequest? PendingUidConfirmation =>
        _statisticsUidCoordinatorService.PendingConfirmation;

    public Visibility UidConfirmationWarningVisibility =>
        PendingUidConfirmation is null ? Visibility.Collapsed : Visibility.Visible;

    public string UidConfirmationWarningToolTip =>
        PendingUidConfirmation?.Message ?? "统计账号尚未确认";

    public event EventHandler? UidConfirmationChanged;

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
        : this(statisticsService, statisticsUidCoordinatorService, statisticsSyncService,
            encounterSeasonConfigService, spiritCatalogService, logger, CreateDispatcher())
    {
    }

    internal StatisticsViewModel(
        IStatisticsService statisticsService,
        IStatisticsUidCoordinatorService statisticsUidCoordinatorService,
        IStatisticsSyncService statisticsSyncService,
        IEncounterSeasonConfigService encounterSeasonConfigService,
        ISpiritCatalogService spiritCatalogService,
        ILogger<StatisticsViewModel> logger,
        Action<Action> dispatch)
    {
        _statisticsService = statisticsService;
        _statisticsUidCoordinatorService = statisticsUidCoordinatorService;
        _statisticsSyncService = statisticsSyncService;
        _encounterSeasonConfigService = encounterSeasonConfigService;
        _spiritCatalogService = spiritCatalogService;
        _logger = logger;
        _dispatch = dispatch;
        _seasonConfig = LoadEncounterSeasonConfig();
        _statisticsService.DocumentChanged += StatisticsService_DocumentChanged;
        _statisticsService.SelectedAccountChanged += StatisticsService_SelectedAccountChanged;
        _statisticsUidCoordinatorService.PendingConfirmationChanged +=
            StatisticsUidCoordinatorService_PendingConfirmationChanged;
        _statisticsSyncService.StatusChanged += StatisticsSyncService_StatusChanged;
        RefreshStatistics();
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

    public Task LoadAsync()
    {
        // 同一页面重复 Loaded 时共用加载任务；读取失败后下次进入仍可重试。
        return _loadTask is { IsCompleted: false } ? _loadTask : _loadTask = LoadCoreAsync();
    }

    private async Task LoadCoreAsync()
    {
        if (_isLoaded)
        {
            _seasonConfig = LoadEncounterSeasonConfig();
        }
        else
        {
            try
            {
                await _statisticsService.LoadAsync();
                _isLoaded = true;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "读取统计数据失败。");
                ShowNotification(InfoBarSeverity.Warning, "读取统计失败", "已使用当前内存统计数据。");
            }
        }

        RefreshStatistics();
        await LoadSpiritAvatarPathsAsync();
        await _statisticsSyncService.LoadStatusAsync();
        ApplySyncStatus(_statisticsSyncService.CurrentStatus);
    }

    public void SelectAccount(AccountStatisticsOption account)
    {
        _statisticsService.SetSelectedAccountUid(account.Uid);
    }

    public string ExportToJson()
    {
        var exportDocument = _statisticsService.CurrentDocument;
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

        var imported = await _statisticsService.ReplaceAsync(document);
        ShowNotification(
            InfoBarSeverity.Success,
            "导入完成",
            $"已导入 {imported.Accounts.Count} 个账号的统计记录。");
    }

    public async Task<bool> AddAccountAsync(string uid)
    {
        uid = uid.Trim();
        if (string.IsNullOrWhiteSpace(uid))
        {
            ShowNotification(InfoBarSeverity.Warning, "添加失败", "UID 不能为空。");
            return false;
        }

        if (_statisticsService.CurrentDocument.Accounts.Any(account => string.Equals(account.Uid, uid, StringComparison.OrdinalIgnoreCase)))
        {
            ShowNotification(InfoBarSeverity.Warning, "添加失败", $"账号 {uid} 已存在。");
            return false;
        }

        await _statisticsService.AddAccountAsync(uid);
        _statisticsService.SetSelectedAccountUid(uid);
        ShowNotification(InfoBarSeverity.Success, "已添加账号", $"已添加账号 {uid}。");
        return true;
    }

    public async Task DeleteAccountAsync(string uid)
    {
        var exists = _statisticsService.CurrentDocument.Accounts.Any(account =>
            string.Equals(account.Uid, uid, StringComparison.OrdinalIgnoreCase));
        if (!exists)
        {
            return;
        }

        await _statisticsService.DeleteAccountAsync(uid);
        ShowNotification(InfoBarSeverity.Success, "已删除账号", $"已删除账号 {uid} 及其统计记录。");
    }

    public async Task ClearAllAsync()
    {
        await _statisticsService.ClearAsync();
        ShowNotification(InfoBarSeverity.Success, "已清空", "已清空所有账号和统计记录。");
    }

    public async Task<bool> AddEncounterAsync(string seasonId, string name, int count)
    {
        name = CleanSpiritName(name);
        if (!ValidateStatisticInput(seasonId, name, count))
        {
            return false;
        }

        await _statisticsService.UpsertEncounterAsync(seasonId, name, count, DateTimeOffset.Now);
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

        await _statisticsService.EditEncounterAsync(
            seasonId,
            item.Name,
            nextName,
            nextCount,
            DateTimeOffset.Now);
        ShowNotification(InfoBarSeverity.Success, "已更新奇遇", $"已更新 {nextName}。");
        return true;
    }

    public async Task DeleteEncounterAsync(string seasonId, SpiritCountItem item)
    {
        await _statisticsService.DeleteEncounterAsync(seasonId, item.Name);
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
        seasonId = string.IsNullOrWhiteSpace(seasonId) ? Overview.DefaultShinyAddSeasonId : seasonId.Trim();
        name = CleanSpiritName(name);
        if (!ValidateStatisticInput(seasonId, name, count))
        {
            return false;
        }

        await _statisticsService.AddShinyCapturesAsync(
            seasonId!,
            name,
            count,
            capturedAt ?? DateTimeOffset.Now,
            resetEncounterCount,
            encounterCountBeforeCapture);
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
        if (Overview.SelectedAccount is null)
        {
            ShowNotification(InfoBarSeverity.Warning, "操作失败", "请先添加或选择账号。");
            return false;
        }

        if (string.IsNullOrWhiteSpace(nextName))
        {
            ShowNotification(InfoBarSeverity.Warning, "操作失败", "精灵名不能为空。");
            return false;
        }

        await _statisticsService.EditShinyCaptureAsync(
            item.Id,
            nextName,
            Math.Max(0, encounterCountBeforeCapture),
            capturedAt);
        ShowNotification(InfoBarSeverity.Success, "已更新异色", $"已更新 {nextName}。");
        return true;
    }

    public async Task DeleteShinyCaptureAsync(ShinyCaptureDetailItem item)
    {
        await _statisticsService.DeleteShinyCaptureAsync(item.Id);
        ShowNotification(InfoBarSeverity.Success, "已删除异色", $"已删除 {item.Name}。");
    }

    public async Task<bool> ConfirmPendingEncounterAsync(PendingEncounterItem item, string name)
    {
        name = CleanSpiritName(name);
        if (string.IsNullOrWhiteSpace(name))
        {
            ShowNotification(InfoBarSeverity.Warning, "确认失败", "精灵名不能为空。");
            return false;
        }

        // 手动输入允许图鉴尚未收录的名称；已收录时仍归并到进化链最低阶。
        var recordName = name;
        try
        {
            recordName = await _spiritCatalogService.ResolveEvolutionRecordNameAsync(name);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "读取图鉴失败，待确认奇遇将使用用户填写的名称。Spirit={SpiritName}", name);
        }
        if (string.IsNullOrWhiteSpace(recordName)) recordName = name;
        var result = await _statisticsService.ConfirmPendingEncounterAsync(item.AccountUid, item.Id, recordName);
        if (result == PendingEncounterConfirmationResult.BeforeReset)
        {
            ShowNotification(InfoBarSeverity.Warning, "未补入当前计数",
                "这次奇遇发生后，对应精灵的奇遇计数已被清空。请在异色记录中核对当时的奇遇次数，再忽略此条记录。");
            return false;
        }

        ShowNotification(InfoBarSeverity.Success,
            result == PendingEncounterConfirmationResult.Counted ? "已确认奇遇" : "记录已处理",
            result == PendingEncounterConfirmationResult.Counted ? $"已为账号 {item.AccountUid} 的 {item.Season} 补计 {recordName} x1。" : "此条记录已处理，无需重复确认。");
        return true;
    }

    public async Task DiscardPendingEncounterAsync(PendingEncounterItem item)
    {
        await _statisticsService.DiscardPendingEncounterAsync(item.AccountUid, item.Id);
        ShowNotification(InfoBarSeverity.Informational, "已忽略待确认奇遇", item.RawTextDisplay);
    }

    public async Task ConfirmLatestPendingShinyAsync()
    {
        var pendingCapture = Overview.LatestPendingShinyCapture;
        if (pendingCapture is null)
        {
            return;
        }

        var spiritName = CleanSpiritName(Overview.PendingEditor.Name);
        if (string.IsNullOrWhiteSpace(spiritName))
        {
            ShowNotification(InfoBarSeverity.Warning, "确认失败", "精灵名不能为空。");
            return;
        }

        var encounterCount = (int)Math.Round(Overview.PendingEditor.EncounterCount);
        await _statisticsService.ConfirmPendingShinyCaptureAsync(
            pendingCapture.Id,
            spiritName,
            encounterCount,
            DateTimeOffset.Now);
        ShowNotification(
            InfoBarSeverity.Success,
            "已确认异色",
            $"已计入 {spiritName}，并清空对应赛季 {encounterCount} 次奇遇计数。");
    }

    public async Task DiscardLatestPendingShinyAsync()
    {
        var pendingCapture = Overview.LatestPendingShinyCapture;
        if (pendingCapture is null)
        {
            return;
        }

        await _statisticsService.DiscardPendingShinyCaptureAsync(pendingCapture.Id);
        ShowNotification(InfoBarSeverity.Informational, "已忽略待确认异色", pendingCapture.Name);
    }

    public void ShowExported(string path)
    {
        ShowNotification(InfoBarSeverity.Success, "导出完成", path);
    }

    public async Task<StatisticsSyncSettings> LoadSyncSettingsAsync()
    {
        var settings = await _statisticsSyncService.LoadSettingsAsync();
        await _statisticsSyncService.LoadStatusAsync();
        ApplySyncStatus(_statisticsSyncService.CurrentStatus);
        return settings;
    }

    public async Task SaveSyncSettingsAsync(StatisticsSyncSettings settings, string? password)
    {
        await _statisticsSyncService.SaveSettingsAsync(settings, password);
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
        _dispatch(() => ApplySyncStatus(_statisticsSyncService.CurrentStatus));
    }

    private void StatisticsService_DocumentChanged(object? sender, StatisticsDocumentChangedEventArgs e)
    {
        RequestStatisticsRefresh();
    }

    private void StatisticsService_SelectedAccountChanged(object? sender, EventArgs e)
    {
        RequestStatisticsRefresh();
    }

    private void RequestStatisticsRefresh()
    {
        if (Interlocked.Exchange(ref _statisticsRefreshQueued, 1) != 0) return;
        _dispatch(() =>
        {
            Volatile.Write(ref _statisticsRefreshQueued, 0);
            RefreshStatistics();
        });
    }

    private void RefreshStatistics()
    {
        // 事件只表示需要刷新。处理 UI 队列时重新取值，避免旧事件回滚文档或账号选择。
        Overview.ApplyDocument(_statisticsService.CurrentDocument,
            _statisticsService.SelectedAccountUid, _seasonConfig, ResolveSpiritAvatar);
    }

    private void StatisticsUidCoordinatorService_PendingConfirmationChanged(object? sender, EventArgs e)
    {
        _dispatch(NotifyUidConfirmationChanged);
    }

    private static Action<Action> CreateDispatcher()
    {
        var queue = DispatcherQueue.GetForCurrentThread();
        return action =>
        {
            if (queue is null || queue.HasThreadAccess) action();
            else queue.TryEnqueue(() => action());
        };
    }

    private void NotifyUidConfirmationChanged()
    {
        OnPropertyChanged(nameof(PendingUidConfirmation));
        OnPropertyChanged(nameof(UidConfirmationWarningVisibility));
        OnPropertyChanged(nameof(UidConfirmationWarningToolTip));
        UidConfirmationChanged?.Invoke(this, EventArgs.Empty);
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

    private async Task LoadSpiritAvatarPathsAsync()
    {
        try
        {
            _spiritAvatarPaths = BuildSpiritAvatarPaths(await _spiritCatalogService.LoadAsync());
            RefreshStatistics();
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

    private bool ValidateStatisticInput(string? seasonId, string name, int count)
    {
        if (Overview.SelectedAccount is null)
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
}
