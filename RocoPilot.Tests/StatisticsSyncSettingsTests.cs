using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using RocoPilot.Configuration;
using RocoPilot.Models.Statistics;
using RocoPilot.Services.Statistics;
using RocoPilot.Tests.TestDoubles;

namespace RocoPilot.Tests;

[TestClass]
public sealed class StatisticsSyncSettingsTests
{
    [TestMethod]
    public async Task FailedSaveKeepsCachedSettingsAndSyncBaseline()
    {
        var store = new ControlledSettingsStore();
        store.Seed(SettingsKeys.StatisticsSyncSettings, new StatisticsSyncSettings
        {
            ProviderId = "cloudflare-r2",
            RemotePath = "original.json",
            LastSyncedRemoteEntityTag = "previous-version"
        });
        var sync = CreateService(store);
        var settings = await sync.LoadSettingsAsync();
        settings.RemotePath = "changed.json";
        store.BeforeSave = _ => throw new IOException("save failed");
        await Assert.ThrowsExactlyAsync<IOException>(() => sync.SaveSettingsAsync(settings, password: null));
        var afterFailure = await sync.LoadSettingsAsync();
        Assert.AreEqual("original.json", afterFailure.RemotePath);
        Assert.AreEqual("previous-version", afterFailure.LastSyncedRemoteEntityTag);

        store.BeforeSave = null;
        await sync.SaveSettingsAsync(settings, password: null);
        var afterSuccess = await sync.LoadSettingsAsync();
        Assert.AreEqual("changed.json", afterSuccess.RemotePath);
        Assert.IsNull(afterSuccess.LastSyncedRemoteEntityTag);
    }

    [TestMethod]
    public async Task ConcurrentLoadsInitializeOnce()
    {
        var store = new ControlledSettingsStore();
        using var pause = new AsyncPause();
        store.BeforeRead = pause.PauseAsync;
        var sync = CreateService(store);
        var first = sync.LoadSettingsAsync();
        await pause.WaitUntilEnteredAsync();
        var others = Enumerable.Range(0, 10).Select(_ => sync.LoadSettingsAsync()).ToArray();
        Assert.AreEqual(1, store.ReadCount);
        pause.Dispose();
        await Task.WhenAll(others.Append(first));
        Assert.AreEqual(1, store.ReadCount);
    }

    [TestMethod]
    public async Task CloudMergeDoesNotTriggerUploadPreparationButNextLocalEditDoes()
    {
        var store = new ControlledSettingsStore();
        var statistics = new StatisticsService(store, NullLogger<StatisticsService>.Instance);
        var sync = new StatisticsSyncService(store, statistics, NullLogger<StatisticsSyncService>.Instance, new StatisticsRemoteStoreStub(), new StatisticsCredentialsStub());
        var settingsRead = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        store.BeforeRead = key =>
        {
            if (key == SettingsKeys.StatisticsSyncSettings) settingsRead.TrySetResult();
            return Task.CompletedTask;
        };

        await statistics.MergeRemoteAsync(new StatisticsDocument(), null, false);
        Assert.IsFalse(settingsRead.Task.IsCompleted);
        await statistics.AddAccountAsync("100");
        await settingsRead.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.IsFalse((await sync.LoadSettingsAsync()).IsEnabled);
    }

    private static StatisticsSyncService CreateService(ControlledSettingsStore store) => new(
        store,
        new StatisticsService(store, NullLogger<StatisticsService>.Instance),
        NullLogger<StatisticsSyncService>.Instance, new StatisticsRemoteStoreStub(), new StatisticsCredentialsStub());
}
