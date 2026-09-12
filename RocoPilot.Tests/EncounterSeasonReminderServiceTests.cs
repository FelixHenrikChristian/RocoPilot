using Microsoft.VisualStudio.TestTools.UnitTesting;

using RocoPilot.Contracts.Services.Encounters;
using RocoPilot.Models.Encounters;
using RocoPilot.Services.Encounters;
using RocoPilot.Tests.TestDoubles;

namespace RocoPilot.Tests;

[TestClass]
public sealed class EncounterSeasonReminderServiceTests
{
    [TestMethod]
    [DataRow(8, false)]
    [DataRow(9, false)]
    [DataRow(10, true)]
    [DataRow(12, true)]
    public async Task RemindsOnlyAfterTheSeasonEndDate(int day, bool expectedReminder)
    {
        var service = new EncounterSeasonReminderService(S3Config(), new ControlledSettingsStore());

        var reminder = await service.GetPendingReminderAsync(new DateOnly(2026, 9, day));

        Assert.AreEqual(expectedReminder, reminder is not null);
        if (expectedReminder)
        {
            Assert.AreEqual("S3", reminder!.SeasonId);
            Assert.AreEqual(new DateOnly(2026, 9, 9), reminder.EndDate);
        }
    }

    [TestMethod]
    public async Task UsesLatestConfiguredSeasonRegardlessOfOrderOrCurrentSeasonId()
    {
        var config = S3Config();
        config.Config.Seasons.Insert(0, Season("S4", "2026/9/10-2026/11/4"));
        config.Config.Seasons.Add(Season("S1", "2026/3/26-2026/5/21"));
        var service = new EncounterSeasonReminderService(config, new ControlledSettingsStore());

        Assert.IsNull(await service.GetPendingReminderAsync(new DateOnly(2026, 9, 12)));
        var reminder = await service.GetPendingReminderAsync(new DateOnly(2026, 11, 5));
        Assert.IsNotNull(reminder);
        Assert.AreEqual("S4", reminder.SeasonId);
    }

    [TestMethod]
    public async Task ClosingWithoutDismissalLeavesReminderForNextStartup()
    {
        var settings = new ControlledSettingsStore();
        var config = S3Config();
        var service = new EncounterSeasonReminderService(config, settings);
        Assert.IsNotNull(await service.GetPendingReminderAsync(new DateOnly(2026, 9, 12)));

        var restartedService = new EncounterSeasonReminderService(config, settings);

        Assert.IsNotNull(await restartedService.GetPendingReminderAsync(new DateOnly(2026, 9, 13)));
        Assert.AreEqual(0, settings.SaveCount);
    }

    [TestMethod]
    public async Task DismissalSurvivesRestartAndOnlyAppliesToThatSeasonExpiry()
    {
        var settings = new ControlledSettingsStore();
        var config = S3Config();
        var service = new EncounterSeasonReminderService(config, settings);
        var reminder = await service.GetPendingReminderAsync(new DateOnly(2026, 9, 12));
        Assert.IsNotNull(reminder);
        await service.DismissAsync(reminder);

        var restartedService = new EncounterSeasonReminderService(config, settings);
        Assert.IsNull(await restartedService.GetPendingReminderAsync(new DateOnly(2026, 9, 13)));

        config.Config.Seasons.Add(Season("S4", "2026/9/10-2026/11/4"));
        Assert.IsNull(await restartedService.GetPendingReminderAsync(new DateOnly(2026, 10, 1)));
        var nextReminder = await restartedService.GetPendingReminderAsync(new DateOnly(2026, 11, 5));
        Assert.IsNotNull(nextReminder);
        Assert.AreEqual("S4", nextReminder.SeasonId);
    }

    [TestMethod]
    public async Task RevisedEndDateGetsItsOwnReminder()
    {
        var config = S3Config();
        var service = new EncounterSeasonReminderService(config, new ControlledSettingsStore());
        var reminder = await service.GetPendingReminderAsync(new DateOnly(2026, 9, 12));
        Assert.IsNotNull(reminder);
        await service.DismissAsync(reminder);

        config.Config.Seasons[0].DateRange = "2026/7/16-2026/9/16";

        Assert.IsNull(await service.GetPendingReminderAsync(new DateOnly(2026, 9, 12)));
        Assert.IsNotNull(await service.GetPendingReminderAsync(new DateOnly(2026, 9, 17)));
    }

    [TestMethod]
    public async Task DateFormattingChangesDoNotResetDismissal()
    {
        var config = S3Config();
        var service = new EncounterSeasonReminderService(config, new ControlledSettingsStore());
        var reminder = await service.GetPendingReminderAsync(new DateOnly(2026, 9, 12));
        Assert.IsNotNull(reminder);
        await service.DismissAsync(reminder);

        config.Config.Seasons[0].DateRange = "2026/07/16 - 2026/09/09";
        config.Config.Seasons[0].Id = "s3";

        Assert.IsNull(await service.GetPendingReminderAsync(new DateOnly(2026, 9, 12)));
    }

    [TestMethod]
    [DataRow("")]
    [DataRow("日期待定")]
    [DataRow("2026/7/16-2026/2/30")]
    [DataRow("2026/9/10-2026/9/9")]
    public async Task InvalidDateRangesDoNotProduceExpiryReminders(string dateRange)
    {
        var config = S3Config();
        config.Config.Seasons[0].DateRange = dateRange;
        var service = new EncounterSeasonReminderService(config, new ControlledSettingsStore());

        Assert.IsNull(await service.GetPendingReminderAsync(new DateOnly(2026, 9, 12)));
    }

    [TestMethod]
    public async Task EmptyConfigDoesNotProduceAnExpiryReminder()
    {
        var service = new EncounterSeasonReminderService(new SeasonConfigStub(), new ControlledSettingsStore());

        Assert.IsNull(await service.GetPendingReminderAsync(new DateOnly(2026, 9, 12)));
    }

    private static SeasonConfigStub S3Config() => new()
    {
        Config = new EncounterSeasonConfig
        {
            CurrentSeasonId = "S3",
            Seasons = [Season("S3", "2026/7/16-2026/9/9")]
        }
    };

    private static EncounterSeasonDefinition Season(string id, string dateRange) => new()
    {
        Id = id,
        DateRange = dateRange
    };

    private sealed class SeasonConfigStub : IEncounterSeasonConfigService
    {
        public EncounterSeasonConfig Config { get; init; } = new();
        public EncounterSeasonConfig Load() => Config;
        public EncounterSeasonDefinition? GetCurrentSeason() =>
            Config.Seasons.FirstOrDefault(season => season.Id == Config.CurrentSeasonId);
    }
}
