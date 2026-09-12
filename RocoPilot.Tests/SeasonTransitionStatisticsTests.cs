using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using RocoPilot.Configuration;
using RocoPilot.Contracts.Services.Encounters;
using RocoPilot.Models.Encounters;
using RocoPilot.Models.Spirits;
using RocoPilot.Models.Statistics;
using RocoPilot.Services.Encounters;
using RocoPilot.Services.Statistics;
using RocoPilot.Tests.TestDoubles;
using RocoPilot.ViewModels;

namespace RocoPilot.Tests;

[TestClass]
public sealed class SeasonTransitionStatisticsTests
{
    private static readonly EncounterSeasonDefinition S3 = new() { Id = "S3", Name = "S3赛季", DateRange = "2026/7/16-2026/9/9" };
    private static readonly EncounterSeasonDefinition S4 = new() { Id = "S4", Name = "S4赛季", DateRange = "2026/9/10-2026/11/4" };
    private static readonly EncounterSeasonDefinition S5 = new() { Id = "S5", Name = "S5赛季", DateRange = "2026/11/5-2027/1/6" };
    private static readonly DateTimeOffset FirstS4Day = new(2026, 9, 10, 0, 0, 0, TimeSpan.FromHours(8));
    private static readonly DateTimeOffset FirstS5Day = new(2026, 11, 5, 0, 0, 0, TimeSpan.FromHours(8));

