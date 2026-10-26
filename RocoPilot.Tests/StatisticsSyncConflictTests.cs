using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using RocoPilot.Configuration;
using RocoPilot.Contracts.Services.Statistics;
using RocoPilot.Models.Statistics;
using RocoPilot.Services.Statistics;
using RocoPilot.Tests.TestDoubles;

namespace RocoPilot.Tests;

[TestClass]
public sealed class StatisticsSyncConflictTests
{
    [TestMethod]
    [DataRow(false)] [DataRow(true)]
    public async Task ChoiceUploadsJoinedVersionAndRepeatedSyncIsIdempotent(bool useRemote)
    {
        var (sync, a, b, _, _) = await Create();
        await using var lifetime = sync;
        var conflict = await Assert.ThrowsExactlyAsync<StatisticsSyncConflictException>(() => sync.UploadAsync());
        Assert.IsTrue(sync.CurrentStatus.HasError);
        Assert.IsNull((await sync.LoadSettingsAsync()).LastSyncedRemoteEntityTag);
        await sync.ResolveConflictAsync(conflict, useRemote);
        Assert.IsTrue(sync.CurrentStatus.HasPendingChanges);
        await sync.UploadAsync();
        await sync.UploadAsync();
        Assert.IsFalse(sync.CurrentStatus.HasPendingChanges);
        Assert.IsFalse(sync.CurrentStatus.HasError);
        Assert.AreEqual(useRemote ? 12 : 11, StatisticsMultiDeviceTests.Count(a));
        StatisticsMultiDeviceTests.AssertSame(a, b);
    }

