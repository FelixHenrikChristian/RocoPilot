using RocoPilot.Models.Encounters;
using RocoPilot.Models.Statistics;

namespace RocoPilot.Services.Statistics;

internal static class StatisticsMutationRules
{
    public static void RecordEncounter(
        AccountStatisticsData account,
        EncounterSeasonDefinition season,
        string spiritName,
        DateTimeOffset capturedAt)
    {
        var seasonData = ResolveSeason(account, season);
        var record = seasonData.Encounters.FirstOrDefault(item =>
            string.Equals(item.Name, spiritName, StringComparison.OrdinalIgnoreCase));

        if (record is null)
        {
            seasonData.Encounters.Add(new EncounterSpiritRecord
            {
                Name = spiritName,
                Count = 1,
                Season = season.Id,
                LastCapturedAt = capturedAt
            });
            return;
        }

        record.Count++;
        record.Season = season.Id;
        record.LastCapturedAt = capturedAt;
    }

    public static void UpsertEncounter(
        AccountStatisticsData account,
        string seasonId,
        string spiritName,
        int count,
        DateTimeOffset countedAt)
    {
        var seasonData = ResolveSeason(account, seasonId);
        var record = seasonData.Encounters.FirstOrDefault(item =>
            string.Equals(item.Name, spiritName, StringComparison.OrdinalIgnoreCase));
        if (record is null)
        {
            seasonData.Encounters.Add(new EncounterSpiritRecord
            {
                Name = spiritName,
                Count = count,
                Season = seasonId,
                LastCapturedAt = countedAt
            });
            return;
        }

        record.Count += count;
        record.Season = seasonId;
        record.LastCapturedAt = Max(record.LastCapturedAt, countedAt);
    }

    public static void EditEncounter(
        AccountStatisticsData account,
        string seasonId,
        string originalName,
        string nextName,
        int nextCount,
        DateTimeOffset editedAt)
    {
        var seasonData = account.Seasons.FirstOrDefault(item =>
            string.Equals(item.Id, seasonId, StringComparison.OrdinalIgnoreCase));
        var originalRecord = seasonData?.Encounters.FirstOrDefault(item =>
            string.Equals(item.Name, originalName, StringComparison.OrdinalIgnoreCase));
        if (seasonData is null || originalRecord is null)
        {
            return;
        }

        var isRenamed = !string.Equals(originalName, nextName, StringComparison.OrdinalIgnoreCase);
        var editedRecordTime = isRenamed ? editedAt : originalRecord.LastCapturedAt;
        var targetRecord = seasonData.Encounters.FirstOrDefault(item =>
            !ReferenceEquals(item, originalRecord)
            && string.Equals(item.Name, nextName, StringComparison.OrdinalIgnoreCase));

        if (targetRecord is null)
        {
            originalRecord.Name = nextName;
            originalRecord.Count = nextCount;
            originalRecord.Season = seasonId;
            originalRecord.LastCapturedAt = editedRecordTime;
            return;
        }

        targetRecord.Count += nextCount;
        targetRecord.Season = seasonId;
        targetRecord.LastCapturedAt = Max(targetRecord.LastCapturedAt, editedRecordTime);
        seasonData.Encounters.Remove(originalRecord);
    }

    public static void DeleteEncounter(AccountStatisticsData account, string seasonId, string spiritName)
    {
        var seasonData = account.Seasons.FirstOrDefault(item =>
            string.Equals(item.Id, seasonId, StringComparison.OrdinalIgnoreCase));
        var record = seasonData?.Encounters.FirstOrDefault(item =>
            string.Equals(item.Name, spiritName, StringComparison.OrdinalIgnoreCase));
        if (seasonData is not null && record is not null)
        {
            seasonData.Encounters.Remove(record);
        }
    }

