using System.Text.Json;
using System.Text.Json.Serialization;

using CommunityToolkit.Mvvm.ComponentModel;

using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml.Controls;

using RocoPilot.Configuration;
using RocoPilot.Contracts.Services;
using RocoPilot.Models.Statistics;

namespace RocoPilot.ViewModels;

public partial class StatisticsViewModel : ObservableRecipient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true
    };

    private readonly ILocalSettingsService _localSettingsService;
    private readonly ILogger<StatisticsViewModel> _logger;

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
        ILocalSettingsService localSettingsService,
        ILogger<StatisticsViewModel> logger)
    {
        _localSettingsService = localSettingsService;
        _logger = logger;
        _document = CreateDefaultDocument();
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
            var savedDocument = await _localSettingsService.ReadSettingAsync<StatisticsDocument>(SettingsKeys.StatisticsData);
            if (savedDocument is not null)
            {
                ApplyDocument(NormalizeDocument(savedDocument));
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "读取统计数据失败，已使用内置示例数据。");
            ShowNotification(InfoBarSeverity.Warning, "读取统计失败", "已使用内置示例数据。");
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

        ApplyDocument(NormalizeDocument(document));
        await PersistAsync();
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

        _document.Accounts.Add(new AccountStatisticsData { Uid = uid });
        ApplyDocument(_document, uid);
        await PersistAsync();
        ShowNotification(InfoBarSeverity.Success, "已添加账号", $"已添加账号 {uid}。");
        return true;
    }

    public async Task DeleteAccountAsync(string uid)
    {
        var account = _document.Accounts.FirstOrDefault(account =>
            string.Equals(account.Uid, uid, StringComparison.OrdinalIgnoreCase));
        if (account is null)
        {
            return;
        }

        var deletedSelectedAccount = string.Equals(
            SelectedAccount?.Uid,
            account.Uid,
            StringComparison.OrdinalIgnoreCase);
        _document.Accounts.Remove(account);
        ApplyDocument(
            _document,
            deletedSelectedAccount ? _document.Accounts.FirstOrDefault()?.Uid : SelectedAccount?.Uid);
        await PersistAsync();
        ShowNotification(InfoBarSeverity.Success, "已删除账号", $"已删除账号 {uid} 及其统计记录。");
    }

    public async Task ClearAllAsync()
    {
        _document.Accounts.Clear();
        ApplyDocument(_document);
        await PersistAsync();
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

    private async Task PersistAsync()
    {
        await _localSettingsService.SaveSettingAsync(SettingsKeys.StatisticsData, _document);
    }

    private void ApplyDocument(StatisticsDocument document, string? preferredUid = null)
    {
        _document = NormalizeDocument(document);

        var previousUid = preferredUid ?? SelectedAccount?.Uid;
        Accounts = _document.Accounts
            .Select(account => new AccountStatisticsOption(account.Uid))
            .ToList();

        var nextSelectedAccount = Accounts.FirstOrDefault(account => string.Equals(account.Uid, previousUid, StringComparison.OrdinalIgnoreCase))
            ?? Accounts.FirstOrDefault();

        _selectedAccount = nextSelectedAccount;
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

    private static StatisticsDocument NormalizeDocument(StatisticsDocument document)
    {
        document.Info ??= new StatisticsDocumentInfo();
        document.Info.Format = string.IsNullOrWhiteSpace(document.Info.Format)
            ? StatisticsDocumentFormats.RocoPilotStatistics
            : document.Info.Format.Trim();
        document.Info.Version = string.IsNullOrWhiteSpace(document.Info.Version)
            ? StatisticsDocumentFormats.CurrentVersion
            : document.Info.Version.Trim();
        document.Info.ExportApp = string.IsNullOrWhiteSpace(document.Info.ExportApp)
            ? "RocoPilot"
            : document.Info.ExportApp.Trim();
        document.Accounts ??= [];

        var normalizedAccounts = document.Accounts
            .Where(account => !string.IsNullOrWhiteSpace(account.Uid))
            .GroupBy(account => account.Uid.Trim(), StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                var account = group.First();
                account.Uid = group.Key;
                account.Seasons = group
                    .SelectMany(item => item.Seasons ?? [])
                    .GroupBy(season => ResolveSeasonId(season), StringComparer.OrdinalIgnoreCase)
                    .Select(seasonGroup => NormalizeSeason(seasonGroup.Key, seasonGroup))
                    .Where(season => !string.IsNullOrWhiteSpace(season.Id))
                    .OrderBy(season => season.Id, StringComparer.OrdinalIgnoreCase)
                    .ToList();
                return account;
            })
            .OrderBy(account => account.Uid, StringComparer.OrdinalIgnoreCase)
            .ToList();

        document.Accounts = normalizedAccounts;
        return document;
    }

    private static SeasonStatisticsData NormalizeSeason(
        string seasonId,
        IEnumerable<SeasonStatisticsData> seasons)
    {
        var seasonList = seasons.ToList();
        var first = seasonList.First();
        var normalized = new SeasonStatisticsData
        {
            Id = seasonId,
            Name = string.IsNullOrWhiteSpace(first.Name) ? $"{seasonId}赛季" : first.Name.Trim(),
            DateRange = first.DateRange?.Trim() ?? string.Empty,
        };

        normalized.Encounters = seasonList
            .SelectMany(season => season.Encounters ?? [])
            .Where(record => !string.IsNullOrWhiteSpace(record.Name) && record.Count > 0)
            .GroupBy(record => record.Name.Trim(), StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                var latestRecord = group
                    .OrderByDescending(record => record.LastCapturedAt)
                    .First();
                return new EncounterSpiritRecord
                {
                    Name = latestRecord.Name.Trim(),
                    Count = group.Sum(record => Math.Max(0, record.Count)),
                    Season = string.IsNullOrWhiteSpace(latestRecord.Season) ? seasonId : latestRecord.Season.Trim(),
                    LastCapturedAt = latestRecord.LastCapturedAt
                };
            })
            .OrderByDescending(record => record.LastCapturedAt)
            .ThenBy(record => record.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        normalized.ShinyCaptures = seasonList
            .SelectMany(season => season.ShinyCaptures ?? [])
            .Where(record => !string.IsNullOrWhiteSpace(record.Name))
            .Select(record => new ShinySpiritCaptureRecord
            {
                Name = record.Name.Trim(),
                Season = string.IsNullOrWhiteSpace(record.Season) ? seasonId : record.Season.Trim(),
                CapturedAt = record.CapturedAt
            })
            .OrderByDescending(record => record.CapturedAt)
            .ThenBy(record => record.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return normalized;
    }

    private static string ResolveSeasonId(SeasonStatisticsData season)
    {
        if (!string.IsNullOrWhiteSpace(season.Id))
        {
            return season.Id.Trim();
        }

        if (!string.IsNullOrWhiteSpace(season.Name))
        {
            return season.Name.Trim().Replace("赛季", string.Empty, StringComparison.OrdinalIgnoreCase);
        }

        return string.Empty;
    }

    private static StatisticsDocument CloneDocument(StatisticsDocument document)
    {
        var json = JsonSerializer.Serialize(document, JsonOptions);
        return JsonSerializer.Deserialize<StatisticsDocument>(json, JsonOptions) ?? new StatisticsDocument();
    }

    private static StatisticsDocument CreateDefaultDocument()
    {
        var seasonSeeds = new[]
        {
            new SeasonSeed(
                "S1",
                "S1赛季",
                "2025/3/26-2025/5/21",
                [
                    new("已垫", 6),
                    new("银狼LV.999", 60),
                    new("银狼", 74),
                    new("不死途", 80),
                    new("万敌", 79),
                    new("布洛妮娅", 80),
                    new("昔涟", 81),
                    new("白厄", 80),
                    new("克拉拉", 35),
                    new("彦卿", 73),
                    new("镜流", 68),
                ],
                [
                    new("已垫", 2),
                    new("银狼LV.999", 1),
                    new("银狼", 3),
                    new("不死途", 1),
                    new("布洛妮娅", 2),
                    new("昔涟", 4),
                    new("白厄", 1),
                    new("克拉拉", 2),
                ]),
            new SeasonSeed(
                "S2",
                "S2赛季",
                "2025/5/22-2025/7/16",
                [
                    new("已垫", 52),
                    new("银河铁道之夜", 70),
                    new("回到大地的飞行", 11),
                    new("纵然山河万程", 72),
                    new("黎明恰如此燃烧", 68),
                    new("血火啊，燃烧前路", 56),
                    new("命运从未公平", 67),
                    new("那无数个春天", 67),
                    new("流逝的岸", 67),
                    new("时节不居", 68),
                    new("纯粹思维的洗礼", 72),
                    new("到不了的彼岸", 43),
                ],
                [
                    new("银河铁道之夜", 2),
                    new("回到大地的飞行", 1),
                    new("纵然山河万程", 1),
                    new("黎明恰如此燃烧", 3),
                    new("血火啊，燃烧前路", 1),
                    new("命运从未公平", 2),
                    new("那无数个春天", 1),
                    new("纯粹思维的洗礼", 2),
                ]),
            new SeasonSeed(
                "S3",
                "S3赛季",
                "2025/7/17-2025/9/10",
                [
                    new("已垫", 18),
                    new("花火", 44),
                    new("饮月君", 77),
                    new("飞霄", 61),
                    new("砂金", 58),
                    new("知更鸟", 80),
                    new("波提欧", 32),
                    new("黄泉", 70),
                    new("流萤", 65),
                    new("翡翠", 27),
                ],
                [
                    new("花火", 1),
                    new("饮月君", 2),
                    new("飞霄", 1),
                    new("砂金", 2),
                    new("知更鸟", 1),
                    new("黄泉", 3),
                ]),
        };

        return NormalizeDocument(new StatisticsDocument
        {
            Info = new StatisticsDocumentInfo
            {
                Format = StatisticsDocumentFormats.RocoPilotStatistics,
                Version = StatisticsDocumentFormats.CurrentVersion,
                ExportApp = "RocoPilot",
                ExportedAt = DateTimeOffset.Now
            },
            Accounts =
            [
                CreateAccountSeed("106947084", seasonSeeds),
                CreateAccountSeed("240186173", seasonSeeds),
                CreateAccountSeed("613908452", seasonSeeds),
            ]
        });
    }

    private static AccountStatisticsData CreateAccountSeed(
        string uid,
        IReadOnlyList<SeasonSeed> seasonSeeds)
    {
        return new AccountStatisticsData
        {
            Uid = uid,
            Seasons = seasonSeeds.Select(CreateSeasonSeed).ToList()
        };
    }

    private static SeasonStatisticsData CreateSeasonSeed(SeasonSeed seed)
    {
        var seasonEnd = ParseSeasonEnd(seed.DateRange);
        var encounterIndex = 0;
        var shinyIndex = 0;

        return new SeasonStatisticsData
        {
            Id = seed.Id,
            Name = seed.Name,
            DateRange = seed.DateRange,
            Encounters = seed.Encounters
                .Select(item => new EncounterSpiritRecord
                {
                    Name = item.Name,
                    Count = item.Count,
                    Season = seed.Id,
                    LastCapturedAt = seasonEnd.AddMinutes(-encounterIndex++)
                })
                .ToList(),
            ShinyCaptures = seed.ShinyCounts
                .SelectMany(item => Enumerable.Range(0, item.Count).Select(_ => new ShinySpiritCaptureRecord
                {
                    Name = item.Name,
                    Season = seed.Id,
                    CapturedAt = seasonEnd.AddMinutes(-shinyIndex++)
                }))
                .ToList()
        };
    }

    private static DateTimeOffset ParseSeasonEnd(string dateRange)
    {
        var endText = dateRange.Split('-', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .LastOrDefault();
        return DateTimeOffset.TryParse(endText, out var end)
            ? new DateTimeOffset(end.Date.AddHours(23).AddMinutes(59).AddSeconds(59), TimeSpan.FromHours(8))
            : DateTimeOffset.Now;
    }

    private sealed record SpiritCountSeed(string Name, int Count);

    private sealed record SeasonSeed(
        string Id,
        string Name,
        string DateRange,
        IReadOnlyList<SpiritCountSeed> Encounters,
        IReadOnlyList<SpiritCountSeed> ShinyCounts);
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
        IReadOnlyList<SpiritCountItem> pollutionCounts,
        IReadOnlyList<SpiritCountItem> shinyCounts)
    {
        Id = id;
        Name = name;
        DateRange = dateRange;
        PollutionCounts = pollutionCounts;
        ShinyCounts = shinyCounts;
    }

    public string Id
    {
        get;
    }

    public string Name
    {
        get;
    }

    public string DateRange
    {
        get;
    }

    public string SeasonDateDisplay => DateRange;

    public string DateRangeStart => DateRangeSeparatorIndex < 0
        ? DateRange
        : DateRange[..DateRangeSeparatorIndex];

    public string DateRangeEnd => DateRangeSeparatorIndex < 0
        ? DateRange
        : DateRange[(DateRangeSeparatorIndex + 1)..];

    public string EncounterTitle => $"{SeasonCode}奇遇";

    public string SeasonCode => string.IsNullOrWhiteSpace(Id)
        ? Name.Replace("赛季", string.Empty)
        : Id;

    private int DateRangeSeparatorIndex => DateRange.IndexOf('-');

    public IReadOnlyList<SpiritCountItem> PollutionCounts
    {
        get;
    }

    public IReadOnlyList<SpiritCountItem> ShinyCounts
    {
        get;
    }

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

    public string Name
    {
        get;
    }

    public int Count
    {
        get;
    }

    public DateTimeOffset LastCapturedAt
    {
        get;
    }

    public string Season
    {
        get;
    }

    public double PityThreshold
    {
        get;
    }

    public double ProgressRatio => PityThreshold <= 0
        ? 0
        : Math.Clamp(Count / PityThreshold, 0, 1);
}
