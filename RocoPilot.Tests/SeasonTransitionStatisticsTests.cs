using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using RocoPilot.Configuration;
using RocoPilot.Models.Encounters;
using RocoPilot.Models.Statistics;
using RocoPilot.Services.Encounters;
using RocoPilot.Services.Statistics;
using RocoPilot.Tests.TestDoubles;
using RocoPilot.ViewModels;
using static RocoPilot.Tests.TemporaryStatisticsFixture;

namespace RocoPilot.Tests;

[TestClass]
public sealed class SeasonTransitionStatisticsTests
{
    [TestMethod]
    public void EndDateIsInclusiveAndNextDayIsTemporary()
    {
        var config = new TemporarySeasonConfig().Load();
        Assert.IsFalse(EncounterSeasonTimeline.IsExpired(config, new(2026, 11, 4)));
        Assert.IsTrue(EncounterSeasonTimeline.IsExpired(config, new(2026, 11, 5)));
        Assert.AreEqual("S4", EncounterSeasonTimeline.ResolveForRecording(config, Date.AddDays(-1), S4).Id);
        Assert.AreEqual(EncounterSeasonTimeline.PendingSeasonId, EncounterSeasonTimeline.ResolveForRecording(config, Date, S4).Id);
    }

    [TestMethod]
    public async Task MultipleMissedSeasonsAreSuggestedByOriginalDatesAndRequireConfirmation()
    {
        var f = await Create();
        await f.Record("a", "小火苗"); await f.Record("b", "小火苗", 1); await f.Record("c", "小火苗", 70);
        Assert.AreEqual(1, f.Items.Count);
        f.Config.Seasons = [S4, S5, S6];
        var restarted = new StatisticsService(f.Store, NullLogger<StatisticsService>.Instance, f.Config,
            new StatisticsSpiritCatalogStub { Document = Catalog });
        await restarted.LoadAsync();
        var account = restarted.CurrentDocument.Accounts.Single();
        Assert.AreEqual(0, account.Seasons.Sum(x => x.Encounters.Count));
        var groups = TemporaryStatisticsItem.Build(account, f.Config.Load());
        Assert.AreEqual(2, groups.Count);
        Assert.AreEqual(2, groups.Single(x => x.SuggestedSeasonId == "S5").Count);
        Assert.AreEqual(1, groups.Single(x => x.SuggestedSeasonId == "S6").Count);
        await restarted.TransferTemporaryStatisticsAsync(groups.Select(Transfer).ToList());
        account = restarted.CurrentDocument.Accounts.Single();
        Assert.IsNull(account.Temporary);
        Assert.AreEqual(2, account.Seasons.Single(x => x.Id == "S5").Encounters.Single().Count);
        Assert.AreEqual(1, account.Seasons.Single(x => x.Id == "S6").Encounters.Single().Count);
    }

    [TestMethod]
    public async Task UnknownOrOverlappingSeasonNeverGuessesAndUnselectedGroupsRemain()
    {
        var f = await Create(); await f.Record("a", "甲"); await f.Record("b", "乙", 70);
        f.Config.AddNextSeason();
        Assert.AreEqual("", f.Items.Single(x => x.Name == "乙").SuggestedSeasonId);
        await f.Service.TransferTemporaryStatisticsAsync([Transfer(f.Items.Single(x => x.Name == "甲"))]);
        Assert.AreEqual("乙", f.Account.Temporary!.Encounters.Single().RawText);
        f.Config.Seasons.Add(new() { Id = "bad", DateRange = S5.DateRange });
        Assert.IsNull(EncounterSeasonTimeline.FindSeason(f.Config.Load(), new(2026, 11, 6)));
    }

    [TestMethod]
    public async Task MigrationAggregatesUnprocessedLegacyAndPhysicallyRemovesHandledData()
    {
        var old = new StatisticsDocument { Accounts = [new() { Uid = "100", PendingEncounters = [
            new() { Id = "a", RawText = "原文", Season = EncounterSeasonTimeline.PendingSeasonId, DetectedAt = Date },
            new() { Id = "b", RawText = "原文", Season = EncounterSeasonTimeline.PendingSeasonId, DetectedAt = Date.AddHours(1) },
            new() { Id = "old-normal", RawText = "旧正常赛季未匹配", Season = "S4", DetectedAt = Date.AddDays(-1) },
            new() { Id = "handled", RawText = "删除内容", Season = "S4", DetectedAt = Date, HandledAt = Date }],
            PendingShinyCaptures = [new() { Id = "ignored", RawText = "删除异色", Season = "S4", DetectedAt = Date, HandledAt = Date }] }] };
        var store = new ControlledSettingsStore(); store.Seed(SettingsKeys.StatisticsData, old);
        var config = new TemporarySeasonConfig(); config.AddNextSeason();
        var service = new StatisticsService(store, NullLogger<StatisticsService>.Instance, config); await service.LoadAsync();
        var account = service.CurrentDocument.Accounts.Single();
        Assert.AreEqual(0, account.PendingEncounters.Count); Assert.AreEqual(0, account.PendingShinyCaptures.Count);
        Assert.AreEqual(2, account.Temporary!.Encounters.Count);
        Assert.AreEqual(2, account.Temporary.Encounters.Single(x => x.RawText == "原文").Count);
        Assert.AreEqual(0, account.Seasons.Sum(x => x.Encounters.Count));
        await service.MergeRemoteAsync(old);
        Assert.AreEqual(3, service.CurrentDocument.Accounts.Single().Temporary!.Encounters.Sum(x => x.Count));
        var restart = new StatisticsService(store, NullLogger<StatisticsService>.Instance, config); await restart.LoadAsync();
        Assert.AreEqual(3, restart.CurrentDocument.Accounts.Single().Temporary!.Encounters.Sum(x => x.Count));
    }