    public static void AddShinyCaptures(
        AccountStatisticsData account,
        string seasonId,
        string spiritName,
        int count,
        DateTimeOffset capturedAt,
        bool resetEncounterCount,
        int? encounterCountBeforeCapture)
    {
        var seasonData = ResolveSeason(account, seasonId);
        var resetEncounterRecords = resetEncounterCount
            ? seasonData.Encounters
                .Where(item => string.Equals(item.Name, spiritName, StringComparison.OrdinalIgnoreCase))
                .ToList()
            : new List<EncounterSpiritRecord>();
        var resolvedEncounterCountBeforeCapture = StatisticsDocumentNormalizer.NormalizeEncounterCountBeforeCapture(
            encounterCountBeforeCapture
                ?? (resetEncounterCount
                    ? resetEncounterRecords.Sum(item => Math.Max(0, item.Count))
                    : 0));

        for (var index = 0; index < count; index++)
        {
            seasonData.ShinyCaptures.Add(new ShinySpiritCaptureRecord
            {
                Name = spiritName,
                Season = seasonId,
                CapturedAt = capturedAt,
                EncounterCountBeforeCapture = resolvedEncounterCountBeforeCapture
            });
        }

        if (!resetEncounterCount)
        {
            return;
        }

        foreach (var encounter in resetEncounterRecords)
        {
            seasonData.Encounters.Remove(encounter);
        }
    }

    public static void EditShinyCaptures(
        AccountStatisticsData account,
        string? seasonId,
        string originalName,
        string nextName,
        int nextCount,
        string addSeasonId,
        DateTimeOffset capturedAt)
    {
        var originalCaptures = GetShinyCaptures(account, seasonId, originalName).ToList();
        if (originalCaptures.Count == 0)
        {
            return;
        }

        if (nextCount < originalCaptures.Count)
        {
            foreach (var capture in originalCaptures
                .OrderByDescending(capture => capture.CapturedAt)
                .Take(originalCaptures.Count - nextCount)
                .ToList())
            {
                RemoveShinyCapture(account, capture);
            }
        }
        else if (nextCount > originalCaptures.Count)
        {
            var addSeason = ResolveSeason(account, addSeasonId);
            for (var index = 0; index < nextCount - originalCaptures.Count; index++)
            {
                addSeason.ShinyCaptures.Add(new ShinySpiritCaptureRecord
                {
                    Name = originalName,
                    Season = addSeasonId,
                    CapturedAt = capturedAt
                });
            }
        }

        foreach (var capture in GetShinyCaptures(account, seasonId, originalName).ToList())
        {
            capture.Name = nextName;
            if (string.IsNullOrWhiteSpace(capture.Season))
            {
                capture.Season = addSeasonId;
            }
        }
    }

    public static void DeleteShinyCaptures(AccountStatisticsData account, string? seasonId, string spiritName)
    {
        foreach (var capture in GetShinyCaptures(account, seasonId, spiritName).ToList())
        {
            RemoveShinyCapture(account, capture);
        }
    }

    public static void AddPendingShinyCapture(
        AccountStatisticsData account,
        EncounterSeasonDefinition season,
        string spiritName,
        DateTimeOffset detectedAt)
    {
        _ = ResolveSeason(account, season);

        var pendingCapture = account.PendingShinyCaptures.FirstOrDefault(item =>
            string.Equals(item.Season, season.Id, StringComparison.OrdinalIgnoreCase)
            && string.Equals(item.Name, spiritName, StringComparison.OrdinalIgnoreCase));
        if (pendingCapture is null)
        {
            account.PendingShinyCaptures.Add(new PendingShinyCaptureRecord
            {
                Id = Guid.NewGuid().ToString("N"),
                Name = spiritName,
                Season = season.Id,
                DetectedAt = detectedAt
            });
            return;
        }

        pendingCapture.Name = spiritName;
        pendingCapture.Season = season.Id;
        pendingCapture.DetectedAt = Max(pendingCapture.DetectedAt, detectedAt);
    }

