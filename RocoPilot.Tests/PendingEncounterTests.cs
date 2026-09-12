using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using RocoPilot.Configuration;
using RocoPilot.Models.Encounters;
using RocoPilot.Models.Spirits;
using RocoPilot.Models.Statistics;
using RocoPilot.Services.Statistics;
using RocoPilot.Tests.TestDoubles;
using RocoPilot.ViewModels;

namespace RocoPilot.Tests;

[TestClass]
public sealed class PendingEncounterTests
{
    private static readonly DateTimeOffset OccurredAt = new(2026, 9, 10, 10, 0, 0, TimeSpan.FromHours(8));
    private static readonly EncounterSeasonDefinition Season = new() { Id = "S3", Name = "S3赛季" };

    [TestMethod]
    [DataRow("  未知精灵\n原始文字  ")]
    [DataRow("")]
    public async Task UnmatchedEncounterSurvivesRestartWithoutCreatingANameEntry(string rawText)
    {
        var (service, store) = await CreateAsync();
        await service.AddPendingEncounterAsync("100", Season, "event", rawText, OccurredAt);
        var restarted = new StatisticsService(store, NullLogger<StatisticsService>.Instance);
        var document = await restarted.LoadAsync();
        var account = document.Accounts.Single();
        var pending = account.PendingEncounters.Single();
        Assert.AreEqual("100", account.Uid);
        Assert.AreEqual(rawText, pending.RawText);
        Assert.AreEqual("S3", pending.Season);
        Assert.AreEqual(OccurredAt, pending.DetectedAt);
        Assert.AreEqual(0, account.Seasons.Single().Encounters.Count);
    }

    [TestMethod]
    public async Task RepeatedObservationAndConfirmationOnlyCountOnce()
    {
        var (service, _) = await CreateAsync();
        await service.AddPendingEncounterAsync("100", Season, "event", "新精灵", OccurredAt);
        await service.AddPendingEncounterAsync("100", Season, "event", "其他文字", OccurredAt.AddSeconds(1));
        Assert.AreEqual(PendingEncounterConfirmationResult.Counted,
            await service.ConfirmPendingEncounterAsync("100", "event", "新精灵"));
        Assert.AreEqual(PendingEncounterConfirmationResult.NotFound,
            await service.ConfirmPendingEncounterAsync("100", "event", "新精灵"));
        await service.AddPendingEncounterAsync("100", Season, "event", "新精灵", OccurredAt);
        Assert.AreEqual(1, Account(service).PendingEncounters.Count);
        Assert.AreEqual("新精灵", Account(service).PendingEncounters.Single().RawText);
        Assert.AreEqual(1, Account(service).Seasons.Single().Encounters.Single().Count);
        Assert.AreEqual(0, PendingCount(service));
    }

    [TestMethod]
    public async Task CatalogRematchingUsesExistingSimilarityThresholdAndEvolutionBaseAcrossAccounts()
    {
        var (service, _) = await CreateAsync();
        await service.AddAccountAsync("200");
        await service.AddPendingEncounterAsync("100", Season, "one", "小火猫", OccurredAt);
        await service.AddPendingEncounterAsync("200", Season, "two", "烈火王", OccurredAt.AddMinutes(1));
        await service.AddPendingEncounterAsync("100", Season, "unknown", "完全未知", OccurredAt);
        // 相似名称必须继续按原门槛处理，而不是改成精确匹配。
        Assert.AreEqual(2, await service.RematchPendingEncountersAsync(Catalog(), 0.55));
        Assert.AreEqual(0, await service.RematchPendingEncountersAsync(Catalog(), 0.55));
        foreach (var account in service.CurrentDocument.Accounts)
        {
            var encounter = account.Seasons.Single().Encounters.Single();
            Assert.AreEqual("小火苗", encounter.Name);
            Assert.AreEqual(1, encounter.Count);
            Assert.AreEqual("S3", encounter.Season);
        }
        Assert.AreEqual(OccurredAt, Account(service).Seasons.Single().Encounters.Single().LastCapturedAt);
        Assert.AreEqual(1, PendingCount(service));
    }

    [TestMethod]
    public async Task RematchingRespectsConfiguredThresholdAndKeepsLatestCounterTimestamp()
    {
        var (service, _) = await CreateAsync();
        await service.RecordEncounterAsync(Season, "小火苗", OccurredAt.AddDays(1));
        await service.AddPendingEncounterAsync("100", Season, "event", "小火猫", OccurredAt);
        Assert.AreEqual(0, await service.RematchPendingEncountersAsync(Catalog(), 0.9));
        Assert.AreEqual(1, await service.RematchPendingEncountersAsync(Catalog(), 0.55));
        var record = Account(service).Seasons.Single().Encounters.Single();
        Assert.AreEqual(2, record.Count);
        Assert.AreEqual(OccurredAt.AddDays(1), record.LastCapturedAt);
    }

