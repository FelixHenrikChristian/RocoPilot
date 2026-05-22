using RocoPilot.Helpers;
using RocoPilot.Models.Encounters;
using RocoPilot.Models.Statistics;

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media.Imaging;

namespace RocoPilot.ViewModels;

internal static class StatisticsProjection
{
    public static IReadOnlyList<AccountStatisticsOption> BuildAccounts(StatisticsDocument document)
    {
        return document.Accounts
            .Select(account => new AccountStatisticsOption(account.Uid))
            .ToList();
    }

    public static IReadOnlyList<SeasonStatisticsGroup> BuildSeasons(
        AccountStatisticsData? account,
        EncounterSeasonConfig? seasonConfig = null,
        Func<string, BitmapImage?>? avatarResolver = null)
    {
        var seasons = MergeConfiguredSeasons(account?.Seasons ?? [], seasonConfig)
            .Select(season => ToSeasonStatisticsGroup(season.Data, avatarResolver))
            .OrderByDescending(season => IsCurrentSeason(season, seasonConfig))
            .ThenBy(season => GetConfiguredSeasonOrder(season, seasonConfig))
            .ThenByDescending(season => season.LatestCapturedAt)
            .ThenBy(season => season.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        return seasons;
    }

    public static IReadOnlyList<ShinyScopeOption> BuildShinyScopes(IReadOnlyList<SeasonStatisticsGroup> seasons)
    {
        return new[] { new ShinyScopeOption("全部") }
            .Concat(seasons.Select(season => new ShinyScopeOption(season.ScopeName)))
            .ToList();
    }

    public static IReadOnlyList<SpiritCountItem> BuildAllShinyCounts(
        AccountStatisticsData? account,
        Func<string, BitmapImage?>? avatarResolver = null)
    {
        return BuildShinyCounts(
            account?.Seasons.SelectMany(season => season.ShinyCaptures) ?? [],
            season: null,
            avatarResolver);
    }

    public static IReadOnlyList<PendingShinyCaptureItem> BuildPendingShinyCaptures(
        AccountStatisticsData? account,
        Func<string, BitmapImage?>? avatarResolver = null)
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
                    encounterCount,
                    avatarResolver?.Invoke(record.Name));
            })
            .OrderByDescending(item => item.DetectedAt)
            .ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public static IReadOnlyList<ShinyCaptureDetailItem> BuildShinyCaptureDetails(
        AccountStatisticsData? account,
        string? seasonId,
        string spiritName,
        Func<string, BitmapImage?>? avatarResolver = null)
    {
        if (account is null || string.IsNullOrWhiteSpace(spiritName))
        {
            return [];
        }

        var captures = account.Seasons
            .Where(season => string.IsNullOrWhiteSpace(seasonId)
                || string.Equals(season.Id, seasonId, StringComparison.OrdinalIgnoreCase))
            .SelectMany(season => season.ShinyCaptures
                .Where(capture => TextMatchingHelper.AreSameSpiritName(capture.Name, spiritName))
                .Select(capture => new { Season = season, Capture = capture }))
            .OrderByDescending(item => item.Capture.CapturedAt)
            .ThenBy(item => item.Season.Id, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return captures
            .Select((capture, index) => new ShinyCaptureDetailItem(
                capture.Capture.Id,
                capture.Capture.Name,
                string.IsNullOrWhiteSpace(capture.Season.Name) ? $"{capture.Season.Id}赛季" : capture.Season.Name,
                capture.Capture.CapturedAt,
                capture.Capture.EncounterCountBeforeCapture,
                index + 1,
                captures.Count,
                avatarResolver?.Invoke(capture.Capture.Name)))
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
        return season?.Encounters
            .Where(item => TextMatchingHelper.AreSameSpiritName(item.Name, spiritName))
            .Sum(item => Math.Max(0, item.Count)) ?? 0;
    }

    private static SeasonStatisticsGroup ToSeasonStatisticsGroup(
        SeasonStatisticsData season,
        Func<string, BitmapImage?>? avatarResolver)
    {
        var pollutionCounts = season.Encounters
            .Where(item => !string.IsNullOrWhiteSpace(item.Name) && item.Count > 0)
            .GroupBy(item => TextMatchingHelper.NormalizeSpiritNameForMatching(item.Name), StringComparer.OrdinalIgnoreCase)
            .Where(group => !string.IsNullOrWhiteSpace(group.Key))
            .Select(group =>
            {
                var latestItem = group
                    .OrderByDescending(item => item.LastCapturedAt)
                    .First();
                return new SpiritCountItem(
                    TextMatchingHelper.NormalizeSpiritNameForDisplay(latestItem.Name),
                    group.Sum(item => Math.Max(0, item.Count)),
                    latestItem.LastCapturedAt,
                    string.IsNullOrWhiteSpace(latestItem.Season) ? season.Id : latestItem.Season,
                    avatar: avatarResolver?.Invoke(latestItem.Name));
            })
            .OrderByDescending(item => item.LastCapturedAt)
            .ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var shinyCounts = BuildShinyCounts(season.ShinyCaptures, season.Id, avatarResolver);

        return new SeasonStatisticsGroup(
            season.Id,
            season.Name,
            season.DateRange,
            season.EncounterTypeName,
            pollutionCounts,
            shinyCounts);
    }

    private static IReadOnlyList<(SeasonStatisticsData Data, int Order)> MergeConfiguredSeasons(
        IEnumerable<SeasonStatisticsData> accountSeasons,
        EncounterSeasonConfig? seasonConfig)
    {
        var merged = accountSeasons
            .Where(season => !string.IsNullOrWhiteSpace(season.Id))
            .Select((season, index) => (Data: CloneSeasonData(season), Order: seasonConfig?.Seasons.Count + index ?? index))
            .ToDictionary(
                item => item.Data.Id.Trim(),
                item => item,
                StringComparer.OrdinalIgnoreCase);

        if (seasonConfig is not null)
        {
            for (var index = 0; index < seasonConfig.Seasons.Count; index++)
            {
                var configuredSeason = seasonConfig.Seasons[index];
                if (string.IsNullOrWhiteSpace(configuredSeason.Id))
                {
                    continue;
                }

                if (merged.TryGetValue(configuredSeason.Id, out var existing))
                {
                    ApplyConfiguredSeasonMetadata(existing.Data, configuredSeason);
                    merged[configuredSeason.Id] = (existing.Data, index);
                    continue;
                }

                var seasonData = new SeasonStatisticsData
                {
                    Id = configuredSeason.Id,
                    Name = string.IsNullOrWhiteSpace(configuredSeason.Name)
                        ? $"{configuredSeason.Id}赛季"
                        : configuredSeason.Name,
                    DateRange = configuredSeason.DateRange,
                    EncounterTypeName = configuredSeason.EncounterTypeName
                };
                merged[configuredSeason.Id] = (seasonData, index);
            }
        }

        return merged.Values.ToList();
    }

    private static SeasonStatisticsData CloneSeasonData(SeasonStatisticsData season)
    {
        return new SeasonStatisticsData
        {
            Id = season.Id,
            Name = season.Name,
            DateRange = season.DateRange,
            EncounterTypeName = season.EncounterTypeName,
            Encounters = season.Encounters.ToList(),
            ShinyCaptures = season.ShinyCaptures.ToList()
        };
    }

    private static void ApplyConfiguredSeasonMetadata(
        SeasonStatisticsData seasonData,
        EncounterSeasonDefinition configuredSeason)
    {
        if (!string.IsNullOrWhiteSpace(configuredSeason.Name))
        {
            seasonData.Name = configuredSeason.Name;
        }

        if (!string.IsNullOrWhiteSpace(configuredSeason.DateRange))
        {
            seasonData.DateRange = configuredSeason.DateRange;
        }

        if (!string.IsNullOrWhiteSpace(configuredSeason.EncounterTypeName))
        {
            seasonData.EncounterTypeName = configuredSeason.EncounterTypeName;
        }
    }

    private static bool IsCurrentSeason(
        SeasonStatisticsGroup season,
        EncounterSeasonConfig? seasonConfig)
    {
        return !string.IsNullOrWhiteSpace(seasonConfig?.CurrentSeasonId)
            && string.Equals(season.Id, seasonConfig.CurrentSeasonId, StringComparison.OrdinalIgnoreCase);
    }

    private static int GetConfiguredSeasonOrder(
        SeasonStatisticsGroup season,
        EncounterSeasonConfig? seasonConfig)
    {
        if (seasonConfig is null)
        {
            return int.MaxValue;
        }

        var index = seasonConfig.Seasons.FindIndex(configuredSeason =>
            string.Equals(configuredSeason.Id, season.Id, StringComparison.OrdinalIgnoreCase));
        return index < 0 ? int.MaxValue : index;
    }

    private static IReadOnlyList<SpiritCountItem> BuildShinyCounts(
        IEnumerable<ShinySpiritCaptureRecord> captures,
        string? season,
        Func<string, BitmapImage?>? avatarResolver)
    {
        return captures
            .Where(capture => !string.IsNullOrWhiteSpace(capture.Name))
            .GroupBy(capture => TextMatchingHelper.NormalizeSpiritNameForMatching(capture.Name), StringComparer.OrdinalIgnoreCase)
            .Where(group => !string.IsNullOrWhiteSpace(group.Key))
            .Select(group =>
            {
                var latestCapture = group
                    .OrderByDescending(capture => capture.CapturedAt)
                    .First();
                return new SpiritCountItem(
                    TextMatchingHelper.NormalizeSpiritNameForDisplay(latestCapture.Name),
                    group.Count(),
                    latestCapture.CapturedAt,
                    season ?? latestCapture.Season,
                    avatar: avatarResolver?.Invoke(latestCapture.Name));
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

    public string ScopeName => string.IsNullOrWhiteSpace(Name)
        || string.Equals(Name, SeasonCode, StringComparison.OrdinalIgnoreCase)
        ? $"{SeasonCode}赛季"
        : Name;

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
        double pityThreshold = DefaultPityThreshold,
        BitmapImage? avatar = null)
    {
        Name = name;
        Count = count;
        LastCapturedAt = lastCapturedAt;
        Season = season;
        PityThreshold = pityThreshold;
        Avatar = avatar;
    }

    public string Name { get; }

    public int Count { get; }

    public DateTimeOffset LastCapturedAt { get; }

    public string Season { get; }

    public double PityThreshold { get; }

    public BitmapImage? Avatar { get; }

    public Visibility AvatarVisibility => Avatar is null ? Visibility.Collapsed : Visibility.Visible;

    public Visibility AvatarFallbackVisibility => Avatar is null ? Visibility.Visible : Visibility.Collapsed;

    public double ProgressRatio => PityThreshold <= 0
        ? 0
        : Math.Clamp(Count / PityThreshold, 0, 1);
}

public sealed class ShinyCaptureDetailItem
{
    public ShinyCaptureDetailItem(
        string id,
        string name,
        string seasonDisplay,
        DateTimeOffset capturedAt,
        int encounterCountBeforeCapture,
        int position,
        int totalCount,
        BitmapImage? avatar = null)
    {
        Id = id;
        Name = name;
        SeasonDisplay = seasonDisplay;
        CapturedAt = capturedAt;
        EncounterCountBeforeCapture = Math.Max(0, encounterCountBeforeCapture);
        Position = position;
        TotalCount = totalCount;
        Avatar = avatar;
    }

    public string Id { get; }

    public string Name { get; }

    public string SeasonDisplay { get; }

    public DateTimeOffset CapturedAt { get; }

    public int EncounterCountBeforeCapture { get; }

    public int Position { get; }

    public int TotalCount { get; }

    public BitmapImage? Avatar { get; }

    public Visibility AvatarVisibility => Avatar is null ? Visibility.Collapsed : Visibility.Visible;

    public Visibility AvatarFallbackVisibility => Avatar is null ? Visibility.Visible : Visibility.Collapsed;

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
        int encounterCount,
        BitmapImage? avatar = null)
    {
        Id = id;
        Name = name;
        Season = season;
        SeasonDisplay = seasonDisplay;
        DetectedAt = detectedAt;
        EncounterCount = encounterCount;
        Avatar = avatar;
    }

    public string Id { get; }

    public string Name { get; }

    public string Season { get; }

    public string SeasonDisplay { get; }

    public DateTimeOffset DetectedAt { get; }

    public int EncounterCount { get; }

    public BitmapImage? Avatar { get; }

    public Visibility AvatarVisibility => Avatar is null ? Visibility.Collapsed : Visibility.Visible;

    public Visibility AvatarFallbackVisibility => Avatar is null ? Visibility.Visible : Visibility.Collapsed;

    public string DetectedAtDisplay => DetectedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
}
