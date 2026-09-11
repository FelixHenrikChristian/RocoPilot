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
        await using var lifetime = sync;
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
        await using var lifetime = sync;
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
        await using var lifetime = sync;
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
        await using var lifetime = sync;
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
        await using var lifetime = sync;
        remote.OnUpload = (_, _, _) => Task.FromResult(Success("v1"));
        store.BeforeSave = key => key == SettingsKeys.StatisticsSyncSettings ? throw new IOException("save failed") : Task.CompletedTask;
        await Assert.ThrowsExactlyAsync<IOException>(() => sync.UploadAsync());
        Assert.IsNull((await sync.LoadSettingsAsync()).LastSyncedRemoteEntityTag);
    }

    [TestMethod]
    public async Task ReadingStatusDuringUploadPreservesBusyMessage()
    {
        var (sync, _, remote, _) = await CreateAsync();
        await using var lifetime = sync;
        using var pause = new AsyncPause();
        remote.OnRead = async (_, _) => { await pause.PauseAsync(string.Empty); return new StatisticsSyncRemoteInfo(); };
        remote.OnUpload = (_, _, _) => Task.FromResult(Success("v1"));
        var uploading = sync.UploadAsync();
        await pause.WaitUntilEnteredAsync();
        var message = sync.CurrentStatus.Message;
        var status = await sync.LoadStatusAsync();
        Assert.IsTrue(status.IsBusy);
        Assert.AreEqual(message, status.Message);
        pause.Dispose();
        await uploading;
    }

    [TestMethod]
    public async Task ShutdownCancelsActiveAndQueuedOperationsAndWaitsForCleanup()
    {
        var (sync, _, remote, _) = await CreateAsync();
        await using var lifetime = sync;
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cleanup = new AsyncPause();
        var requests = 0;
        remote.OnRead = async (_, token) =>
        {
            requests++;
            started.TrySetResult();
            try { await Task.Delay(Timeout.Infinite, token); }
            finally { await cleanup.PauseAsync(string.Empty); }
            return new StatisticsSyncRemoteInfo();
        };
        var active = sync.UploadAsync();
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var queued = sync.RefreshRemoteInfoAsync();
        var stopping = sync.DisposeAsync().AsTask();
        await cleanup.WaitUntilEnteredAsync();
        Assert.IsFalse(stopping.IsCompleted);
        cleanup.Dispose();
        await stopping.WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.ThrowsAsync<OperationCanceledException>(() => active);
        await Assert.ThrowsAsync<OperationCanceledException>(() => queued);
        await Assert.ThrowsAsync<OperationCanceledException>(() => sync.UploadAsync());
        Assert.AreEqual(1, requests);
        Assert.IsFalse(sync.CurrentStatus.IsBusy);
    }

    [TestMethod]
    public async Task DisablingSyncCancelsPendingAutomaticUpload()
    {
        var delay = new ManualAsyncDelay();
        var (sync, statistics, remote, _) = await CreateAsync(delay.DelayAsync);
        await using var lifetime = sync;
        var requests = 0;
        remote.OnRead = (_, _) => { requests++; return Task.FromResult(new StatisticsSyncRemoteInfo()); };
        await statistics.UpsertEncounterAsync("S1", "精灵", 1, DateTimeOffset.Now);
        var waiting = await delay.NextAsync();
        var settings = await sync.LoadSettingsAsync();
        settings.IsEnabled = false;
        await sync.SaveSettingsAsync(settings, null);
        await sync.DisposeAsync();
        Assert.IsTrue(waiting.Completion.Task.IsCanceled);
        Assert.AreEqual(0, requests);
    }

    [TestMethod]
    public async Task ShutdownWaitsForInFlightSettingsRead()
    {
        var (sync, _, _, store) = await CreateAsync();
        await using var lifetime = sync;
        using var pause = new AsyncPause();
        store.BeforeRead = pause.PauseAsync;
        var reading = sync.LoadSettingsAsync();
        await pause.WaitUntilEnteredAsync();
        var stopping = sync.DisposeAsync().AsTask();
        Assert.IsFalse(stopping.IsCompleted);
        pause.Dispose();
        await Assert.ThrowsAsync<OperationCanceledException>(() => reading);
        await stopping.WaitAsync(TimeSpan.FromSeconds(5));
    }

    private static StatisticsRemoteUpload Success(string tag) => new(false, new StatisticsSyncResult
    {
        EntityTag = tag,
        CompletedAt = DateTimeOffset.Now
    });

    internal static async Task<(StatisticsSyncService Sync, StatisticsService Statistics, StatisticsRemoteStoreStub Remote, ControlledSettingsStore Store)> CreateAsync(Func<TimeSpan, CancellationToken, Task>? delay = null)
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
        var sync = new StatisticsSyncService(store, statistics, NullLogger<StatisticsSyncService>.Instance, remote, new StatisticsCredentialsStub(), delay ?? Task.Delay);
        return (sync, statistics, remote, store);
    }
}