    [TestMethod]
    public async Task EndDateRemainsInOldSeasonAndNextDayStoresEveryEncounter()
    {
        var (service, store) = await CreateAsync(S3);
        await service.RecordEncounterAsync(S3, "小火苗", FirstS4Day.AddTicks(-1));
        await service.RecordEncounterAsync(S3, "小火苗", FirstS4Day);
        await service.AddPendingEncounterAsync("100", S3, "unknown", " 新精灵原始 OCR ", FirstS4Day.AddMinutes(1));
        Assert.AreEqual(1, Count(service, "S3"));
        Assert.AreEqual(2, Account(service).PendingEncounters.Count);
        var known = Account(service).PendingEncounters.Single(item => item.Name is not null);
        Assert.AreEqual("小火苗", known.Name);
        Assert.AreEqual(FirstS4Day, known.DetectedAt);
        Assert.AreEqual(EncounterSeasonTimeline.PendingSeasonId, known.Season);
        Assert.IsNull(known.HandledAt);
        var restarted = Service(store, S3);
        await restarted.LoadAsync();
        Assert.AreEqual(2, Account(restarted).PendingEncounters.Count);
        Assert.AreEqual(" 新精灵原始 OCR ", Account(restarted).PendingEncounters.Single(item => item.Id == "unknown").RawText);
        Assert.AreEqual(1, Count(restarted, "S3"));
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task CatalogAndSeasonCanBeUpdatedInEitherOrder(bool catalogFirst)
    {
        var (service, store) = await CreateAsync(S3);
        await service.AddPendingEncounterAsync("100", S3, "event", "烈火王", FirstS4Day);
        if (catalogFirst)
        {
            Assert.AreEqual(1, await service.RematchPendingEncountersAsync(Catalog(), 0.55));
            Assert.AreEqual("小火苗", Account(service).PendingEncounters.Single().Name);
            Assert.AreEqual(0, Count(service, "S3"));
            Assert.IsNull(Account(service).PendingEncounters.Single().HandledAt);
        }

        var updated = Service(store, S3, S4);
        await updated.LoadAsync();
        Assert.AreEqual("S4", Account(updated).PendingEncounters.Single().Season);
        if (!catalogFirst)
        {
            Assert.AreEqual(0, Count(updated, "S4"));
            Assert.AreEqual(1, await updated.RematchPendingEncountersAsync(Catalog(), 0.55));
        }
        Assert.AreEqual(1, Count(updated, "S4"));
        Assert.AreEqual(0, Count(updated, "S3"));
        Assert.AreEqual(FirstS4Day, Account(updated).Seasons.Single(item => item.Id == "S4").Encounters.Single().LastCapturedAt);
        Assert.AreEqual(0, await updated.RematchPendingEncountersAsync(Catalog(), 0.55));
        var restarted = Service(store, S3, S4);
        await restarted.LoadAsync();
        Assert.AreEqual(1, Count(restarted, "S4"));
    }

    [TestMethod]
    public async Task SoftwareUpdatedFirstAlsoPreservesSubsequentMissingNames()
    {
        var (service, _) = await CreateAsync(S3, S4);
        await service.AddPendingEncounterAsync("100", S3, "event", "烈火王", FirstS4Day);
        Assert.AreEqual("S4", Account(service).PendingEncounters.Single().Season);
        Assert.AreEqual(0, Count(service, "S3"));
        Assert.AreEqual(1, await service.RematchPendingEncountersAsync(Catalog(), 0.55));
        Assert.AreEqual(1, Count(service, "S4"));
    }

    [TestMethod]
    public async Task StartupCanUseUpdatedBundledCatalogWithoutNetworkSync()
    {
        var (service, store) = await CreateAsync(S3);
        await service.AddPendingEncounterAsync("100", S3, "event", "烈火王", FirstS4Day);
        var catalog = new StatisticsSpiritCatalogStub { Document = Catalog() };
        var updated = new StatisticsService(store, NullLogger<StatisticsService>.Instance, Config(S3, S4), catalog);
        await updated.LoadAsync();
        Assert.AreEqual(1, catalog.LoadCount);
        Assert.AreEqual(1, Count(updated, "S4"));
        Assert.IsNotNull(store.ReadSaved<StatisticsDocument>(SettingsKeys.StatisticsData)!.Accounts.Single().PendingEncounters.Single().HandledAt);
    }

    [TestMethod]
    public async Task CatalogReadFailureKeepsUnmatchedDataAndAllowsLaterSync()
    {
        var (service, store) = await CreateAsync(S3);
        await service.AddPendingEncounterAsync("100", S3, "event", "烈火王", FirstS4Day);
        var catalog = new StatisticsSpiritCatalogStub { BeforeLoad = () => throw new IOException("catalog unavailable") };
        var updated = new StatisticsService(store, NullLogger<StatisticsService>.Instance, Config(S3, S4), catalog);
        await updated.LoadAsync();
        Assert.AreEqual("烈火王", Account(updated).PendingEncounters.Single().RawText);
        Assert.AreEqual(0, Count(updated, "S4"));
        await updated.RematchPendingEncountersAsync(Catalog(), 0.55);
        Assert.AreEqual(1, Count(updated, "S4"));
    }

    [TestMethod]
    public async Task MissingNamesStillUseOriginalSimilarityThreshold()
    {
        var (service, store) = await CreateAsync(S3);
        await service.AddPendingEncounterAsync("100", S3, "event", "小火猫", FirstS4Day);
        Assert.AreEqual(0, await service.RematchPendingEncountersAsync(Catalog(), 0.9));
        Assert.AreEqual(1, await service.RematchPendingEncountersAsync(Catalog(), 0.55));
        Assert.AreEqual(0, Count(service, "S3"));
        var updated = Service(store, S3, S4);
        await updated.LoadAsync();
        Assert.AreEqual(1, Count(updated, "S4"));
    }

    [TestMethod]
    public async Task UnmatchedBlankOcrSurvivesSeasonUpdateForManualFallback()
    {
        var (service, store) = await CreateAsync(S3);
        await service.AddPendingEncounterAsync("100", S3, "event", "", FirstS4Day);
        var updated = Service(store, S3, S4);
        await updated.LoadAsync();
        Assert.AreEqual(0, await updated.RematchPendingEncountersAsync(Catalog(), 0.55));
        Assert.AreEqual(PendingEncounterConfirmationResult.Counted,
            await updated.ConfirmPendingEncounterAsync("100", "event", "新精灵"));
        Assert.AreEqual(1, Count(updated, "S4"));
    }

    [TestMethod]
    public async Task ManualNameCanBeSavedBeforeSeasonIsKnown()
    {
        var (service, store) = await CreateAsync(S3);
        await service.AddPendingEncounterAsync("100", S3, "event", "误识别", FirstS4Day);
        Assert.AreEqual(PendingEncounterConfirmationResult.AwaitingSeason,
            await service.ConfirmPendingEncounterAsync("100", "event", "用户确认的名称"));
        Assert.AreEqual(0, Count(service, "S3"));
        Assert.AreEqual(0, await service.RematchPendingEncountersAsync(Catalog(), 0.55));
        var updated = Service(store, S3, S4);
        await updated.LoadAsync();
        Assert.AreEqual("用户确认的名称", Account(updated).Seasons.Single(item => item.Id == "S4").Encounters.Single().Name);
    }

    [TestMethod]
    public async Task MultipleMissingSeasonsSplitByOccurrenceDateAndAccount()
    {
        var (service, store) = await CreateAsync(S3);
        await service.AddAccountAsync("200");
        await service.RecordEncounterAsync(S3, "小火苗", FirstS4Day, "100");
        await service.RecordEncounterAsync(S3, "小火苗", FirstS5Day.AddTicks(-1), "100");
        await service.RecordEncounterAsync(S3, "小火苗", FirstS5Day, "100");
        await service.RecordEncounterAsync(S3, "小火苗", FirstS5Day, "200");
        var updated = Service(store, S3, S4, S5);
        await updated.LoadAsync();
        Assert.AreEqual(2, Count(updated, "S4"));
        Assert.AreEqual(1, Count(updated, "S5"));
        var other = updated.CurrentDocument.Accounts.Single(item => item.Uid == "200");
        Assert.AreEqual("S5", other.Seasons.Single().Id);
        Assert.AreEqual(1, other.Seasons.Single().Encounters.Single().Count);
    }

    [TestMethod]
    public async Task PartialSeasonUpdateLeavesLaterEventsInStaging()
    {
        var (service, store) = await CreateAsync(S3);
        await service.RecordEncounterAsync(S3, "小火苗", FirstS4Day);
        await service.RecordEncounterAsync(S3, "小火苗", FirstS5Day);
        var partial = Service(store, S3, S4);
        await partial.LoadAsync();
        Assert.AreEqual(1, Count(partial, "S4"));
        Assert.AreEqual(EncounterSeasonTimeline.PendingSeasonId, Account(partial).PendingEncounters.Single(item => item.HandledAt is null).Season);
        var updated = Service(store, S3, S4, S5);
        await updated.LoadAsync();
        Assert.AreEqual(1, Count(updated, "S4"));
        Assert.AreEqual(1, Count(updated, "S5"));
    }

    [TestMethod]
    public async Task FailedMigrationIsNotPublishedAndRetryDoesNotDuplicateCounts()
    {
        var (service, store) = await CreateAsync(S3);
        await service.RecordEncounterAsync(S3, "小火苗", FirstS4Day);
        store.BeforeSave = _ => throw new IOException("save failed");
        var updated = Service(store, S3, S4);
        await Assert.ThrowsExactlyAsync<IOException>(() => updated.LoadAsync());
        Assert.AreEqual(0, updated.CurrentDocument.Accounts.Count);
        var saved = store.ReadSaved<StatisticsDocument>(SettingsKeys.StatisticsData)!.Accounts.Single().PendingEncounters.Single();
        Assert.IsNull(saved.HandledAt);
        Assert.AreEqual(EncounterSeasonTimeline.PendingSeasonId, saved.Season);
        store.BeforeSave = null;
        await updated.LoadAsync();
        Assert.AreEqual(1, Count(updated, "S4"));
        await updated.LoadAsync();
        Assert.AreEqual(1, Count(updated, "S4"));
    }

    [TestMethod]
    public async Task ImportAndCloudMergeUseCurrentSeasonConfigAndKeepMigrationIdempotent()
    {
        var (service, _) = await CreateAsync(S3);
        await service.RecordEncounterAsync(S3, "小火苗", FirstS4Day);
        var oldCloud = service.CurrentDocument;
        var (updated, _) = await CreateAsync(S3, S4);
        await updated.ReplaceAsync(oldCloud);
        Assert.AreEqual(1, Count(updated, "S4"));
        await updated.MergeRemoteAsync(oldCloud, null, false);
        Assert.AreEqual(1, Count(updated, "S4"));
        Assert.IsNotNull(Account(updated).PendingEncounters.Single().HandledAt);
    }

    [TestMethod]
    public async Task ShinyResetAfterSeasonUpdatePreventsOldUnknownNameEnteringNewCycle()
    {
        var (service, store) = await CreateAsync(S3);
        await service.AddPendingEncounterAsync("100", S3, "event", "小火苗", FirstS4Day);
        var updated = Service(store, S3, S4);
        await updated.LoadAsync();
        updated.SetSelectedAccountUid("100");
        await updated.AddShinyCapturesAsync("S4", "小火苗", 1, FirstS4Day.AddDays(1), true);
        Assert.AreEqual(0, await updated.RematchPendingEncountersAsync(Catalog(), 0.55));
        Assert.AreEqual(0, Count(updated, "S4"));
        Assert.IsNull(Account(updated).PendingEncounters.Single().HandledAt);
    }

    [TestMethod]
    public async Task PendingShiniesDoNotOverwriteEachOtherOrResetOldSeasonWhileSeasonUnknown()
    {
        var (service, store) = await CreateAsync(S3);
        await service.RecordEncounterAsync(S3, "小火苗", FirstS4Day.AddDays(-1));
        await service.AddPendingShinyCaptureAsync(S3, "小火苗", FirstS4Day);
        await service.AddPendingShinyCaptureAsync(S3, "小火苗", FirstS5Day);
        var shinyId = Account(service).PendingShinyCaptures.First().Id;
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => service.ConfirmPendingShinyCaptureAsync(shinyId, "小火苗", 10, FirstS5Day));
        Assert.AreEqual(1, Count(service, "S3"));
        Assert.AreEqual(2, Account(service).PendingShinyCaptures.Count);
        var overview = new StatisticsOverviewViewModel();
        overview.ApplyDocument(service.CurrentDocument, "100", Config(S3).Load());
        Assert.AreEqual(Microsoft.UI.Xaml.Visibility.Collapsed, overview.PendingShinyConfirmationVisibility);
        Assert.IsFalse(overview.Seasons.Any(season => season.Id == EncounterSeasonTimeline.PendingSeasonId));
        var updated = Service(store, S3, S4, S5);
        await updated.LoadAsync();
        CollectionAssert.AreEquivalent(new[] { "S4", "S5" }, Account(updated).PendingShinyCaptures.Select(item => item.Season).ToArray());
        overview.ApplyDocument(updated.CurrentDocument, "100", Config(S3, S4, S5).Load());
        Assert.AreEqual(Microsoft.UI.Xaml.Visibility.Visible, overview.PendingShinyConfirmationVisibility);
    }

