using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using RocoPilot.Configuration;
using RocoPilot.Models.Encounters;
using RocoPilot.Models.Statistics;
using RocoPilot.Services.Statistics;
using RocoPilot.Tests.TestDoubles;
using RocoPilot.ViewModels;

namespace RocoPilot.Tests;

[TestClass]
public sealed class StatisticsViewModelTests
{
    [TestMethod]
    public async Task OldEventAndOperationResultCannotRollBackNewerDocument()
    {
        var (service, _) = await CreateServiceAsync();
        var nestedWrite = false;
        service.DocumentChanged += (_, _) =>
        {
            if (nestedWrite) return;
            nestedWrite = true;
            // 第二次提交先于第一次通知送达 ViewModel。
            service.EditEncounterAsync("S1", "精灵", "精灵", 25, DateTimeOffset.Now).GetAwaiter().GetResult();
        };
        var viewModel = CreateViewModel(service);

        await viewModel.AddEncounterAsync("S1", "精灵", 10);

        Assert.AreEqual(25, viewModel.Overview.Seasons.Single(item => item.Id == "S1").PollutionTotal);
    }

    [TestMethod]
    public async Task BackgroundEventsShareOneRefreshAndUseLatestAccountSelection()
    {
        var (service, _) = await CreateServiceAsync();
        var queue = new ConcurrentQueue<Action>();
        var viewModel = CreateViewModel(service, dispatch: queue.Enqueue);
        service.SetSelectedAccountUid("200");
        service.SetSelectedAccountUid("100");
        await service.UpsertEncounterAsync("S1", "精灵", 12, DateTimeOffset.Now);
        await service.UpsertEncounterAsync("S1", "精灵", 13, DateTimeOffset.Now);

        Assert.AreEqual(1, queue.Count);
        queue.TryDequeue(out var refresh);
        refresh!();

        Assert.AreEqual("100", viewModel.Overview.SelectedAccount?.Uid);
        Assert.AreEqual(33, viewModel.Overview.Seasons.Single(item => item.Id == "S1").PollutionTotal);
    }

