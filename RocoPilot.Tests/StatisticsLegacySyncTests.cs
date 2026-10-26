using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using RocoPilot.Configuration;
using RocoPilot.Models.Statistics;
using RocoPilot.Services.Statistics;
using RocoPilot.Tests.TestDoubles;

namespace RocoPilot.Tests;

[TestClass]
public sealed class StatisticsLegacySyncTests
{
    private static readonly DateTimeOffset Date = new(2026, 10, 25, 12, 0, 0, TimeSpan.FromHours(8));

    [TestMethod]
    public async Task MigrationOnlyLocalAcceptsChangedLegacyCloudAndKeepsVersionHistory()
    {
        var (service, _, old, baseline) = await Create();
        var migrated = service.CurrentDocument;
        var firstVersion = migrated.Accounts.Single().SyncVersion!.Devices.Single();
        old.Accounts.Single().Seasons.Single().Encounters.Single().Count += 5;
        var merged = await service.MergeRemoteAsync(old, baseline, false);
        var account = merged.Document.Accounts.Single();
        Assert.AreEqual(15, account.Seasons.Single().Encounters.Single().Count);
        Assert.AreEqual(0, account.PendingEncounters.Count);
        Assert.AreEqual(0, account.PendingShinyCaptures.Count);
        Assert.AreEqual(0, account.Seasons.Single().EncounterCountResets.Count);
        Assert.AreEqual(firstVersion.Value + 1, account.SyncVersion!.Devices[firstVersion.Key]);
        await service.MergeRemoteAsync(old, baseline, false);
        await service.MergeRemoteAsync(migrated, null, false);
        Assert.AreEqual(15, service.CurrentDocument.Accounts.Single().Seasons.Single().Encounters.Single().Count);
        Assert.AreEqual(firstVersion.Value + 1, service.CurrentDocument.Accounts.Single().SyncVersion!.Devices[firstVersion.Key]);
    }

    [TestMethod]
    public async Task FailedMigrationMergeCanRetryWithoutChangingLocalOrRemote()
    {
        var (service, store, old, baseline) = await Create();
        old.Accounts.Single().Seasons.Single().Encounters.Single().Count += 5;
        var localBefore = JsonSerializer.Serialize(service.CurrentDocument);
        var remoteBefore = JsonSerializer.Serialize(old);
        store.BeforeSave = key => key == SettingsKeys.StatisticsData ? throw new IOException("disk full") : Task.CompletedTask;
        await Assert.ThrowsExactlyAsync<IOException>(() => service.MergeRemoteAsync(old, baseline, false));
        Assert.AreEqual(localBefore, JsonSerializer.Serialize(service.CurrentDocument));
        Assert.AreEqual(localBefore, JsonSerializer.Serialize(store.ReadSaved<StatisticsDocument>(SettingsKeys.StatisticsData)));
        Assert.AreEqual(remoteBefore, JsonSerializer.Serialize(old));
        store.BeforeSave = null;
        await service.MergeRemoteAsync(old, baseline, false);
        Assert.AreEqual(15, service.CurrentDocument.Accounts.Single().Seasons.Single().Encounters.Single().Count);
    }

    [TestMethod]
    [DataRow("local-count")]
    [DataRow("changed-markers")]
    [DataRow("no-baseline")]
    public async Task UnprovenLegacyRelationshipStillRejectsConflict(string scenario)
    {
        var (service, _, old, baseline) = await Create();
        old.Accounts.Single().Seasons.Single().Encounters.Single().Count += 5;
        if (scenario == "local-count") await service.UpsertEncounterAsync("S4", "精灵", 1, Date);
        if (scenario == "changed-markers") old.Accounts.Single().PendingEncounters.Single().HandledAt = Date.AddDays(1);
        var before = JsonSerializer.Serialize(service.CurrentDocument);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => service.MergeRemoteAsync(old,
            scenario == "no-baseline" ? null : baseline, false));
        Assert.AreEqual(before, JsonSerializer.Serialize(service.CurrentDocument));
    }

    [TestMethod]
    public async Task FirstVersionCreatedByActualShinyConfirmationIsNotMistakenForMigrationOnly()
    {
        var old = new StatisticsDocument { Accounts = [new() { Uid = "100", Seasons = [new()
        {
            Id = "S4", Name = "S4赛季", Encounters = [new() { Name = "精灵", Count = 10, Season = "S4", LastCapturedAt = Date }]
        }], PendingShinyCaptures = [new() { Id = "shiny", Name = "精灵", Season = "S4", DetectedAt = Date }] }] };
        var baseline = StatisticsDocumentMerger.ComputeAccountFingerprints(old);
        var store = new ControlledSettingsStore();
        store.Seed(SettingsKeys.StatisticsData, old);
        var service = new StatisticsService(store, NullLogger<StatisticsService>.Instance);
        await service.LoadAsync();
        await service.ConfirmPendingShinyCaptureAsync("shiny", "精灵", null, Date);
        Assert.AreEqual(1L, service.CurrentDocument.Accounts.Single().SyncVersion!.Devices.Single().Value);
        old.Accounts.Single().Seasons.Single().Encounters.Single().Count += 5;
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => service.MergeRemoteAsync(old, baseline, false));
        Assert.AreEqual(1, service.CurrentDocument.Accounts.Single().Seasons.Single().ShinyCaptures.Count);
    }

    private static async Task<(StatisticsService Service, ControlledSettingsStore Store, StatisticsDocument Old,
        Dictionary<string, string> Baseline)> Create()
    {
        var old = new StatisticsDocument { Accounts = [new()
        {
            Uid = "100", Seasons = [new()
            {
                Id = "S4", Name = "S4赛季",
                Encounters = [new() { Name = "精灵", Count = 10, Season = "S4", LastCapturedAt = Date }],
                EncounterCountResets = [new() { Name = "历史精灵", ResetAt = Date }]
            }],
            PendingEncounters = [new() { Id = "handled", RawText = "已处理", Season = "S4", DetectedAt = Date, HandledAt = Date }],
            PendingShinyCaptures = [new() { Id = "ignored", Name = "历史精灵", Season = "S4", DetectedAt = Date, HandledAt = Date }]
        }] };
        var baseline = StatisticsDocumentMerger.ComputeAccountFingerprints(old);
        var store = new ControlledSettingsStore();
        store.Seed(SettingsKeys.StatisticsData, old);
        var service = new StatisticsService(store, NullLogger<StatisticsService>.Instance);
        await service.LoadAsync();
        return (service, store, old, baseline);
    }
}
