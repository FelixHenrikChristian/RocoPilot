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
    public async Task MigrationIsDeterministicAndUnchangedOldCloudDoesNotResurrectHandledRecords()
    {
        var (service, _, old) = await Create();
        var migrated = service.CurrentDocument;
        Assert.AreEqual(0, migrated.Accounts.Single().SyncVersion!.Devices.Count);
        await service.MergeRemoteAsync(old);
        Assert.AreEqual(JsonSerializer.Serialize(migrated), JsonSerializer.Serialize(service.CurrentDocument));
        var account = service.CurrentDocument.Accounts.Single();
        Assert.AreEqual(0, account.PendingEncounters.Count);
        Assert.AreEqual(0, account.PendingShinyCaptures.Count);
        Assert.AreEqual(0, account.Seasons.Single().EncounterCountResets.Count);
        await service.UpsertEncounterAsync("S4", "精灵", 1, Date);
        await service.MergeRemoteAsync(old);
        Assert.AreEqual(11, service.CurrentDocument.Accounts.Single().Seasons.Single().Encounters.Single().Count);
    }

    [TestMethod]
    public async Task ChangedOldCloudRequiresExplicitChoiceAndResolutionSurvivesRetry()
    {
        var (service, store, old) = await Create();
        old.Accounts.Single().Seasons.Single().Encounters.Single().Count += 5;
        var before = JsonSerializer.Serialize(service.CurrentDocument);
        var conflict = await Assert.ThrowsExactlyAsync<StatisticsSyncConflictException>(() => service.MergeRemoteAsync(old));
        Assert.AreEqual(before, JsonSerializer.Serialize(service.CurrentDocument));
        store.BeforeSave = key => key == SettingsKeys.StatisticsData ? throw new IOException("disk full") : Task.CompletedTask;
        await Assert.ThrowsExactlyAsync<IOException>(() => service.ResolveConflictAsync(conflict, true));
        Assert.AreEqual(before, JsonSerializer.Serialize(service.CurrentDocument));
        store.BeforeSave = null;
        await service.ResolveConflictAsync(conflict, true);
        await service.MergeRemoteAsync(old);
        Assert.AreEqual(15, service.CurrentDocument.Accounts.Single().Seasons.Single().Encounters.Single().Count);
    }

    [TestMethod]
    public async Task IndependentMigrationOfPendingBucketsProducesIdenticalSnapshots()
    {
        var (_, _, old) = await Create();
        old.Accounts.Single().PendingEncounters.Single().HandledAt = null;
        var first = StatisticsDocumentMerger.Prepare(old);
        var second = StatisticsDocumentMerger.Prepare(old);
        Assert.AreEqual(StatisticsDocumentMerger.ComputeAccountFingerprints(first)["100"],
            StatisticsDocumentMerger.ComputeAccountFingerprints(second)["100"]);
        Assert.AreEqual(1, StatisticsDocumentMerger.Merge(first, second).Accounts.Single().Temporary!.Encounters.Single().Count);
    }

    [TestMethod]
    public async Task LocalAccountDeletedBeforeUpgradeIsNotSilentlyRestored()
    {
        var (_, _, old) = await Create();
        old.Info.Version = "2.0";
        var baseline = new Dictionary<string, string>
        {
            ["100"] = StatisticsDocumentMerger.ComputeAccountFingerprint(old.Accounts.Single())
        };
        var store = new ControlledSettingsStore();
        store.Seed(SettingsKeys.StatisticsData, new StatisticsDocument { Info = new() { Version = "2.0" } });
        store.Seed(SettingsKeys.StatisticsSyncSettings, new StatisticsSyncSettings { LastSyncedAccountFingerprints = baseline });
        var service = new StatisticsService(store, NullLogger<StatisticsService>.Instance);
        await service.LoadAsync();
        Assert.AreEqual(0, service.CurrentDocument.Accounts.Count);
        Assert.IsTrue(service.CurrentDocument.DeletedAccounts.ContainsKey("100"));
        var conflict = await Assert.ThrowsExactlyAsync<StatisticsSyncConflictException>(() => service.MergeRemoteAsync(old));
        await service.ResolveConflictAsync(conflict, false);
        await service.MergeRemoteAsync(old);
        Assert.AreEqual(0, service.CurrentDocument.Accounts.Count);
        Assert.AreEqual(1, service.CurrentDocument.DeletedAccounts.Count);
    }

    private static async Task<(StatisticsService Service, ControlledSettingsStore Store, StatisticsDocument Old)> Create()
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
        var store = new ControlledSettingsStore();
        store.Seed(SettingsKeys.StatisticsData, old);
        var service = new StatisticsService(store, NullLogger<StatisticsService>.Instance);
        await service.LoadAsync();
        return (service, store, old);
    }
}
