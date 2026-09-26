using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.UI.Xaml;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using RocoPilot.Contracts.Services.Encounters;
using RocoPilot.Models.Encounters;
using RocoPilot.Models.Spirits;
using RocoPilot.Models.Statistics;
using RocoPilot.Services.Encounters;
using RocoPilot.Services.RuntimeTasks;
using RocoPilot.Services.Statistics;
using RocoPilot.Tests.TestDoubles;
using RocoPilot.ViewModels;

namespace RocoPilot.Tests;

[TestClass]
public sealed class PendingShinyCaptureTests
{
    private static readonly EncounterSeasonDefinition Season = new() { Id = "S4", Name = "S4赛季", DateRange = "2026/9/10-2026/11/4" };
    private static readonly DateTimeOffset DetectedAt = new(2026, 9, 12, 12, 0, 0, TimeSpan.FromHours(8));

    [TestMethod]
    public async Task UnknownShinyPersistsBeforeNameOcrAndSurvivesRestart()
    {
        var (service, store) = await CreateAsync();
        var capture = new RuntimePendingShinyCapture(1, "100", Season, DetectedAt);
        await capture.SaveAsync(service);
        var saves = store.SaveCount;
        await capture.SaveAsync(service);
        Assert.AreEqual(saves, store.SaveCount);
        var tipOnly = Account(service).PendingShinyCaptures.Single();
        Assert.AreEqual(string.Empty, tipOnly.Name);
        Assert.AreEqual(string.Empty, tipOnly.RawText);
        Assert.AreEqual(DetectedAt, tipOnly.DetectedAt);

        await capture.SaveAsync(service, "银月狼王");
        var restarted = new StatisticsService(store, NullLogger<StatisticsService>.Instance);
        await restarted.LoadAsync();
        var pending = restarted.GetSelectedAccountPendingShinyCaptures().Single();
        Assert.AreEqual(capture.Id, pending.Id);
        Assert.AreEqual("银月狼王", pending.RawText);
        Assert.AreEqual(string.Empty, pending.Name);
        Assert.AreEqual(0, Account(restarted).Seasons.Single().ShinyCaptures.Count);
    }

    [TestMethod]
    public async Task NewCatalogCompletesNameAndConfirmationConsumesAllAccumulatedEncounters()
    {
        var (service, _) = await CreateAsync();
        await service.UpsertEncounterAsync("S4", "诅咒狼灵", 20, DetectedAt.AddHours(-1));
        var capture = new RuntimePendingShinyCapture(1, "100", Season, DetectedAt);
        await capture.SaveAsync(service, "银月狼王");
        await service.UpsertEncounterAsync("S4", "诅咒狼灵", 8, DetectedAt.AddHours(1));
        await service.AddPendingEncounterAsync("100", Season, "before", "银月狼王", DetectedAt.AddMinutes(-1));
        await service.AddPendingEncounterAsync("100", Season, "after", "银月狼王", DetectedAt.AddMinutes(1));
        Assert.AreEqual(2, await service.RematchPendingEncountersAsync(Catalog(), 0.9));
        Assert.AreEqual("诅咒狼灵", service.GetSelectedAccountPendingShinyCaptures().Single().Name);
        Assert.AreEqual(0, Account(service).Seasons.Single().ShinyCaptures.Count);

        await service.ConfirmPendingShinyCaptureAsync(capture.Id, "诅咒狼灵", null, DetectedAt.AddDays(1));
        var season = Account(service).Seasons.Single();
        Assert.AreEqual(30, season.ShinyCaptures.Single().EncounterCountBeforeCapture);
        Assert.AreEqual(DetectedAt, season.ShinyCaptures.Single().CapturedAt);
        Assert.AreEqual(0, season.Encounters.Count);
        Assert.AreEqual(0, service.GetSelectedAccountPendingShinyCaptures().Count);
        Assert.AreEqual(0, await service.RematchPendingEncountersAsync(Catalog(), 0.9));

        await service.RecordEncounterAsync(Season, "诅咒狼灵", DetectedAt.AddDays(2));
        await service.ConfirmPendingShinyCaptureAsync(capture.Id, "诅咒狼灵", null, DetectedAt.AddDays(2));
        Assert.AreEqual(1, Account(service).Seasons.Single().Encounters.Single().Count);
        Assert.AreEqual(1, Account(service).Seasons.Single().ShinyCaptures.Count);
    }