    [TestMethod]
    public async Task TemporaryShinyCanBeConfirmedNowAndArchivedLaterWithoutSecondReset()
    {
        var f = await Create(); await f.Record("a", "烈火王"); await f.Record("b", "烈火王", 1);
        await f.Service.RefreshTemporaryNamesAsync(Catalog);
        await f.Service.AddPendingShinyCaptureAsync(S4, "小火苗", Date.AddDays(2), "shiny", "烈火王");
        var overview = new StatisticsOverviewViewModel(); overview.ApplyDocument(f.Service.CurrentDocument, "100", f.Config.Load());
        Assert.IsNotNull(overview.LatestPendingShinyCapture);
        Assert.AreEqual(2d, overview.PendingEditor.EncounterCount);
        await f.Service.ConfirmPendingShinyCaptureAsync("shiny", "小火苗", null, Date.AddDays(2));
        await f.Service.ConfirmPendingShinyCaptureAsync("shiny", "小火苗", null, Date.AddDays(2));
        Assert.IsNull(f.Account.Temporary); Assert.AreEqual(0, f.Account.PendingShinyCaptures.Count);
        var capture = f.Account.Seasons.Single().ShinyCaptures.Single();
        Assert.AreEqual(2, capture.EncounterCountBeforeCapture); Assert.AreEqual(Date.AddDays(2), capture.CapturedAt);
        await f.Record("after", "小火苗", 3);
        f.Config.AddNextSeason();
        await f.Service.RecordEncounterAsync(S5, "小火苗", Date.AddDays(4));
        var items = f.Items;
        await f.Service.TransferTemporaryStatisticsAsync([Transfer(items.Single(x => x.IsShiny))]);
        Assert.AreEqual(1, f.Account.Temporary!.Encounters.Sum(x => x.Count));
        Assert.AreEqual(1, f.Account.Seasons.Single(x => x.Id == "S5").Encounters.Single().Count);
        Assert.AreEqual(2, f.Account.Seasons.Single(x => x.Id == "S5").ShinyCaptures.Single().EncounterCountBeforeCapture);
        await f.Service.TransferTemporaryStatisticsAsync([Transfer(f.Items.Single())]);
        Assert.AreEqual(2, f.Account.Seasons.Single(x => x.Id == "S5").Encounters.Single().Count);
        Assert.IsNull(f.Account.Temporary);
        Assert.IsFalse(f.Account.Seasons.Any(x => x.Id == EncounterSeasonTimeline.PendingSeasonId));
    }

    [TestMethod]
    public void ExpiredConfigShowsEmptyTemporaryCardAndCorrespondingShinyScope()
    {
        var config = new EncounterSeasonConfig { Seasons = [new() { Id = "old", Name = "旧赛季", DateRange = "2020/1/1-2020/2/1" }] };
        var view = new StatisticsOverviewViewModel(); view.ApplyDocument(new(), null, config);
        Assert.AreEqual(EncounterSeasonTimeline.PendingSeasonId, view.Seasons.First().Id);
        Assert.AreEqual("新赛季", view.Seasons.First().EncounterTitle);
        Assert.IsTrue(view.ShinyScopes.Any(x => x.Name == "新赛季"));
    }
    [TestMethod]
    public async Task LegacyTemporaryShinyOnlyMigrationRecognizesItsOldCloudSnapshot()
    {
        var old = new StatisticsDocument { Accounts = [new() { Uid = "100", PendingShinyCaptures = [new()
        {
            Id = "legacy-shiny", RawText = "烈火王", Season = EncounterSeasonTimeline.PendingSeasonId, DetectedAt = Date
        }] }] };
        var store = new ControlledSettingsStore(); store.Seed(SettingsKeys.StatisticsData, old);
        var config = new TemporarySeasonConfig();
        var service = new StatisticsService(store, NullLogger<StatisticsService>.Instance, config,
            new StatisticsSpiritCatalogStub { Document = Catalog });
        await service.LoadAsync();
        await service.ConfirmPendingShinyCaptureAsync("legacy-shiny", "小火苗", 15, Date);
        await service.MergeRemoteAsync(old);
        var account = service.CurrentDocument.Accounts.Single();
        Assert.AreEqual(0, account.PendingShinyCaptures.Count);
        Assert.AreEqual(15, account.Seasons.Single().ShinyCaptures.Single().EncounterCountBeforeCapture);
    }

}
