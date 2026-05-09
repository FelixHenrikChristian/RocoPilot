using RocoPilot.Models.Statistics;

namespace RocoPilot.Services.Statistics;

internal static class StatisticsDocumentNormalizer
{
    public static StatisticsDocument Normalize(StatisticsDocument document)
    {
        document.Info ??= new StatisticsDocumentInfo();
        document.Info.Format = string.IsNullOrWhiteSpace(document.Info.Format)
            ? StatisticsDocumentFormats.RocoPilotStatistics
            : document.Info.Format.Trim();
        document.Info.Version = StatisticsDocumentFormats.CurrentVersion;
        document.Info.ExportApp = string.IsNullOrWhiteSpace(document.Info.ExportApp)
            ? "RocoPilot"
            : document.Info.ExportApp.Trim();
        document.Accounts ??= [];

        document.Accounts = document.Accounts
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
                account.PendingShinyCaptures = group
                    .SelectMany(item => item.PendingShinyCaptures ?? [])
                    .Select(NormalizePendingShinyCapture)
                    .Where(record => !string.IsNullOrWhiteSpace(record.Id)
                        && !string.IsNullOrWhiteSpace(record.Name)
                        && !string.IsNullOrWhiteSpace(record.Season))
                    .GroupBy(record => record.Id, StringComparer.OrdinalIgnoreCase)
                    .Select(recordGroup => recordGroup
                        .OrderByDescending(record => record.DetectedAt)
                        .First())
                    .OrderByDescending(record => record.DetectedAt)
                    .ThenBy(record => record.Name, StringComparer.OrdinalIgnoreCase)
                    .ToList();
                return account;
            })
            .OrderBy(account => account.Uid, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return document;
    }

    public static StatisticsDocument CreateDefault()
    {
        return Normalize(new StatisticsDocument
        {
            Info = new StatisticsDocumentInfo
            {
                Format = StatisticsDocumentFormats.RocoPilotStatistics,
                Version = StatisticsDocumentFormats.CurrentVersion,
                ExportApp = "RocoPilot",
                ExportedAt = DateTimeOffset.Now
            },
            Accounts = []
        });
    }

    public static int NormalizeEncounterCountBeforeCapture(int encounterCountBeforeCapture)
    {
        return Math.Max(0, encounterCountBeforeCapture);
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
            EncounterTypeName = first.EncounterTypeName?.Trim() ?? string.Empty
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
                CapturedAt = record.CapturedAt,
                EncounterCountBeforeCapture = NormalizeEncounterCountBeforeCapture(record.EncounterCountBeforeCapture)
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

    private static PendingShinyCaptureRecord NormalizePendingShinyCapture(PendingShinyCaptureRecord record)
    {
        return new PendingShinyCaptureRecord
        {
            Id = string.IsNullOrWhiteSpace(record.Id)
                ? Guid.NewGuid().ToString("N")
                : record.Id.Trim(),
            Name = record.Name?.Trim() ?? string.Empty,
            Season = record.Season?.Trim() ?? string.Empty,
            DetectedAt = record.DetectedAt == default
                ? DateTimeOffset.Now
                : record.DetectedAt
        };
    }
}