    [TestMethod]
    public async Task LegacyUnconfirmedRecordsCanBeReassignedButAggregatesAreLeftIntact()
    {
        var store = new ControlledSettingsStore();
        var legacy = new StatisticsService(store, NullLogger<StatisticsService>.Instance);
        await legacy.AddAccountAsync("100");
        legacy.SetSelectedAccountUid("100");
        legacy.SetActiveAccountUid("100");
        await legacy.RecordEncounterAsync(S3, "已有汇总", FirstS4Day);
        await legacy.AddPendingEncounterAsync("100", S3, "event", "小火苗", FirstS4Day);
        await legacy.AddPendingShinyCaptureAsync(S3, "小火苗", FirstS4Day);
        var updated = Service(store, S3);
        await updated.LoadAsync();
        Assert.AreEqual(1, Count(updated, "S3"));
        Assert.AreEqual(EncounterSeasonTimeline.PendingSeasonId, Account(updated).PendingEncounters.Single().Season);
        Assert.AreEqual(EncounterSeasonTimeline.PendingSeasonId, Account(updated).PendingShinyCaptures.Single().Season);
    }

    [TestMethod]
    public async Task KnownDatesOverrideStaleCurrentSeasonIdWithoutUsingStaging()
    {
        var (service, _) = await CreateAsync(S3, S4);
        await service.RecordEncounterAsync(S3, "小火苗", FirstS4Day);
        Assert.AreEqual(1, Count(service, "S4"));
        Assert.AreEqual(0, Account(service).PendingEncounters.Count);
    }