    [TestMethod]
    public async Task StartupResolvesBothSeasonAndShinyNameFromUpdatedBundledData()
    {
        var (service, store) = await CreateAsync();
        var expired = new EncounterSeasonDefinition { Id = "S3", DateRange = "2026/7/16-2026/9/9" };
        var old = new StatisticsService(store, NullLogger<StatisticsService>.Instance, new SeasonConfig(expired));
        await old.LoadAsync();
        await old.AddPendingShinyCaptureAsync(expired, string.Empty, DetectedAt, "unknown-season", "银月狼王");
        Assert.AreEqual(EncounterSeasonTimeline.PendingSeasonId, old.GetSelectedAccountPendingShinyCaptures().Single().Season);

        var catalog = new StatisticsSpiritCatalogStub { Document = Catalog() };
        var updated = new StatisticsService(store, NullLogger<StatisticsService>.Instance, new SeasonConfig(Season), catalog);
        await updated.LoadAsync();
        var pending = updated.GetSelectedAccountPendingShinyCaptures().Single();
        Assert.AreEqual("S4", pending.Season);
        Assert.AreEqual("诅咒狼灵", pending.Name);
        Assert.AreEqual(DetectedAt, pending.DetectedAt);
        Assert.AreEqual(1, catalog.LoadCount);
        Assert.AreEqual(0, Account(updated).Seasons.SelectMany(item => item.ShinyCaptures).Count());
    }

    [TestMethod]
    public async Task MissingCatalogNameRemainsPendingUntilLaterUpdate()
    {
        var (service, _) = await CreateAsync();
        await service.AddPendingShinyCaptureAsync(Season, string.Empty, DetectedAt, "unmatched", "银月狼王");
        await service.RematchPendingEncountersAsync(new SpiritCatalogDocument(), 0.9);
        Assert.AreEqual(string.Empty, service.GetSelectedAccountPendingShinyCaptures().Single().Name);
        await service.RematchPendingEncountersAsync(Catalog(), 0.9);
        Assert.AreEqual("诅咒狼灵", service.GetSelectedAccountPendingShinyCaptures().Single().Name);
    }

