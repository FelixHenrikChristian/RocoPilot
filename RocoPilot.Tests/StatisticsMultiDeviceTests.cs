using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using RocoPilot.Configuration;
using RocoPilot.Models.Statistics;
using RocoPilot.Services.Statistics;
using RocoPilot.Tests.TestDoubles;

namespace RocoPilot.Tests;

[TestClass]
public sealed class StatisticsMultiDeviceTests
{
    private static readonly DateTimeOffset Date = new(2026, 10, 26, 12, 0, 0, TimeSpan.FromHours(8));

    [TestMethod]
    public async Task EveryAccountIsVersionedAndOrdinaryEncountersStayAggregated()
    {
        var (a, store) = await Device("A");
        await a.AddAccountAsync("100");
        a.SetSelectedAccountUid("100");
        for (var i = 0; i < 50; i++) await a.UpsertEncounterAsync("S4", "精灵", 1, Date);
        var account = a.CurrentDocument.Accounts.Single();
        Assert.AreEqual(50, account.Seasons.Single().Encounters.Single().Count);
        Assert.AreEqual(1, account.SyncVersion!.Devices.Count);
        Assert.AreEqual(51L, account.SyncVersion.Devices["A"]);
        Assert.AreEqual(0, account.PendingEncounters.Count);
        var saves = store.SaveCount;
        await a.EditEncounterAsync("100", "S4", "精灵", "精灵", 50, Date);
        Assert.AreEqual(saves, store.SaveCount);
    }

    [TestMethod]
    [DataRow(true, 0)] [DataRow(true, 3)] [DataRow(false, 0)] [DataRow(false, 3)]
    public async Task ResetAndNewRoundReplaceOldCountsOnOtherDevices(bool shiny, int nextCount)
    {
        var (a, b) = await Pair();
        var stale = b.CurrentDocument;
        if (shiny) await a.AddShinyCapturesAsync("S4", "精灵", 1, Date, true);
        else await a.DeleteEncounterAsync("100", "S4", "精灵");
        if (nextCount > 0) await a.UpsertEncounterAsync("S4", "精灵", nextCount, Date);
        await a.MergeRemoteAsync(stale);
        await b.MergeRemoteAsync(a.CurrentDocument);
        await b.MergeRemoteAsync(stale);
        Assert.AreEqual(nextCount, Count(b));
        if (shiny) Assert.AreEqual(10, b.CurrentDocument.Accounts.Single().Seasons.Single().ShinyCaptures.Single().EncounterCountBeforeCapture);
        AssertSame(a, b);
    }

    [TestMethod]
    public async Task ReducedCountAndDeletedShinyAreNotRestored()
    {
        var (a, b) = await Pair();
        await a.AddShinyCapturesAsync("S4", "精灵", 1, Date);
        await b.MergeRemoteAsync(a.CurrentDocument);
        var stale = b.CurrentDocument;
        await a.EditEncounterAsync("100", "S4", "精灵", "精灵", 2, Date);
        await a.DeleteShinyCaptureAsync("100", a.CurrentDocument.Accounts.Single().Seasons.Single().ShinyCaptures.Single().Id);
        await b.MergeRemoteAsync(a.CurrentDocument);
        await b.MergeRemoteAsync(stale);
        Assert.AreEqual(2, Count(b));
        Assert.AreEqual(0, b.CurrentDocument.Accounts.Single().Seasons.Single().ShinyCaptures.Count);
    }

    [TestMethod]
    public async Task IndependentAccountsMergeWithoutAddingOverlappingCounts()
    {
        var (a, _) = await Seed();
        await a.AddAccountAsync("200");
        var (b, _) = await Device("B", a.CurrentDocument);
        await a.UpsertEncounterAsync("S4", "精灵", 2, Date);
        b.SetSelectedAccountUid("200");
        await b.UpsertEncounterAsync("S4", "另一只", 5, Date);
        await a.MergeRemoteAsync(b.CurrentDocument);
        await b.MergeRemoteAsync(a.CurrentDocument);
        await a.MergeRemoteAsync(b.CurrentDocument);
        Assert.AreEqual(12, Count(a));
        Assert.AreEqual(5, a.CurrentDocument.Accounts.Single(x => x.Uid == "200").Seasons.Single().Encounters.Single().Count);
        AssertSame(a, b);
    }