    [TestMethod]
    public async Task FailedWritesLeavePendingAndCountersUnchangedUntilRetry()
    {
        var (service, store) = await CreateAsync();
        store.BeforeSave = _ => throw new IOException("save failed");
        await Assert.ThrowsExactlyAsync<IOException>(() => service.AddPendingEncounterAsync("100", Season, "event", "小火苗", OccurredAt));
        Assert.AreEqual(0, Account(service).PendingEncounters.Count);
        store.BeforeSave = null;
        await service.AddPendingEncounterAsync("100", Season, "event", "小火苗", OccurredAt);
        store.BeforeSave = _ => throw new IOException("save failed");
        await Assert.ThrowsExactlyAsync<IOException>(() => service.RematchPendingEncountersAsync(Catalog(), 0.55));
        Assert.AreEqual(1, PendingCount(service));
        Assert.AreEqual(0, Account(service).Seasons.Single().Encounters.Count);
        store.BeforeSave = null;
        Assert.AreEqual(1, await service.RematchPendingEncountersAsync(Catalog(), 0.55));
        Assert.AreEqual(0, PendingCount(service));
    }

    [TestMethod]
    public async Task ManualConfirmationRacingCatalogSyncDoesNotCountTwice()
    {
        var (service, store) = await CreateAsync();
        await service.AddPendingEncounterAsync("100", Season, "event", "小火苗", OccurredAt);
        using var pause = new AsyncPause();
        store.BeforeSave = pause.PauseAsync;
        var manual = service.ConfirmPendingEncounterAsync("100", "event", "小火苗");
        await pause.WaitUntilEnteredAsync();
        var rematch = service.RematchPendingEncountersAsync(Catalog(), 0.55);
        pause.Dispose();
        await manual;
        Assert.AreEqual(0, await rematch);
        Assert.AreEqual(1, Account(service).Seasons.Single().Encounters.Single().Count);
    }

    [TestMethod]
    public async Task ConfirmationKeepsOriginalAccountWhileSelectionChanges()
    {
        var (service, store) = await CreateAsync();
        await service.AddAccountAsync("200");
        await service.AddPendingEncounterAsync("100", Season, "event", "未知", OccurredAt);
        using var pause = new AsyncPause();
        store.BeforeSave = pause.PauseAsync;
        var blocker = service.UpsertEncounterAsync("S3", "已有", 1, OccurredAt);
        await pause.WaitUntilEnteredAsync();
        var confirmation = service.ConfirmPendingEncounterAsync("100", "event", "新精灵");
        service.SetSelectedAccountUid("200");
        service.SetActiveAccountUid("200");
        pause.Dispose();
        await blocker;
        await confirmation;
        Assert.AreEqual(0, service.CurrentDocument.Accounts.Single(item => item.Uid == "200").Seasons.Count);
        Assert.AreEqual(1, Account(service).Seasons.Single().Encounters.Single(item => item.Name == "新精灵").Count);
    }

