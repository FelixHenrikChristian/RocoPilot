using System.Text.Json;
using System.Text.Json.Serialization;

using CommunityToolkit.Mvvm.ComponentModel;

using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Controls;

using RocoPilot.Contracts.Services.Statistics;
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
            SetProperty(ref _selectedSeasonIndex, nextIndex);
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
                NotifySelectedShinyChanged();
            }
        }
    }

    public IReadOnlyList<SpiritCountItem> AllShinyCounts
    {
        get => _allShinyCounts;
        private set => SetProperty(ref _allShinyCounts, value);
    }

    public int TotalAllShiny => AllShinyCounts.Sum(item => item.Count);

    public IReadOnlyList<SpiritCountItem> SelectedShinyCounts => SelectedShinyScopeIndex == 0
        ? AllShinyCounts
        : Seasons.ElementAtOrDefault(SelectedShinyScopeIndex - 1)?.ShinyCounts ?? [];

    public int TotalSelectedShiny => SelectedShinyCounts.Sum(item => item.Count);

    public string SelectedShinyDateDisplay => SelectedShinyScopeIndex == 0
        ? BuildAllSeasonDateDisplay()
        : Seasons.ElementAtOrDefault(SelectedShinyScopeIndex - 1)?.SeasonDateDisplay ?? "无记录";

    public IReadOnlyList<SeasonStatisticsGroup> Seasons
    {
        get => _seasons;
        private set => SetProperty(ref _seasons, value);
    }

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
        Accounts = _document.Accounts
            .Select(account => new AccountStatisticsOption(account.Uid))
            .ToList();

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
        var seasons = selectedAccount?.Seasons ?? [];

        Seasons = seasons
            .Select(ToSeasonStatisticsGroup)
            .OrderByDescending(season => season.LatestCapturedAt)
            .ThenBy(season => season.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        ShinyScopes = new[] { new ShinyScopeOption("全部") }
            .Concat(Seasons.Select(season => new ShinyScopeOption(season.SeasonCode)))
            .ToList();
        AllShinyCounts = BuildShinyCounts(
            seasons.SelectMany(season => season.ShinyCaptures),
            season: null);

        SelectedSeasonIndex = Math.Min(SelectedSeasonIndex, Math.Max(0, Seasons.Count - 1));
        SelectedShinyScopeIndex = Math.Min(SelectedShinyScopeIndex, Math.Max(0, ShinyScopes.Count - 1));

        OnPropertyChanged(nameof(TotalAllShiny));
        NotifySelectedShinyChanged();
    }

    private AccountStatisticsData? FindSelectedAccount()
    {
        return SelectedAccount is null
            ? null
            : _document.Accounts.FirstOrDefault(account => string.Equals(account.Uid, SelectedAccount.Uid, StringComparison.OrdinalIgnoreCase));
    }

    private SeasonStatisticsGroup ToSeasonStatisticsGroup(SeasonStatisticsData season)
    {
        var pollutionCounts = season.Encounters
            .Where(item => !string.IsNullOrWhiteSpace(item.Name) && item.Count > 0)
            .Select(item => new SpiritCountItem(
                item.Name,
                item.Count,
                item.LastCapturedAt,
                string.IsNullOrWhiteSpace(item.Season) ? season.Id : item.Season))
            .OrderByDescending(item => item.LastCapturedAt)
            .ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var shinyCounts = BuildShinyCounts(season.ShinyCaptures, season.Id);

        return new SeasonStatisticsGroup(
            season.Id,
            season.Name,
            season.DateRange,
            season.EncounterTypeName,
            pollutionCounts,
            shinyCounts);
    }

    private static IReadOnlyList<SpiritCountItem> BuildShinyCounts(
        IEnumerable<ShinySpiritCaptureRecord> captures,
        string? season)
    {
        return captures
            .Where(capture => !string.IsNullOrWhiteSpace(capture.Name))
            .GroupBy(capture => capture.Name, StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                var latestCapture = group
                    .OrderByDescending(capture => capture.CapturedAt)
                    .First();
                return new SpiritCountItem(
                    latestCapture.Name,
                    group.Count(),
                    latestCapture.CapturedAt,
                    season ?? latestCapture.Season);
            })
            .OrderByDescending(item => item.LastCapturedAt)
            .ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private string BuildAllSeasonDateDisplay()
    {
        if (Seasons.Count == 0)
        {
            return "无记录";
        }

        return $"{Seasons.Min(season => season.DateRangeStart)}-{Seasons.Max(season => season.DateRangeEnd)}";
    }

    private void NotifySelectedShinyChanged()
    {
        OnPropertyChanged(nameof(SelectedShinyCounts));
        OnPropertyChanged(nameof(TotalSelectedShiny));
        OnPropertyChanged(nameof(SelectedShinyDateDisplay));
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

public sealed record AccountStatisticsOption(string Uid)
{
    public string DisplayName => Uid;
}

public sealed record ShinyScopeOption(string Name);

public sealed class SeasonStatisticsGroup
{
    public SeasonStatisticsGroup(
        string id,
        string name,
        string dateRange,
        string encounterTypeName,
        IReadOnlyList<SpiritCountItem> pollutionCounts,
        IReadOnlyList<SpiritCountItem> shinyCounts)
    {
        Id = id;
        Name = name;
        DateRange = dateRange;
        EncounterTypeName = encounterTypeName;
        PollutionCounts = pollutionCounts;
        ShinyCounts = shinyCounts;
    }

    public string Id { get; }

    public string Name { get; }

    public string DateRange { get; }

    public string EncounterTypeName { get; }

    public string SeasonDateDisplay => DateRange;

    public string DateRangeStart => DateRangeSeparatorIndex < 0
        ? DateRange
        : DateRange[..DateRangeSeparatorIndex];

    public string DateRangeEnd => DateRangeSeparatorIndex < 0
        ? DateRange
        : DateRange[(DateRangeSeparatorIndex + 1)..];

    public string EncounterTitle => string.IsNullOrWhiteSpace(EncounterTypeName)
        ? $"{SeasonCode}奇遇"
        : $"{SeasonCode}{EncounterTypeName}奇遇";

    public string SeasonCode => string.IsNullOrWhiteSpace(Id)
        ? Name.Replace("赛季", string.Empty)
        : Id;

    private int DateRangeSeparatorIndex => DateRange.IndexOf('-');

    public IReadOnlyList<SpiritCountItem> PollutionCounts { get; }

    public IReadOnlyList<SpiritCountItem> ShinyCounts { get; }

    public int PollutionTotal => PollutionCounts.Sum(item => item.Count);

    public int ShinyTotal => ShinyCounts.Sum(item => item.Count);

    public DateTimeOffset LatestCapturedAt
    {
        get
        {
            var latestPollution = PollutionCounts.Count == 0
                ? DateTimeOffset.MinValue
                : PollutionCounts.Max(item => item.LastCapturedAt);
            var latestShiny = ShinyCounts.Count == 0
                ? DateTimeOffset.MinValue
                : ShinyCounts.Max(item => item.LastCapturedAt);
            return latestPollution > latestShiny ? latestPollution : latestShiny;
        }
    }
}

public sealed class SpiritCountItem
{
    private const double DefaultPityThreshold = 80;

    public SpiritCountItem(
        string name,
        int count,
        DateTimeOffset lastCapturedAt,
        string season,
        double pityThreshold = DefaultPityThreshold)
    {
        Name = name;
        Count = count;
        LastCapturedAt = lastCapturedAt;
        Season = season;
        PityThreshold = pityThreshold;
    }

    public string Name { get; }

    public int Count { get; }

    public DateTimeOffset LastCapturedAt { get; }

    public string Season { get; }

    public double PityThreshold { get; }

    public double ProgressRatio => PityThreshold <= 0
        ? 0
        : Math.Clamp(Count / PityThreshold, 0, 1);
}