    [TestMethod]
    [DataRow(false)] [DataRow(true)]
    public async Task AccountDeletionSurvivesOfflineAndFreshDevices(bool clearAll)
    {
        var (a, b) = await Pair();
        var stale = b.CurrentDocument;
        if (clearAll) await a.ClearAsync(); else await a.DeleteAccountAsync("100");
        await b.MergeRemoteAsync(a.CurrentDocument);
        var (fresh, _) = await Device("C", stale);
        await fresh.MergeRemoteAsync(a.CurrentDocument);
        await b.MergeRemoteAsync(stale);
        Assert.AreEqual(0, b.CurrentDocument.Accounts.Count);
        Assert.AreEqual(1, b.CurrentDocument.DeletedAccounts.Count);
        Assert.IsFalse(JsonSerializer.Serialize(b.CurrentDocument).Contains("精灵", StringComparison.Ordinal));
        AssertSame(a, b);
        AssertSame(a, fresh);
    }

    [TestMethod]
    public async Task ReaddingDeletedUidStartsEmptyAndKeepsCausalHistory()
    {
        var (a, b) = await Pair();
        await a.DeleteAccountAsync("100");
        var deleted = a.CurrentDocument;
        await a.AddAccountAsync("100");
        a.SetSelectedAccountUid("100");
        await a.UpsertEncounterAsync("S4", "精灵", 3, Date);
        await b.MergeRemoteAsync(a.CurrentDocument);
        await b.MergeRemoteAsync(deleted);
        Assert.AreEqual(3, Count(b));
        Assert.AreEqual(0, b.CurrentDocument.DeletedAccounts.Count);
        AssertSame(a, b);
    }

    [TestMethod]
    [DataRow(false)] [DataRow(true)]
    public async Task ConcurrentResetAndIncrementRequireChoiceAndResolutionConverges(bool useRemote)
    {
        var (a, b) = await Pair();
        await a.AddShinyCapturesAsync("S4", "精灵", 1, Date, true);
        await b.UpsertEncounterAsync("S4", "精灵", 5, Date);
        var local = JsonSerializer.Serialize(a.CurrentDocument);
        var remote = JsonSerializer.Serialize(b.CurrentDocument);
        var conflict = await Assert.ThrowsExactlyAsync<StatisticsSyncConflictException>(() => a.MergeRemoteAsync(b.CurrentDocument));
        Assert.AreEqual(local, JsonSerializer.Serialize(a.CurrentDocument));
        Assert.AreEqual(remote, JsonSerializer.Serialize(b.CurrentDocument));
        await a.ResolveConflictAsync(conflict, useRemote);
        await a.MergeRemoteAsync(b.CurrentDocument);
        await b.MergeRemoteAsync(a.CurrentDocument);
        Assert.AreEqual(useRemote ? 15 : 0, Count(a));
        Assert.AreEqual(useRemote ? 0 : 1, a.CurrentDocument.Accounts.Single().Seasons.Single().ShinyCaptures.Count);
        Assert.AreEqual(2, a.CurrentDocument.Accounts.Single().SyncVersion!.Devices.Count);
        AssertSame(a, b);
    }

    [TestMethod]
    [DataRow(false, false)] [DataRow(true, false)] [DataRow(false, true)] [DataRow(true, true)]
    public async Task ConcurrentAccountDeletionAndIncrementCanResolveEitherWay(bool useRemote, bool deleteRemote)
    {
        var (a, b) = await Pair();
        await (deleteRemote ? b : a).DeleteAccountAsync("100");
        await (deleteRemote ? a : b).UpsertEncounterAsync("S4", "精灵", 1, Date);
        var conflict = await Assert.ThrowsExactlyAsync<StatisticsSyncConflictException>(() => a.MergeRemoteAsync(b.CurrentDocument));
        Assert.IsNull((deleteRemote ? conflict.Remote : conflict.Local).Account);
        await a.ResolveConflictAsync(conflict, useRemote);
        await b.MergeRemoteAsync(a.CurrentDocument);
        var deleted = useRemote == deleteRemote;
        Assert.AreEqual(deleted ? 0 : 1, b.CurrentDocument.Accounts.Count);
        Assert.AreEqual(deleted ? 1 : 0, b.CurrentDocument.DeletedAccounts.Count);
        AssertSame(a, b);
    }