    [TestMethod]
    public async Task DeletedOriginalAccountDoesNotRedirectConfirmation()
    {
        var (service, _) = await CreateAsync();
        await service.AddAccountAsync("200");
        await service.AddPendingEncounterAsync("100", Season, "event", "未知", OccurredAt);
        await service.DeleteAccountAsync("100");
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => service.ConfirmPendingEncounterAsync("100", "event", "新精灵"));
        Assert.AreEqual(0, service.CurrentDocument.Accounts.Single().Seasons.Count);
    }

    [TestMethod]
    public async Task OldPendingStaysForReviewAfterShinyResetEvenIfShinyIsDeleted()
    {
        var (service, _) = await CreateAsync();
        await service.AddPendingEncounterAsync("100", Season, "old", "小火苗", OccurredAt);
        await service.AddPendingShinyCaptureAsync(Season, "小火苗", OccurredAt.AddMinutes(1));
        var shiny = Account(service).PendingShinyCaptures.Single();
        await service.ConfirmPendingShinyCaptureAsync(shiny.Id, "小火苗", 20, OccurredAt.AddMinutes(2));
        await service.DeleteShinyCaptureAsync(Account(service).Seasons.Single().ShinyCaptures.Single().Id);
        await service.AddPendingEncounterAsync("100", Season, "new", "小火苗", OccurredAt.AddMinutes(3));
        Assert.AreEqual(1, await service.RematchPendingEncountersAsync(Catalog(), 0.55));
        Assert.AreEqual(PendingEncounterConfirmationResult.BeforeReset,
            await service.ConfirmPendingEncounterAsync("100", "old", "小火苗"));
        Assert.AreEqual(1, PendingCount(service));
        Assert.AreEqual(1, Account(service).Seasons.Single().Encounters.Single().Count);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task HistoricalShinyEntryDoesNotResetButCurrentShinyEntryDoes(bool reset)
    {
        var (service, _) = await CreateAsync();
        await service.AddPendingEncounterAsync("100", Season, "event", "小火苗", OccurredAt);
        await service.AddShinyCapturesAsync("S3", "小火苗", 1, OccurredAt.AddMinutes(1), reset);
        Assert.AreEqual(reset ? 0 : 1, await service.RematchPendingEncountersAsync(Catalog(), 0.55));
    }

    [TestMethod]
    public async Task ResetDoesNotAffectAnotherSpiritOrSeason()
    {
        var (service, _) = await CreateAsync();
        await service.AddShinyCapturesAsync("S3", "小火苗", 1, OccurredAt, true);
        await service.AddPendingEncounterAsync("100", Season, "other-name", "其他精灵", OccurredAt);
        await service.AddPendingEncounterAsync("100", new EncounterSeasonDefinition { Id = "S4" }, "other-season", "小火苗", OccurredAt);
        Assert.AreEqual(PendingEncounterConfirmationResult.Counted,
            await service.ConfirmPendingEncounterAsync("100", "other-name", "其他精灵"));
        Assert.AreEqual(PendingEncounterConfirmationResult.Counted,
            await service.ConfirmPendingEncounterAsync("100", "other-season", "小火苗"));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task LegacyCloudMergeDoesNotReviveHandledPending(bool discard)
    {
        var (service, _) = await CreateAsync();
        await service.AddPendingEncounterAsync("100", Season, "event", "小火苗", OccurredAt);
        var stale = service.CurrentDocument;
        if (discard) await service.DiscardPendingEncounterAsync("100", "event");
        else await service.ConfirmPendingEncounterAsync("100", "event", "小火苗");
        await service.MergeRemoteAsync(stale, null, false);
        Assert.AreEqual(0, PendingCount(service));
        Assert.AreEqual(0, await service.RematchPendingEncountersAsync(Catalog(), 0.55));
        Assert.AreEqual(discard ? 0 : 1, Account(service).Seasons.Single().Encounters.Sum(item => item.Count));
    }

    [TestMethod]
    public async Task EmptyNewFieldsPreserveExistingCloudAccountFingerprint()
    {
        var (service, _) = await CreateAsync();
        await service.RecordEncounterAsync(Season, "小火苗", OccurredAt);
        var account = Account(service);
        var oldShape = new
        {
            account.Uid,
            Seasons = account.Seasons.Select(season => new
            {
                season.Id, season.Name, season.DateRange, season.EncounterTypeName,
                season.Encounters, season.ShinyCaptures
            }),
            account.PendingShinyCaptures
        };
        var oldJson = JsonSerializer.Serialize(oldShape, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true });
        var oldFingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(oldJson))).ToLowerInvariant();
        Assert.AreEqual(oldFingerprint, StatisticsDocumentMerger.ComputeAccountFingerprints(service.CurrentDocument)["100"]);
    }

    [TestMethod]
    public async Task OverviewShowsOnlySelectedAccountsUnresolvedEncounters()
    {
        var (service, _) = await CreateAsync();
        await service.AddAccountAsync("200");
        await service.AddPendingEncounterAsync("100", Season, "one", "甲", OccurredAt);
        await service.AddPendingEncounterAsync("100", Season, "handled", "乙", OccurredAt);
        await service.DiscardPendingEncounterAsync("100", "handled");
        await service.AddPendingEncounterAsync("200", Season, "two", "丙", OccurredAt);
        var overview = new StatisticsOverviewViewModel();
        overview.ApplyDocument(service.CurrentDocument, "100", new EncounterSeasonConfig());
        Assert.AreEqual(1, overview.PendingEncounterCount);
        Assert.AreEqual("甲", overview.PendingEncounters.Single().RawText);
        overview.ApplyDocument(service.CurrentDocument, "200", new EncounterSeasonConfig());
        Assert.AreEqual("200", overview.PendingEncounters.Single().AccountUid);
        Assert.AreEqual("丙", overview.PendingEncounters.Single().RawText);
    }

    private static async Task<(StatisticsService, ControlledSettingsStore)> CreateAsync()
    {
        var store = new ControlledSettingsStore();
        var service = new StatisticsService(store, NullLogger<StatisticsService>.Instance);
        await service.AddAccountAsync("100");
        service.SetSelectedAccountUid("100");
        service.SetActiveAccountUid("100");
        return (service, store);
    }

    private static AccountStatisticsData Account(StatisticsService service) => service.CurrentDocument.Accounts.Single(item => item.Uid == "100");
    private static int PendingCount(StatisticsService service) => Account(service).PendingEncounters.Count(item => item.HandledAt is null);
    private static SpiritCatalogDocument Catalog() => new()
    {
        Spirits =
        [
            new() { Id = "1", Name = "小火苗", BaseId = "1", BaseName = "小火苗", ChainId = "fire" },
            new() { Id = "2", Name = "烈火王", BaseId = "1", BaseName = "小火苗", ChainId = "fire" }
        ]
    };
}