    [TestMethod]
    public async Task DiscardedStagingDoesNotReappearAfterUpdate()
    {
        var (service, store) = await CreateAsync(S3);
        await service.AddPendingEncounterAsync("100", S3, "event", "小火苗", FirstS4Day);
        await service.DiscardPendingEncounterAsync("100", "event");
        var updated = Service(store, S3, S4);
        await updated.LoadAsync();
        Assert.AreEqual(0, Count(updated, "S4"));
    }

    [TestMethod]
    public async Task OverviewExplainsWhichInformationIsStillMissing()
    {
        var (service, _) = await CreateAsync(S3);
        await service.RecordEncounterAsync(S3, "小火苗", FirstS4Day);
        var overview = new StatisticsOverviewViewModel();
        overview.ApplyDocument(service.CurrentDocument, "100", Config(S3).Load());
        var item = overview.PendingEncounters.Single();
        Assert.IsTrue(item.IsSeasonPending);
        Assert.AreEqual("赛季待更新", item.SeasonDisplay);
        Assert.AreEqual("小火苗", item.NameDisplay);
    }

    [TestMethod]
    public void TimelineUsesLatestValidEndAndDoesNotGuessMissingOrOverlappingRanges()
    {
        Assert.IsFalse(EncounterSeasonTimeline.IsExpired(new EncounterSeasonConfig(), DateOnly.FromDateTime(FirstS4Day.DateTime)));
        var config = Config(S5, new EncounterSeasonDefinition { Id = "bad", DateRange = "2027/1/1-2026/1/1" }, S3).Load();
        Assert.IsFalse(EncounterSeasonTimeline.IsExpired(config, DateOnly.FromDateTime(FirstS4Day.DateTime)));
        Assert.IsNull(EncounterSeasonTimeline.FindSeason(config, DateOnly.FromDateTime(FirstS4Day.DateTime)));
        config.Seasons = [S4, new() { Id = "overlap", DateRange = S4.DateRange }];
        Assert.IsNull(EncounterSeasonTimeline.FindSeason(config, DateOnly.FromDateTime(FirstS4Day.DateTime)));
    }