    [TestMethod]
    public async Task ImportPreservesDeletionMarkersAndMarksOmittedAccountsDeleted()
    {
        var (a, b) = await Pair();
        await a.AddAccountAsync("200");
        await a.DeleteAccountAsync("200");
        await a.ReplaceAsync(new StatisticsDocument());
        Assert.AreEqual(2, a.CurrentDocument.DeletedAccounts.Count);
        await b.MergeRemoteAsync(a.CurrentDocument);
        Assert.AreEqual(0, b.CurrentDocument.Accounts.Count);
        AssertSame(a, b);
    }

    [TestMethod]
    public async Task StaleLocalConflictChoiceNeverOverwritesNewRecords()
    {
        var (a, b) = await Pair();
        await a.UpsertEncounterAsync("S4", "精灵", 1, Date);
        await b.UpsertEncounterAsync("S4", "精灵", 2, Date);
        var conflict = await Assert.ThrowsExactlyAsync<StatisticsSyncConflictException>(() => a.MergeRemoteAsync(b.CurrentDocument));
        await a.UpsertEncounterAsync("S4", "精灵", 1, Date);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => a.ResolveConflictAsync(conflict, true));
        Assert.AreEqual(12, Count(a));
    }

    [TestMethod]
    public async Task EqualVersionsWithDifferentContentsAreRejected()
    {
        var (a, b) = await Pair();
        var corrupt = b.CurrentDocument;
        corrupt.Accounts.Single().Seasons.Single().Encounters.Single().Count = 99;
        await Assert.ThrowsExactlyAsync<StatisticsSyncConflictException>(() => a.MergeRemoteAsync(corrupt));
        Assert.AreEqual(10, Count(a));
    }

    [TestMethod]
    public async Task PendingLegacyAccountsAreVersionedWithoutUserAction()
    {
        var legacy = new StatisticsDocument { Accounts = [new() { Uid = "100" }] };
        var (a, store) = await Device("A", legacy);
        Assert.IsNotNull(a.CurrentDocument.Accounts.Single().SyncVersion);
        var saves = store.SaveCount;
        var reloaded = new StatisticsService(store, NullLogger<StatisticsService>.Instance);
        await reloaded.LoadAsync();
        Assert.AreEqual(saves, store.SaveCount);
        AssertSame(a, reloaded);
    }

    [TestMethod]
    public async Task RepeatedDeletionOfSameUidDoesNotGrowHistory()
    {
        var (a, _) = await Device("A");
        for (var i = 0; i < 50; i++)
        {
            await a.AddAccountAsync("100");
            await a.DeleteAccountAsync("100");
        }
        Assert.AreEqual(0, a.CurrentDocument.Accounts.Count);
        var version = a.CurrentDocument.DeletedAccounts.Single().Value;
        Assert.AreEqual(1, version.Devices.Count);
        Assert.AreEqual(100L, version.Devices["A"]);
    }

    internal static async Task<(StatisticsService Service, ControlledSettingsStore Store)> Device(string id, StatisticsDocument? document = null)
    {
        var store = new ControlledSettingsStore();
        store.Seed(SettingsKeys.StatisticsDeviceId, id);
        if (document is not null) store.Seed(SettingsKeys.StatisticsData, document);
        var service = new StatisticsService(store, NullLogger<StatisticsService>.Instance);
        await service.LoadAsync();
        if (service.CurrentDocument.Accounts.Any(x => x.Uid == "100")) service.SetSelectedAccountUid("100");
        return (service, store);
    }

    internal static async Task<(StatisticsService Service, ControlledSettingsStore Store)> Seed()
    {
        var (service, store) = await Device("A");
        await service.AddAccountAsync("100");
        service.SetSelectedAccountUid("100");
        await service.UpsertEncounterAsync("S4", "精灵", 10, Date);
        return (service, store);
    }

    private static async Task<(StatisticsService, StatisticsService)> Pair()
    {
        var (a, _) = await Seed();
        var (b, _) = await Device("B", a.CurrentDocument);
        return (a, b);
    }

    internal static int Count(StatisticsService service) => service.CurrentDocument.Accounts
        .Where(x => x.Uid == "100").SelectMany(x => x.Seasons).SelectMany(x => x.Encounters).Sum(x => x.Count);

    internal static void AssertSame(StatisticsService a, StatisticsService b) =>
        Assert.AreEqual(JsonSerializer.Serialize(StatisticsDocumentMerger.ComputeAccountFingerprints(a.CurrentDocument)),
            JsonSerializer.Serialize(StatisticsDocumentMerger.ComputeAccountFingerprints(b.CurrentDocument)));
}
