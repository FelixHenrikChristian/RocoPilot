using System.Globalization;

using RocoPilot.Configuration;
using RocoPilot.Contracts.Services;
using RocoPilot.Contracts.Services.Encounters;
using RocoPilot.Models.Encounters;

namespace RocoPilot.Services.Encounters;

public sealed class EncounterSeasonReminderService(
    IEncounterSeasonConfigService seasonConfigService,
    ILocalSettingsService localSettingsService)
{
    public async Task<EncounterSeasonReminder?> GetPendingReminderAsync(DateOnly today)
    {
        EncounterSeasonReminder? latestSeason = null;
        foreach (var season in seasonConfigService.Load().Seasons)
        {
            if (string.IsNullOrWhiteSpace(season.Id)
                || !TryGetEndDate(season.DateRange, out var endDate))
            {
                continue;
            }

            if (latestSeason is null || endDate > latestSeason.EndDate)
            {
                latestSeason = new EncounterSeasonReminder(season.Id.Trim(), endDate);
            }
        }

        // 结束日期当天仍在有效期内，次日才提醒。
        if (latestSeason is null || today <= latestSeason.EndDate)
        {
            return null;
        }

        var dismissedKey = await localSettingsService.ReadSettingAsync<string>(
            SettingsKeys.DismissedEncounterSeasonReminder);
        return string.Equals(dismissedKey, latestSeason.DismissalKey, StringComparison.Ordinal)
            ? null
            : latestSeason;
    }

    public Task DismissAsync(EncounterSeasonReminder reminder)
    {
        return localSettingsService.SaveSettingAsync(
            SettingsKeys.DismissedEncounterSeasonReminder,
            reminder.DismissalKey);
    }

    private static bool TryGetEndDate(string? dateRange, out DateOnly endDate)
    {
        endDate = default;
        var dates = dateRange?.Split('-', StringSplitOptions.TrimEntries);
        return dates is { Length: 2 }
            && DateOnly.TryParseExact(dates[0], "yyyy/M/d", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var startDate)
            && DateOnly.TryParseExact(dates[1], "yyyy/M/d", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out endDate)
            && startDate <= endDate;
    }
}