    public static void ConfirmPendingShinyCapture(
        AccountStatisticsData account,
        string pendingCaptureId,
        string spiritName,
        int encounterCount,
        DateTimeOffset confirmedAt)
    {
        var pendingCapture = FindPendingShinyCapture(account, pendingCaptureId);
        if (pendingCapture is null)
        {
            return;
        }

        var originalName = pendingCapture.Name;
        var seasonId = pendingCapture.Season;
        account.PendingShinyCaptures.Remove(pendingCapture);

        var seasonData = ResolveSeason(account, seasonId);
        seasonData.ShinyCaptures.Add(new ShinySpiritCaptureRecord
        {
            Name = spiritName,
            Season = seasonId,
            CapturedAt = pendingCapture.DetectedAt == default
                ? confirmedAt
                : pendingCapture.DetectedAt,
            EncounterCountBeforeCapture = encounterCount
        });

        foreach (var encounter in seasonData.Encounters
            .Where(item => string.Equals(item.Name, originalName, StringComparison.OrdinalIgnoreCase)
                || string.Equals(item.Name, spiritName, StringComparison.OrdinalIgnoreCase))
            .ToList())
        {
            seasonData.Encounters.Remove(encounter);
        }
    }

    public static void DiscardPendingShinyCapture(AccountStatisticsData account, string pendingCaptureId)
    {
        var pendingCapture = FindPendingShinyCapture(account, pendingCaptureId);
        if (pendingCapture is not null)
        {
            account.PendingShinyCaptures.Remove(pendingCapture);
        }
    }

    private static SeasonStatisticsData ResolveSeason(
        AccountStatisticsData account,
        EncounterSeasonDefinition season)
    {
        var seasonData = account.Seasons.FirstOrDefault(item =>
            string.Equals(item.Id, season.Id, StringComparison.OrdinalIgnoreCase));
        if (seasonData is null)
        {
            seasonData = new SeasonStatisticsData
            {
                Id = season.Id,
                Name = string.IsNullOrWhiteSpace(season.Name) ? $"{season.Id}赛季" : season.Name,
                DateRange = season.DateRange,
                EncounterTypeName = season.EncounterTypeName
            };
            account.Seasons.Add(seasonData);
        }

        if (!string.IsNullOrWhiteSpace(season.Name))
        {
            seasonData.Name = season.Name;
        }

        if (!string.IsNullOrWhiteSpace(season.DateRange))
        {
            seasonData.DateRange = season.DateRange;
        }

        if (!string.IsNullOrWhiteSpace(season.EncounterTypeName))
        {
            seasonData.EncounterTypeName = season.EncounterTypeName;
        }

        return seasonData;
    }

    private static SeasonStatisticsData ResolveSeason(AccountStatisticsData account, string seasonId)
    {
        seasonId = seasonId.Trim();
        var seasonData = account.Seasons.FirstOrDefault(item =>
            string.Equals(item.Id, seasonId, StringComparison.OrdinalIgnoreCase));
        if (seasonData is not null)
        {
            return seasonData;
        }

        seasonData = new SeasonStatisticsData
        {
            Id = seasonId,
            Name = $"{seasonId}赛季"
        };
        account.Seasons.Add(seasonData);
        return seasonData;
    }

    private static IEnumerable<ShinySpiritCaptureRecord> GetShinyCaptures(
        AccountStatisticsData account,
        string? seasonId,
        string spiritName)
    {
        return account.Seasons
            .Where(season => string.IsNullOrWhiteSpace(seasonId)
                || string.Equals(season.Id, seasonId, StringComparison.OrdinalIgnoreCase))
            .SelectMany(season => season.ShinyCaptures)
            .Where(capture => string.Equals(capture.Name, spiritName, StringComparison.OrdinalIgnoreCase));
    }

    private static void RemoveShinyCapture(AccountStatisticsData account, ShinySpiritCaptureRecord capture)
    {
        foreach (var season in account.Seasons)
        {
            if (season.ShinyCaptures.Remove(capture))
            {
                return;
            }
        }
    }

    private static PendingShinyCaptureRecord? FindPendingShinyCapture(
        AccountStatisticsData account,
        string pendingCaptureId)
    {
        return account.PendingShinyCaptures.FirstOrDefault(item =>
            string.Equals(item.Id, pendingCaptureId, StringComparison.OrdinalIgnoreCase));
    }

    private static DateTimeOffset Max(DateTimeOffset left, DateTimeOffset right)
    {
        return left >= right ? left : right;
    }
}
