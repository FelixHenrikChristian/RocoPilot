using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using RocoPilot.Configuration;
using RocoPilot.Contracts.Services.Statistics;
using RocoPilot.Models.Statistics;
using RocoPilot.Services.Statistics;
using RocoPilot.Tests.TestDoubles;

namespace RocoPilot.Tests;

[TestClass]
public sealed class StatisticsSyncServiceTests
{
    [TestMethod]
    public async Task UploadBaselineDescribesUploadedSnapshotDespiteConcurrentLocalWrite()
    {
        var (sync, statistics, remote, _) = await CreateAsync();
        Dictionary<string, string>? uploaded = null;
        remote.OnUpload = async (document, _, _) =>
        {
            uploaded = StatisticsDocumentMerger.ComputeAccountFingerprints(document);
            await statistics.UpsertEncounterAsync("S1", "精灵", 1, DateTimeOffset.Now);
            return Success("v1");
        };
        await sync.UploadAsync();
        var saved = await sync.LoadSettingsAsync();
        Assert.AreEqual(uploaded!["100"], saved.LastSyncedAccountFingerprints!["100"]);
        Assert.AreNotEqual(uploaded["100"], StatisticsDocumentMerger.ComputeAccountFingerprints(statistics.CurrentDocument)["100"]);
    }

    [TestMethod]
    public async Task ConditionalConflictDownloadsNewVersionBeforeRetry()
    {
        var (sync, statistics, remote, _) = await CreateAsync();
        var uploads = 0;
        remote.OnRead = (_, _) => Task.FromResult(new StatisticsSyncRemoteInfo { Exists = uploads > 0, EntityTag = "other" });
        var remoteDocument = statistics.CurrentDocument;
        remoteDocument.Accounts.Add(new AccountStatisticsData { Uid = "300" });
        remote.OnDownload = (_, _) => Task.FromResult(new StatisticsRemoteDownload(remoteDocument,
            new StatisticsSyncRemoteInfo { Exists = true, EntityTag = "other" }));
        remote.OnUpload = (document, expected, _) =>
        {
            if (++uploads == 1) return Task.FromResult(new StatisticsRemoteUpload(true));
            Assert.AreEqual("other", expected.EntityTag);
            Assert.IsTrue(document.Accounts.Any(account => account.Uid == "300"));
            return Task.FromResult(Success("v2"));
        };

        await sync.UploadAsync();

        Assert.AreEqual(2, uploads);
        Assert.AreEqual("v2", (await sync.LoadSettingsAsync()).LastSyncedRemoteEntityTag);
        Assert.IsTrue(statistics.CurrentDocument.Accounts.Any(account => account.Uid == "300"));
    }

    [TestMethod]
    public async Task RepeatedConflictStopsAfterThreeAttemptsWithoutAdvancingBaseline()
    {
        var (sync, _, remote, _) = await CreateAsync();
        var attempts = 0;
        remote.OnUpload = (_, _, _) => { attempts++; return Task.FromResult(new StatisticsRemoteUpload(true)); };
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => sync.UploadAsync());
        Assert.AreEqual(3, attempts);
        Assert.IsNull((await sync.LoadSettingsAsync()).LastSyncedRemoteEntityTag);
    }

    [TestMethod]
    public async Task FailedLocalMergeDoesNotAdvanceDownloadBaseline()
    {
        var (sync, statistics, remote, store) = await CreateAsync();
        remote.OnDownload = (_, _) => Task.FromResult(new StatisticsRemoteDownload(new StatisticsDocument(),
            new StatisticsSyncRemoteInfo { Exists = true, EntityTag = "remote" }));
        store.BeforeSave = key => key == SettingsKeys.StatisticsData ? throw new IOException("save failed") : Task.CompletedTask;
        await Assert.ThrowsExactlyAsync<IOException>(() => sync.DownloadAsync());
        Assert.AreEqual(2, statistics.CurrentDocument.Accounts.Count);
        Assert.IsNull((await sync.LoadSettingsAsync()).LastDownloadedAt);
    }

    [TestMethod]
    public async Task FailedBaselineSaveKeepsCachedSyncMetadata()
    {
        var (sync, _, remote, store) = await CreateAsync();
        remote.OnUpload = (_, _, _) => Task.FromResult(Success("v1"));
        store.BeforeSave = key => key == SettingsKeys.StatisticsSyncSettings ? throw new IOException("save failed") : Task.CompletedTask;
        await Assert.ThrowsExactlyAsync<IOException>(() => sync.UploadAsync());
        Assert.IsNull((await sync.LoadSettingsAsync()).LastSyncedRemoteEntityTag);
    }

    private static StatisticsRemoteUpload Success(string tag) => new(false, new StatisticsSyncResult
    {
        EntityTag = tag,
        CompletedAt = DateTimeOffset.Now
    });

    internal static async Task<(StatisticsSyncService Sync, StatisticsService Statistics, StatisticsRemoteStoreStub Remote, ControlledSettingsStore Store)> CreateAsync()
    {
        var store = new ControlledSettingsStore();
        store.Seed(SettingsKeys.StatisticsData, StatisticsOverviewViewModelTests.Document());
        store.Seed(SettingsKeys.StatisticsSyncSettings, new StatisticsSyncSettings
        {
            IsEnabled = true, ProviderId = "cloudflare-r2", ProviderKind = StatisticsSyncProviderKinds.S3,
            Endpoint = "test", BucketName = "bucket", UserName = "test-key", RemotePath = "statistics.json"
        });
        var statistics = new StatisticsService(store, NullLogger<StatisticsService>.Instance);
        await statistics.LoadAsync();
        statistics.SetSelectedAccountUid("100");
        var remote = new StatisticsRemoteStoreStub();
        var sync = new StatisticsSyncService(store, statistics, NullLogger<StatisticsSyncService>.Instance, remote, new StatisticsCredentialsStub());
        return (sync, statistics, remote, store);
    }
}
