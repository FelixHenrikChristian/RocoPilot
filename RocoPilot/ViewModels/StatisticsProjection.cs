using RocoPilot.Models.Statistics;

namespace RocoPilot.ViewModels;

internal static class StatisticsProjection
{
    public static IReadOnlyList<AccountStatisticsOption> BuildAccounts(StatisticsDocument document)
    {
        return document.Accounts
            .Select(account => new AccountStatisticsOption(account.Uid))
            .ToList();
    }

    public static IReadOnlyList<SeasonStatisticsGroup> BuildSeasons(AccountStatisticsData? account)
    {
        return (account?.Seasons ?? [])
            .Select(ToSeasonStatisticsGroup)
            .OrderByDescending(season => season.LatestCapturedAt)
            .ThenBy(season => season.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public static IReadOnlyList<ShinyScopeOption> BuildShinyScopes(IReadOnlyList<SeasonStatisticsGroup> seasons)
    {
        return new[] { new ShinyScopeOption("全部") }
            .Concat(seasons.Select(season => new ShinyScopeOption(season.SeasonCode)))
            .ToList();
    }

    public static IReadOnlyList<SpiritCountItem> BuildAllShinyCounts(AccountStatisticsData? account)
    {
        return BuildShinyCounts(account?.Seasons.SelectMany(season => season.ShinyCaptures) ?? [], season: null);
    }

    public static IReadOnlyList<PendingShinyCaptureItem> BuildPendingShinyCaptures(AccountStatisticsData? account)
    {
        if (account is null)
        {
            return [];
        }

        return account.PendingShinyCaptures
            .Where(record => !string.IsNullOrWhiteSpace(record.Id)
                && !string.IsNullOrWhiteSpace(record.Name)
                && !string.IsNullOrWhiteSpace(record.Season))
            .Select(record =>
            {
                var season = account.Seasons.FirstOrDefault(item =>
                    string.Equals(item.Id, record.Season, StringComparison.OrdinalIgnoreCase));
                var encounterCount = FindEncounterCount(account, record.Season, record.Name);
                return new PendingShinyCaptureItem(
                    record.Id.Trim(),
                    record.Name.Trim(),
                    record.Season.Trim(),
                    string.IsNullOrWhiteSpace(season?.Name) ? $"{record.Season.Trim()}赛季" : season.Name,
                    record.DetectedAt,
                    encounterCount);
            })
            .OrderByDescending(item => item.DetectedAt)
            .ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public static IReadOnlyList<ShinyCaptureDetailItem> BuildShinyCaptureDetails(
        AccountStatisticsData? account,
        string? seasonId,
        string spiritName)
    {
        if (account is null || string.IsNullOrWhiteSpace(spiritName))
        {
            return [];
        }

        var captures = account.Seasons
            .Where(season => string.IsNullOrWhiteSpace(seasonId)
                || string.Equals(season.Id, seasonId, StringComparison.OrdinalIgnoreCase))
            .SelectMany(season => season.ShinyCaptures
                .Where(capture => string.Equals(capture.Name, spiritName, StringComparison.OrdinalIgnoreCase))
                .Select(capture => new { Season = season, Capture = capture }))
            .OrderByDescending(item => item.Capture.CapturedAt)
            .ThenBy(item => item.Season.Id, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return captures
            .Select((capture, index) => new ShinyCaptureDetailItem(
                capture.Capture.Name,
                string.IsNullOrWhiteSpace(capture.Season.Name) ? $"{capture.Season.Id}赛季" : capture.Season.Name,
                capture.Capture.CapturedAt,
                capture.Capture.EncounterCountBeforeCapture,
                index + 1,
                captures.Count))
            .ToList();
    }

    public static string BuildAllSeasonDateDisplay(IReadOnlyList<SeasonStatisticsGroup> seasons)
    {
        if (seasons.Count == 0)
        {
            return "无记录";
        }

        return $"{seasons.Min(season => season.DateRangeStart)}-{seasons.Max(season => season.DateRangeEnd)}";
    }

    public static int FindEncounterCount(AccountStatisticsData? account, string seasonId, string spiritName)
    {
        if (account is null || string.IsNullOrWhiteSpace(seasonId) || string.IsNullOrWhiteSpace(spiritName))
        {
            return 0;
        }

        var season = account.Seasons.FirstOrDefault(item =>
            string.Equals(item.Id, seasonId, StringComparison.OrdinalIgnoreCase));
        return season?.Encounters.FirstOrDefault(item =>
            string.Equals(item.Name, spiritName, StringComparison.OrdinalIgnoreCase))?.Count ?? 0;
    }

    private static SeasonStatisticsGroup ToSeasonStatisticsGroup(SeasonStatisticsData season)
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

public sealed class ShinyCaptureDetailItem
{
    public ShinyCaptureDetailItem(
        string name,
        string seasonDisplay,
        DateTimeOffset capturedAt,
        int encounterCountBeforeCapture,
        int position,
        int totalCount)
    {
        Name = name;
        SeasonDisplay = seasonDisplay;
        CapturedAt = capturedAt;
        EncounterCountBeforeCapture = Math.Max(0, encounterCountBeforeCapture);
        Position = position;
        TotalCount = totalCount;
    }

    public string Name { get; }

    public string SeasonDisplay { get; }

    public DateTimeOffset CapturedAt { get; }

    public int EncounterCountBeforeCapture { get; }

    public int Position { get; }

    public int TotalCount { get; }

    public string PositionDisplay => $"{Position} / {TotalCount}";

    public string CapturedDateDisplay => CapturedAt.ToLocalTime().ToString("yyyy-MM-dd");

    public string CapturedTimeDisplay => CapturedAt.ToLocalTime().ToString("HH:mm:ss");

    public string EncounterCountDisplay => $"{EncounterCountBeforeCapture} 次";
}

public sealed class PendingShinyCaptureItem
{
    public PendingShinyCaptureItem(
        string id,
        string name,
        string season,
        string seasonDisplay,
        DateTimeOffset detectedAt,
        int encounterCount)
    {
        Id = id;
        Name = name;
        Season = season;
        SeasonDisplay = seasonDisplay;
        DetectedAt = detectedAt;
        EncounterCount = encounterCount;
    }

    public string Id { get; }

    public string Name { get; }

    public string Season { get; }

    public string SeasonDisplay { get; }

    public DateTimeOffset DetectedAt { get; }

    public int EncounterCount { get; }

    public string DetectedAtDisplay => DetectedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
}