    private static async Task<(StatisticsService, ControlledSettingsStore)> CreateAsync(params EncounterSeasonDefinition[] seasons)
    {
        var store = new ControlledSettingsStore();
        var service = Service(store, seasons);
        await service.AddAccountAsync("100");
        service.SetSelectedAccountUid("100");
        service.SetActiveAccountUid("100");
        return (service, store);
    }

    private static StatisticsService Service(ControlledSettingsStore store, params EncounterSeasonDefinition[] seasons) =>
        new(store, NullLogger<StatisticsService>.Instance, Config(seasons));
    private static SeasonConfig Config(params EncounterSeasonDefinition[] seasons) => new(new EncounterSeasonConfig { CurrentSeasonId = "S3", Seasons = [.. seasons] });
    private static AccountStatisticsData Account(StatisticsService service) => service.CurrentDocument.Accounts.Single(item => item.Uid == "100");
    private static int Count(StatisticsService service, string seasonId) => Account(service).Seasons.FirstOrDefault(item => item.Id == seasonId)?.Encounters.Sum(item => item.Count) ?? 0;
    private static SpiritCatalogDocument Catalog() => new()
    {
        Spirits =
        [
            new() { Id = "1", Name = "小火苗", BaseId = "1", BaseName = "小火苗", ChainId = "fire" },
            new() { Id = "2", Name = "烈火王", BaseId = "1", BaseName = "小火苗", ChainId = "fire" }
        ]
    };

    private sealed class SeasonConfig(EncounterSeasonConfig config) : IEncounterSeasonConfigService
    {
        public EncounterSeasonConfig Load() => config;
        public EncounterSeasonDefinition? GetCurrentSeason() => config.Seasons.FirstOrDefault();
    }
}