    [TestMethod]
    public async Task CloudChangeWhileReviewingRejectsStaleChoice()
    {
        var (sync, a, b, _, _) = await Create();
        await using var lifetime = sync;
        var conflict = await Assert.ThrowsExactlyAsync<StatisticsSyncConflictException>(() => sync.DownloadAsync());
        var before = JsonSerializer.Serialize(a.CurrentDocument);
        await b.UpsertEncounterAsync("S4", "精灵", 5, DateTimeOffset.Now);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => sync.ResolveConflictAsync(conflict, true));
        Assert.AreEqual(before, JsonSerializer.Serialize(a.CurrentDocument));
        Assert.AreEqual(17, StatisticsMultiDeviceTests.Count(b));
    }

    [TestMethod]
    public async Task LocalChangeDuringCloudRecheckRejectsStaleChoice()
    {
        var (sync, a, b, _, remote) = await Create();
        await using var lifetime = sync;
        var conflict = await Assert.ThrowsExactlyAsync<StatisticsSyncConflictException>(() => sync.DownloadAsync());
        var download = remote.OnDownload;
        remote.OnDownload = async (settings, token) =>
        {
            await a.UpsertEncounterAsync("S4", "精灵", 3, DateTimeOffset.Now);
            return await download(settings, token);
        };
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => sync.ResolveConflictAsync(conflict, true));
        Assert.AreEqual(14, StatisticsMultiDeviceTests.Count(a));
        Assert.AreEqual(12, StatisticsMultiDeviceTests.Count(b));
    }

    [TestMethod]
    public async Task FailedDecisionSaveCanRetryWithoutChangingEitherSnapshot()
    {
        var (sync, a, b, store, _) = await Create();
        await using var lifetime = sync;
        var conflict = await Assert.ThrowsExactlyAsync<StatisticsSyncConflictException>(() => sync.UploadAsync());
        var preview = JsonSerializer.Serialize(conflict.Local);
        store.BeforeSave = key => key == SettingsKeys.StatisticsData ? throw new IOException("disk full") : Task.CompletedTask;
        await Assert.ThrowsExactlyAsync<IOException>(() => sync.ResolveConflictAsync(conflict, false));
        Assert.AreEqual(preview, JsonSerializer.Serialize(conflict.Local));
        Assert.AreEqual(11, StatisticsMultiDeviceTests.Count(a));
        Assert.AreEqual(12, StatisticsMultiDeviceTests.Count(b));
        store.BeforeSave = null;
        await sync.ResolveConflictAsync(conflict, false);
        await sync.UploadAsync();
        StatisticsMultiDeviceTests.AssertSame(a, b);
    }

    [TestMethod]
    public async Task FailedUploadKeepsDecisionOnDiskAndRetryDoesNotCountAgain()
    {
        var (sync, a, b, store, remote) = await Create();
        await using var lifetime = sync;
        var conflict = await Assert.ThrowsExactlyAsync<StatisticsSyncConflictException>(() => sync.UploadAsync());
        await sync.ResolveConflictAsync(conflict, false);
        var upload = remote.OnUpload;
        remote.OnUpload = (_, _, _) => throw new IOException("offline");
        await Assert.ThrowsExactlyAsync<IOException>(() => sync.UploadAsync());
        var restarted = new StatisticsService(store, NullLogger<StatisticsService>.Instance);
        await restarted.LoadAsync();
        StatisticsMultiDeviceTests.AssertSame(a, restarted);
        Assert.IsTrue(sync.CurrentStatus.HasPendingChanges);
        remote.OnUpload = upload;
        await sync.UploadAsync();
        Assert.AreEqual(11, StatisticsMultiDeviceTests.Count(a));
        StatisticsMultiDeviceTests.AssertSame(a, b);
    }

    [TestMethod]
    public async Task StartupRetriesLocalChangesEvenWhenCloudTagIsUnchanged()
    {
        var (sync, a, _, _, _) = await Create();
        await using var lifetime = sync;
        var conflict = await Assert.ThrowsExactlyAsync<StatisticsSyncConflictException>(() => sync.UploadAsync());
        await sync.ResolveConflictAsync(conflict, false);
        await sync.UploadAsync();
        await a.UpsertEncounterAsync("S4", "精灵", 1, DateTimeOffset.Now);
        var settings = await sync.LoadSettingsAsync();
        var store = new ControlledSettingsStore();
        store.Seed(SettingsKeys.StatisticsDeviceId, "A");
        store.Seed(SettingsKeys.StatisticsData, a.CurrentDocument);
        store.Seed(SettingsKeys.StatisticsSyncSettings, settings);
        var restarted = new StatisticsService(store, NullLogger<StatisticsService>.Instance);
        var delay = new ManualAsyncDelay();
        var uploaded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var remote = new StatisticsRemoteStoreStub
        {
            OnRead = (_, _) => Task.FromResult(new StatisticsSyncRemoteInfo { Exists = true, EntityTag = settings.LastSyncedRemoteEntityTag }),
            OnUpload = (document, _, _) =>
            {
                Assert.AreEqual(12, document.Accounts.Single().Seasons.Single().Encounters.Single().Count);
                uploaded.TrySetResult();
                return Task.FromResult(new StatisticsRemoteUpload(false, new StatisticsSyncResult { EntityTag = "restarted" }));
            }
        };
        await using var resumed = new StatisticsSyncService(store, restarted, NullLogger<StatisticsSyncService>.Instance,
            remote, new StatisticsCredentialsStub(), delay.DelayAsync);
        Assert.IsFalse(await resumed.DownloadRemoteChangesIfNeededAsync());
        Assert.IsTrue(resumed.CurrentStatus.HasPendingChanges);
        (await delay.NextAsync()).Complete();
        await uploaded.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [TestMethod]
    public async Task CloudChangesAfterDecisionCauseNewConflictInsteadOfOverwrite()
    {
        var (sync, a, b, _, _) = await Create();
        await using var lifetime = sync;
        var conflict = await Assert.ThrowsExactlyAsync<StatisticsSyncConflictException>(() => sync.UploadAsync());
        await sync.ResolveConflictAsync(conflict, false);
        await b.UpsertEncounterAsync("S4", "精灵", 1, DateTimeOffset.Now);
        await Assert.ThrowsExactlyAsync<StatisticsSyncConflictException>(() => sync.UploadAsync());
        Assert.AreEqual(11, StatisticsMultiDeviceTests.Count(a));
        Assert.AreEqual(13, StatisticsMultiDeviceTests.Count(b));
    }

    [TestMethod]
    [DataRow(false)] [DataRow(true)]
    public async Task OldCloudDeletionCanBeReviewedAndResolved(bool useRemote)
    {
        var (sync, a, _, store, remote) = await Create();
        await using var lifetime = sync;
        var settings = store.ReadSaved<StatisticsSyncSettings>(SettingsKeys.StatisticsSyncSettings)!;
        settings.LastSyncedAccountFingerprints = StatisticsDocumentMerger.ComputeAccountFingerprints(a.CurrentDocument);
        store.Seed(SettingsKeys.StatisticsSyncSettings, settings);
        remote.OnRead = (_, _) => Task.FromResult(new StatisticsSyncRemoteInfo { Exists = true, EntityTag = "legacy-deleted" });
        remote.OnDownload = (_, _) => Task.FromResult(new StatisticsRemoteDownload(
            new StatisticsDocument { Info = new() { Version = "2.0" } },
            new StatisticsSyncRemoteInfo { Exists = true, EntityTag = "legacy-deleted" }));
        remote.OnUpload = (document, _, _) =>
        {
            Assert.AreEqual(useRemote ? 0 : 1, document.Accounts.Count);
            Assert.AreEqual(useRemote ? 1 : 0, document.DeletedAccounts.Count);
            return Task.FromResult(new StatisticsRemoteUpload(false, new StatisticsSyncResult { EntityTag = "resolved" }));
        };
        var conflict = await Assert.ThrowsExactlyAsync<StatisticsSyncConflictException>(() => sync.UploadAsync());
        Assert.IsNull(conflict.Remote.Account);
        Assert.AreEqual(1, a.CurrentDocument.Accounts.Count);
        await sync.ResolveConflictAsync(conflict, useRemote);
        await sync.DownloadAsync();
        await sync.DownloadAsync();
        await sync.UploadAsync();
        Assert.IsFalse(sync.CurrentStatus.HasPendingChanges);
    }

    private static async Task<(StatisticsSyncService, StatisticsService, StatisticsService, ControlledSettingsStore, StatisticsRemoteStoreStub)> Create()
    {
        var (a, store) = await StatisticsMultiDeviceTests.Seed();
        var (b, _) = await StatisticsMultiDeviceTests.Device("B", a.CurrentDocument);
        await a.UpsertEncounterAsync("S4", "精灵", 1, DateTimeOffset.Now);
        await b.UpsertEncounterAsync("S4", "精灵", 2, DateTimeOffset.Now);
        store.Seed(SettingsKeys.StatisticsSyncSettings, new StatisticsSyncSettings
        {
            IsEnabled = true, ProviderId = "cloudflare-r2", ProviderKind = StatisticsSyncProviderKinds.S3,
            Endpoint = "test", BucketName = "bucket", UserName = "test-key", RemotePath = "statistics.json"
        });
        var revision = 1;
        var remote = new StatisticsRemoteStoreStub
        {
            OnRead = (_, _) => Task.FromResult(new StatisticsSyncRemoteInfo { Exists = true, EntityTag = revision.ToString() }),
            OnDownload = (_, _) => Task.FromResult(new StatisticsRemoteDownload(b.CurrentDocument,
                new StatisticsSyncRemoteInfo { Exists = true, EntityTag = revision.ToString() })),
            OnUpload = async (document, expected, _) =>
            {
                Assert.AreEqual(revision.ToString(), expected.EntityTag);
                await b.MergeRemoteAsync(document);
                return new StatisticsRemoteUpload(false, new StatisticsSyncResult { EntityTag = (++revision).ToString() });
            }
        };
        var sync = new StatisticsSyncService(store, a, NullLogger<StatisticsSyncService>.Instance,
            remote, new StatisticsCredentialsStub(), (_, token) => Task.Delay(Timeout.Infinite, token));
        return (sync, a, b, store, remote);
    }
}