    [TestMethod]
    public async Task LocalOperationRefreshesOnceThroughServiceEvent()
    {
        var (service, _) = await CreateServiceAsync();
        var viewModel = CreateViewModel(service);
        var refreshCount = 0;
        viewModel.Overview.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(StatisticsOverviewViewModel.Seasons)) refreshCount++;
        };

        await viewModel.AddEncounterAsync("S1", "精灵", 10);

        Assert.AreEqual(1, refreshCount);
    }

    [TestMethod]
    public async Task DisplayingAndRefreshingAccountDoesNotConfirmActiveAccount()
    {
        var (service, _) = await CreateServiceAsync();
        service.RequireActiveAccountSelection();
        var viewModel = CreateViewModel(service);
        await service.UpsertEncounterAsync("S1", "精灵", 10, DateTimeOffset.Now);

        Assert.IsTrue(service.IsActiveAccountSelectionRequired);
        Assert.IsNull(service.ActiveAccountUid);
        Assert.AreEqual("100", viewModel.Overview.SelectedAccount?.Uid);

        // 再次点击当前展示账号也属于明确选择。
        viewModel.SelectAccount(viewModel.Overview.SelectedAccount!);
        Assert.AreEqual("100", service.ActiveAccountUid);
        Assert.IsFalse(service.IsActiveAccountSelectionRequired);
    }

    [TestMethod]
    public async Task LoadRetriesAfterReadFailureAndCoalescesConcurrentLoads()
    {
        var store = new ControlledSettingsStore();
        store.Seed(SettingsKeys.StatisticsData, StatisticsOverviewViewModelTests.Document());
        store.BeforeRead = _ => throw new IOException("read failed");
        var service = new StatisticsService(store, NullLogger<StatisticsService>.Instance);
        var catalog = new StatisticsSpiritCatalogStub();
        var viewModel = CreateViewModel(service, catalog: catalog);
        await viewModel.LoadAsync();
        Assert.AreEqual(0, viewModel.Overview.Accounts.Count);

        store.BeforeRead = null;
        using var pause = new AsyncPause();
        catalog.BeforeLoad = () => pause.PauseAsync(string.Empty);
        var loading = viewModel.LoadAsync();
        await pause.WaitUntilEnteredAsync();
        Assert.AreEqual(2, viewModel.Overview.Accounts.Count);
        var sameLoading = viewModel.LoadAsync();
        Assert.AreSame(loading, sameLoading);
        pause.Dispose();
        await Task.WhenAll(loading, sameLoading);

        Assert.AreEqual(2, viewModel.Overview.Accounts.Count);
        Assert.AreEqual(2, store.ReadCount);
        Assert.AreEqual(2, catalog.LoadCount);
    }

    [TestMethod]
    public async Task AutomaticRecordsDoNotReloadSeasonConfiguration()
    {
        var (service, _) = await CreateServiceAsync();
        var config = new StatisticsSeasonConfigStub();
        var viewModel = CreateViewModel(service, config: config);
        await viewModel.LoadAsync();
        Assert.AreEqual(1, config.LoadCount);

        await service.RecordEncounterAsync(new EncounterSeasonDefinition { Id = "S1" }, "精灵", DateTimeOffset.Now);
        await service.RecordEncounterAsync(new EncounterSeasonDefinition { Id = "S1" }, "精灵", DateTimeOffset.Now);

        Assert.AreEqual(1, config.LoadCount);
        await viewModel.LoadAsync();
        Assert.AreEqual(2, config.LoadCount);
    }

    [TestMethod]
    public async Task ExportUsesCommittedDataEvenBeforeQueuedRefresh()
    {
        var (service, _) = await CreateServiceAsync();
        var queue = new ConcurrentQueue<Action>();
        var viewModel = CreateViewModel(service, dispatch: queue.Enqueue);
        await service.UpsertEncounterAsync("S1", "精灵", 30, DateTimeOffset.Now);

        var exported = JsonSerializer.Deserialize<StatisticsDocument>(viewModel.ExportToJson(), new JsonSerializerOptions(JsonSerializerDefaults.Web))!;

        Assert.AreEqual(38, exported.Accounts[0].Seasons.Single(item => item.Id == "S1").Encounters.Single().Count);
        Assert.AreEqual(StatisticsDocumentFormats.RocoPilotStatistics, exported.Info.Format);
    }

    [TestMethod]
    public async Task DelayedSyncEventCannotRestoreOldBusyStatus()
    {
        var (service, _) = await CreateServiceAsync();
        var queue = new ConcurrentQueue<Action>();
        var sync = new StatisticsSyncStub();
        var viewModel = CreateViewModel(service, dispatch: queue.Enqueue, sync: sync);
        sync.SetStatus(new StatisticsSyncStatus { IsBusy = true });
        sync.SetStatus(new StatisticsSyncStatus { IsBusy = false });
        var pending = queue.ToArray();

        pending[1]();
        pending[0]();

        Assert.IsFalse(viewModel.IsSyncBusy);
    }

    [TestMethod]
    public async Task FailedPendingConfirmationKeepsDraftAndCanBeRetried()
    {
        var (service, store) = await CreateServiceAsync();
        var viewModel = CreateViewModel(service);
        viewModel.Overview.PendingEditor.EncounterCount = 35;
        store.BeforeSave = _ => throw new IOException("save failed");

        await Assert.ThrowsExactlyAsync<IOException>(() => viewModel.ConfirmLatestPendingShinyAsync());

        Assert.AreEqual(1, viewModel.Overview.PendingShinyCount);
        Assert.AreEqual(35d, viewModel.Overview.PendingEditor.EncounterCount);
        store.BeforeSave = null;
        await viewModel.ConfirmLatestPendingShinyAsync();
        Assert.AreEqual(0, viewModel.Overview.PendingShinyCount);
        var capture = service.CurrentDocument.Accounts[0].Seasons.Single(item => item.Id == "S1").ShinyCaptures.Single();
        Assert.AreEqual(35, capture.EncounterCountBeforeCapture);
    }

    private static async Task<(StatisticsService, ControlledSettingsStore)> CreateServiceAsync()
    {
        var store = new ControlledSettingsStore();
        store.Seed(SettingsKeys.StatisticsData, StatisticsOverviewViewModelTests.Document());
        var service = new StatisticsService(store, NullLogger<StatisticsService>.Instance);
        await service.LoadAsync();
        service.SetSelectedAccountUid("100");
        return (service, store);
    }

    private static StatisticsViewModel CreateViewModel(
        StatisticsService service,
        Action<Action>? dispatch = null,
        StatisticsSeasonConfigStub? config = null,
        StatisticsSpiritCatalogStub? catalog = null,
        StatisticsSyncStub? sync = null) => new(
            service,
            new StatisticsUidCoordinatorStub(),
            sync ?? new StatisticsSyncStub(),
            config ?? new StatisticsSeasonConfigStub(),
            catalog ?? new StatisticsSpiritCatalogStub(),
            NullLogger<StatisticsViewModel>.Instance,
            dispatch ?? (action => action()));
}
