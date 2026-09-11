using System.Collections.Concurrent;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using RocoPilot.Configuration;
using RocoPilot.Contracts.Services.Statistics;
using RocoPilot.Models.Encounters;
using RocoPilot.Models.Statistics;
using RocoPilot.Services.Statistics;
using RocoPilot.Tests.TestDoubles;

namespace RocoPilot.Tests;

[TestClass]
public sealed class StatisticsServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 11, 0, 0, 0, TimeSpan.Zero);
    private static readonly EncounterSeasonDefinition Season = new() { Id = "S3", Name = "S3赛季" };

    [TestMethod]
    public async Task FailedRecordDoesNotBecomeVisibleOrCountTwiceWhenRetried()
    {
        var (service, store) = await CreateServiceAsync();
        var notifications = 0;
        service.DocumentChanged += (_, _) => notifications++;
        store.BeforeSave = _ => throw new IOException("save failed");

        await Assert.ThrowsExactlyAsync<IOException>(() => service.RecordEncounterAsync(Season, "精灵", Now));
        Assert.AreEqual(1, Count(service.CurrentDocument));
        Assert.AreEqual(0, notifications);
        store.BeforeSave = null;
        await service.RecordEncounterAsync(Season, "精灵", Now);
        Assert.AreEqual(2, Count(service.CurrentDocument));
        Assert.AreEqual(2, Count(store.ReadSaved<StatisticsDocument>(SettingsKeys.StatisticsData)!));
        Assert.AreEqual(1, notifications);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task FailedAccountRemovalKeepsAccountSelection(bool clearAll)
    {
        var (service, store) = await CreateServiceAsync();
        store.BeforeSave = _ => throw new IOException("save failed");
        await Assert.ThrowsExactlyAsync<IOException>(() => clearAll ? service.ClearAsync() : service.DeleteAccountAsync("100"));
        Assert.AreEqual("100", service.SelectedAccountUid);
        Assert.AreEqual("100", service.ActiveAccountUid);
        Assert.IsFalse(service.IsActiveAccountSelectionRequired);
        Assert.AreEqual(1, service.CurrentDocument.Accounts.Count);

        store.BeforeSave = null;
        await service.DeleteAccountAsync("100");
        Assert.IsNull(service.SelectedAccountUid);
        Assert.IsNull(service.ActiveAccountUid);
        Assert.IsTrue(service.IsActiveAccountSelectionRequired);
    }

    [TestMethod]
    public async Task ReadersOnlySeeCommittedSnapshotWhileSaveIsPending()
    {
        var (service, store) = await CreateServiceAsync();
        using var pause = new AsyncPause();
        store.BeforeSave = pause.PauseAsync;
        var recording = service.RecordEncounterAsync(Season, "精灵", Now);
        await pause.WaitUntilEnteredAsync();
        Assert.AreEqual(1, Count(service.CurrentDocument));
        Assert.AreEqual(1, service.GetActiveAccountSeasonEncounters("S3").Single().Count);
        pause.Dispose();
        await recording;
        Assert.AreEqual(2, Count(service.CurrentDocument));
    }

    [TestMethod]
    public async Task ReplacementInputResultsAndNotificationsCannotMutateCommittedState()
    {
        var (service, _) = await CreateServiceAsync();
        service.DocumentChanged += (_, e) => e.Document.Accounts.Clear();
        var input = Document(5);
        await service.ReplaceAsync(input);
        input.Accounts.Clear();
        var snapshot = service.CurrentDocument;
        snapshot.Accounts.Clear();
        Assert.AreEqual(5, Count(service.CurrentDocument));
    }

    [TestMethod]
    public async Task LoadFailureDoesNotCacheEmptyDataAndNextAttemptRetries()
    {
        var store = new ControlledSettingsStore();
        store.Seed(SettingsKeys.StatisticsData, Document(7));
        store.BeforeRead = _ => throw new IOException("read failed");
        var service = new StatisticsService(store, NullLogger<StatisticsService>.Instance);
        await Assert.ThrowsExactlyAsync<IOException>(() => service.AddAccountAsync("200"));
        Assert.AreEqual(0, store.SaveCount);
        store.BeforeRead = null;
        var loaded = await service.LoadAsync();
        Assert.AreEqual(7, Count(loaded));
        Assert.AreEqual(2, store.ReadCount);
    }

    [TestMethod]
    public async Task ConcurrentRecordsPreserveEveryIncrement()
    {
        var (service, store) = await CreateServiceAsync();
        await Task.WhenAll(Enumerable.Range(0, 40).Select(_ => service.RecordEncounterAsync(Season, "精灵", Now)));
        Assert.AreEqual(41, Count(service.CurrentDocument));
        Assert.AreEqual(41, Count(store.ReadSaved<StatisticsDocument>(SettingsKeys.StatisticsData)!));
    }

    [TestMethod]
    public async Task MergeUsesLatestCommittedLocalChangesAndKeepsCloudChangesToOtherAccounts()
    {
        var (service, store) = await CreateServiceAsync();
        var remote = Document(1);
        remote.Accounts.Add(new AccountStatisticsData { Uid = "200" });
        var baseline = StatisticsDocumentMerger.ComputeAccountFingerprints(service.CurrentDocument);
        using var pause = new AsyncPause();
        store.BeforeSave = pause.PauseAsync;
        var recording = service.RecordEncounterAsync(Season, "精灵", Now);
        await pause.WaitUntilEnteredAsync();
        var merging = service.MergeRemoteAsync(remote, baseline, false);
        Assert.IsFalse(merging.IsCompleted);
        pause.Dispose();
        await Task.WhenAll(recording, merging);

        Assert.AreEqual(2, Count(service.CurrentDocument));
        Assert.AreEqual(2, service.CurrentDocument.Accounts.Count);
        Assert.AreEqual(0, (await merging).ConflictingAccountUids.Count);
    }

    [TestMethod]
    public async Task RecordQueuedDuringMergeRunsAfterMergeAndRetainsItsOwnChangeSource()
    {
        var (service, store) = await CreateServiceAsync();
        var sources = new ConcurrentBag<StatisticsDocumentChangeSource>();
        service.DocumentChanged += (_, e) => sources.Add(e.Source);
        var baseline = StatisticsDocumentMerger.ComputeAccountFingerprints(service.CurrentDocument);
        using var pause = new AsyncPause();
        store.BeforeSave = pause.PauseAsync;
        var merging = service.MergeRemoteAsync(Document(5), baseline, false);
        await pause.WaitUntilEnteredAsync();
        var recording = service.RecordEncounterAsync(Season, "精灵", Now);
        Assert.IsFalse(recording.IsCompleted);
        pause.Dispose();
        await Task.WhenAll(merging, recording);

        Assert.AreEqual(6, Count(service.CurrentDocument));
        CollectionAssert.AreEquivalent(new[] { StatisticsDocumentChangeSource.CloudSync, StatisticsDocumentChangeSource.Local }, sources.ToArray());
    }

    [TestMethod]
    public async Task FailedMergeKeepsOriginalAndSuccessfulRetryReportsConflicts()
    {
        var (service, store) = await CreateServiceAsync();
        var baseline = StatisticsDocumentMerger.ComputeAccountFingerprints(Document(0));
        store.BeforeSave = _ => throw new IOException("save failed");
        await Assert.ThrowsExactlyAsync<IOException>(() => service.MergeRemoteAsync(Document(9), baseline, false));
        Assert.AreEqual(1, Count(service.CurrentDocument));

        store.BeforeSave = null;
        var merged = await service.MergeRemoteAsync(Document(9), baseline, false);
        Assert.AreEqual(9, Count(merged.Document));
        CollectionAssert.AreEqual(new[] { "100" }, merged.ConflictingAccountUids.ToArray());
    }

    [TestMethod]
    public async Task CancelledMergeWaitingForWriterDoesNotWrite()
    {
        var (service, store) = await CreateServiceAsync();
        using var pause = new AsyncPause();
        store.BeforeSave = pause.PauseAsync;
        var recording = service.RecordEncounterAsync(Season, "精灵", Now);
        await pause.WaitUntilEnteredAsync();
        using var cancellation = new CancellationTokenSource();
        var merging = service.MergeRemoteAsync(Document(9), null, false, cancellation.Token);
        cancellation.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => merging);
        pause.Dispose();
        await recording;
        Assert.AreEqual(2, Count(service.CurrentDocument));
        Assert.AreEqual(1, store.SaveCount);
    }

    [TestMethod]
    public async Task ManualEditsUseSelectedAccountWhileAutomaticRecordsUseActiveAccount()
    {
        var (service, _) = await CreateServiceAsync();
        await service.AddAccountAsync("200");
        service.SetSelectedAccountUid("200");
        await service.UpsertEncounterAsync("S3", "手动精灵", 3, Now);
        await service.EditEncounterAsync("S3", "手动精灵", "改名", 4, Now);
        await service.RecordEncounterAsync(Season, "精灵", Now);
        Assert.AreEqual(2, Count(service.CurrentDocument));
        Assert.AreEqual(4, service.CurrentDocument.Accounts.Single(a => a.Uid == "200").Seasons.Single().Encounters.Single().Count);
        await service.DeleteEncounterAsync("S3", "改名");
        Assert.AreEqual(0, service.CurrentDocument.Accounts.Single(a => a.Uid == "200").Seasons.Single().Encounters.Count);
    }

    [TestMethod]
    public async Task PendingAndConfirmedShinyMutationsShareTheCommitPath()
    {
        var (service, _) = await CreateServiceAsync();
        await service.AddPendingShinyCaptureAsync(Season, "异色精灵", Now);
        var pending = service.GetSelectedAccountPendingShinyCaptures().Single();
        await service.ConfirmPendingShinyCaptureAsync(pending.Id, "异色精灵", 8, Now);
        Assert.AreEqual(0, service.GetSelectedAccountPendingShinyCaptures().Count);
        var capture = service.CurrentDocument.Accounts.Single().Seasons.Single().ShinyCaptures.Single();
        await service.EditShinyCaptureAsync(capture.Id, "改名", 9, Now);
        Assert.AreEqual(9, service.CurrentDocument.Accounts.Single().Seasons.Single().ShinyCaptures.Single().EncounterCountBeforeCapture);
        await service.DeleteShinyCaptureAsync(capture.Id);
        await service.AddShinyCapturesAsync("S3", "批量", 2, Now);
        await service.DeleteShinyCapturesAsync("S3", "批量");
        Assert.AreEqual(0, service.CurrentDocument.Accounts.Single().Seasons.Single().ShinyCaptures.Count);
        await service.AddPendingShinyCaptureAsync(Season, "放弃", Now);
        await service.DiscardPendingShinyCaptureAsync(service.GetSelectedAccountPendingShinyCaptures().Single().Id);
        Assert.AreEqual(0, service.GetSelectedAccountPendingShinyCaptures().Count);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task QueuedWriteKeepsAccountThatWasSelectedWhenRequested(bool automatic)
    {
        var (service, store) = await CreateServiceAsync();
        await service.AddAccountAsync("200");
        using var pause = new AsyncPause();
        store.BeforeSave = pause.PauseAsync;
        var holdingLock = service.AddAccountAsync("300");
        await pause.WaitUntilEnteredAsync();
        var queued = automatic ? service.RecordEncounterAsync(Season, "精灵", Now)
            : service.UpsertEncounterAsync("S3", "精灵", 1, Now);
        service.SetSelectedAccountUid("200");
        service.SetActiveAccountUid("200");
        pause.Dispose();
        await Task.WhenAll(holdingLock, queued);
        Assert.AreEqual(2, Count(service.CurrentDocument));
        Assert.AreEqual(0, service.CurrentDocument.Accounts.Single(account => account.Uid == "200").Seasons.Count);
    }

    [TestMethod]
    public async Task DeletedQueuedManualTargetNeverFallsBackToAnotherAccount()
    {
        var (service, store) = await CreateServiceAsync();
        await service.AddAccountAsync("200");
        using var pause = new AsyncPause();
        store.BeforeSave = pause.PauseAsync;
        var deleting = service.DeleteAccountAsync("100");
        await pause.WaitUntilEnteredAsync();
        var editing = service.UpsertEncounterAsync("S3", "精灵", 1, Now);
        var saves = store.SaveCount;
        pause.Dispose();
        await deleting;
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => editing);
        Assert.AreEqual(saves, store.SaveCount);
        Assert.AreEqual("200", service.SelectedAccountUid);
        Assert.AreEqual(0, service.CurrentDocument.Accounts.Single().Seasons.Count);
    }

    [TestMethod]
    public async Task DeletedAutomaticTargetIsSkippedWithoutClearingNewActiveAccount()
    {
        var (service, store) = await CreateServiceAsync();
        await service.AddAccountAsync("200");
        using var pause = new AsyncPause();
        store.BeforeSave = pause.PauseAsync;
        var deleting = service.DeleteAccountAsync("100");
        await pause.WaitUntilEnteredAsync();
        var recording = service.RecordEncounterAsync(Season, "精灵", Now);
        service.SetActiveAccountUid("200");
        pause.Dispose();
        await Task.WhenAll(deleting, recording);
        Assert.AreEqual("200", service.ActiveAccountUid);
        Assert.IsFalse(service.IsActiveAccountSelectionRequired);
        Assert.AreEqual(0, service.CurrentDocument.Accounts.Single().Seasons.Count);
    }

    [TestMethod]
    public async Task RecordRequestedBeforeUidConfirmationIsNotCreditedLater()
    {
        var (service, store) = await CreateServiceAsync();
        service.RequireActiveAccountSelection();
        using var pause = new AsyncPause();
        store.BeforeSave = pause.PauseAsync;
        var holdingLock = service.AddAccountAsync("200");
        await pause.WaitUntilEnteredAsync();
        var recording = service.RecordEncounterAsync(Season, "精灵", Now);
        service.SetActiveAccountUid("100");
        pause.Dispose();
        await Task.WhenAll(holdingLock, recording);
        Assert.AreEqual(1, Count(service.CurrentDocument));
    }

    private static async Task<(StatisticsService, ControlledSettingsStore)> CreateServiceAsync()
    {
        var store = new ControlledSettingsStore();
        store.Seed(SettingsKeys.StatisticsData, Document(1));
        var service = new StatisticsService(store, NullLogger<StatisticsService>.Instance);
        await service.LoadAsync();
        service.SetSelectedAccountUid("100");
        service.SetActiveAccountUid("100");
        return (service, store);
    }

    private static int Count(StatisticsDocument document) =>
        document.Accounts.Single(a => a.Uid == "100").Seasons.Single().Encounters.Single().Count;

    private static StatisticsDocument Document(int count) => new()
    {
        Accounts = [new()
        {
            Uid = "100",
            Seasons = [new()
            {
                Id = "S3",
                Encounters = [new() { Name = "精灵", Count = count, Season = "S3", LastCapturedAt = Now }]
            }]
        }]
    };
}