    [TestMethod]
    public async Task SameBattleRetriesOnceAndSeparateBattlesKeepSeparateSameNameShinies()
    {
        var (service, store) = await CreateAsync();
        var capture = new RuntimePendingShinyCapture(1, "100", Season, DetectedAt);
        store.BeforeSave = _ => throw new IOException("save failed");
        await Assert.ThrowsExactlyAsync<IOException>(() => capture.SaveAsync(service));
        Assert.AreEqual(0, service.GetSelectedAccountPendingShinyCaptures().Count);
        store.BeforeSave = null;
        await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => capture.SaveAsync(service, "银月狼王", "诅咒狼灵")));
        await new RuntimePendingShinyCapture(2, "100", Season, DetectedAt.AddSeconds(1))
            .SaveAsync(service, "银月狼王", "诅咒狼灵");
        Assert.AreEqual(2, service.GetSelectedAccountPendingShinyCaptures().Count);
        Assert.AreEqual(2, service.GetSelectedAccountPendingShinyCaptures().Select(item => item.Id).Distinct().Count());
    }

    [TestMethod]
    public async Task RuntimePinsAccountBeforeAsynchronousNameCompletion()
    {
        var (service, _) = await CreateAsync();
        var capture = new RuntimePendingShinyCapture(1, "100", Season, DetectedAt);
        await capture.SaveAsync(service);
        await service.AddAccountAsync("200");
        service.SetSelectedAccountUid("200");
        service.SetActiveAccountUid("200");
        await capture.SaveAsync(service, "银月狼王", "诅咒狼灵");
        Assert.AreEqual("诅咒狼灵", Account(service).PendingShinyCaptures.Single().Name);
        Assert.AreEqual(0, service.GetSelectedAccountPendingShinyCaptures().Count);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task HandledShinyIsNotResurrectedByLateOcrOrOldCloudRecord(bool confirm)
    {
        var (service, _) = await CreateAsync();
        var capture = new RuntimePendingShinyCapture(1, "100", Season, DetectedAt);
        await capture.SaveAsync(service, "银月狼王");
        var oldCloud = service.CurrentDocument;
        if (confirm) await service.ConfirmPendingShinyCaptureAsync(capture.Id, "诅咒狼灵", 28, DetectedAt.AddHours(1));
        else await service.DiscardPendingShinyCaptureAsync(capture.Id);
        await capture.SaveAsync(service, "银月狼王", "诅咒狼灵");
        await service.MergeRemoteAsync(oldCloud, null, false);
        Assert.AreEqual(0, service.GetSelectedAccountPendingShinyCaptures().Count);
        Assert.IsNotNull(Account(service).PendingShinyCaptures.Single().HandledAt);
        Assert.AreEqual(confirm ? 1 : 0, Account(service).Seasons.Single().ShinyCaptures.Count);
    }

    [TestMethod]
    public async Task CloudNameResolutionIsKeptWhenDetectionTimeIsUnchanged()
    {
        var (service, _) = await CreateAsync();
        await service.AddPendingShinyCaptureAsync(Season, string.Empty, DetectedAt, "cloud", "银月狼王");
        var remote = service.CurrentDocument;
        remote.Accounts.Single().PendingShinyCaptures.Single().Name = "诅咒狼灵";
        await service.MergeRemoteAsync(remote, null, false);
        Assert.AreEqual("诅咒狼灵", service.GetSelectedAccountPendingShinyCaptures().Single().Name);
    }

    [TestMethod]
    public async Task OverviewShowsUnknownNameAndRefreshesAutomaticDraftAfterRematch()
    {
        var (service, _) = await CreateAsync();
        await service.AddPendingShinyCaptureAsync(Season, string.Empty, DetectedAt, "view", "银月狼王");
        var overview = new StatisticsOverviewViewModel();
        overview.ApplyDocument(service.CurrentDocument, "100", new EncounterSeasonConfig());
        Assert.AreEqual(Visibility.Visible, overview.PendingShinyConfirmationVisibility);
        StringAssert.Contains(overview.PendingShinyNameHint, "银月狼王");
        Assert.AreEqual(string.Empty, overview.PendingEditor.Name);
        await service.UpsertEncounterAsync("S4", "诅咒狼灵", 28, DetectedAt.AddHours(1));
        await service.RematchPendingEncountersAsync(Catalog(), 0.9);
        overview.ApplyDocument(service.CurrentDocument, "100", new EncounterSeasonConfig());
        Assert.AreEqual("诅咒狼灵", overview.PendingEditor.Name);
        Assert.AreEqual(28d, overview.PendingEditor.EncounterCount);
        Assert.IsNull(overview.PendingEditor.EncounterCountOverride);
        Assert.AreEqual(Visibility.Collapsed, overview.PendingShinyNameHintVisibility);
    }

    [TestMethod]
    public async Task EmptyNewFieldsDoNotChangeExistingCloudFingerprints()
    {
        var (service, _) = await CreateAsync();
        await service.AddPendingShinyCaptureAsync(Season, "诅咒狼灵", DetectedAt);
        var account = Account(service);
        var legacy = new
        {
            account.Uid,
            Seasons = account.Seasons.Select(season => new
            {
                season.Id, season.Name, season.DateRange, season.EncounterTypeName,
                season.Encounters, season.ShinyCaptures
            }),
            PendingShinyCaptures = account.PendingShinyCaptures.Select(item => new { item.Id, item.Name, item.Season, item.DetectedAt })
        };
        var json = JsonSerializer.Serialize(legacy, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true });
        var fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json))).ToLowerInvariant();
        Assert.AreEqual(fingerprint, StatisticsDocumentMerger.ComputeAccountFingerprints(service.CurrentDocument)["100"]);
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
    private static SpiritCatalogDocument Catalog() => new()
    {
        Spirits =
        [
            new() { Id = "443", Name = "诅咒狼灵", BaseId = "443", BaseName = "诅咒狼灵", ChainId = "wolf" },
            new() { Id = "445", Name = "银月狼王", BaseId = "443", BaseName = "诅咒狼灵", ChainId = "wolf" }
        ]
    };

    private sealed class SeasonConfig(params EncounterSeasonDefinition[] seasons) : IEncounterSeasonConfigService
    {
        public EncounterSeasonConfig Load() => new() { CurrentSeasonId = seasons[0].Id, Seasons = [.. seasons] };
        public EncounterSeasonDefinition? GetCurrentSeason() => seasons[0];
    }
}
