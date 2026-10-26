using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using RocoPilot.Configuration;
using RocoPilot.Contracts.Services.Encounters;
using RocoPilot.Models.Encounters;
using RocoPilot.Models.Spirits;
using RocoPilot.Models.Statistics;
using RocoPilot.Services.Statistics;
using RocoPilot.Tests.TestDoubles;
using RocoPilot.ViewModels;

namespace RocoPilot.Tests;

[TestClass]
public sealed class PendingEncounterTests
{
    [TestMethod]
    public async Task NormalSeasonNeverCreatesTemporaryEncounter()
    {
        var f = await TemporaryStatisticsFixture.Create();
        Assert.IsNull(await f.Service.RecordTemporaryEncounterAsync("100", "normal", "未知", "", TemporaryStatisticsFixture.Date.AddDays(-1)));
        await f.Service.RecordEncounterAsync(TemporaryStatisticsFixture.S4, "小火苗", TemporaryStatisticsFixture.Date.AddDays(-1));
        Assert.IsNull(f.Account.Temporary);
        Assert.AreEqual(1, f.Account.Seasons.Single().Encounters.Single().Count);
    }

    [TestMethod]
    public async Task AggregatesByDayAndRawTextWithoutPerEncounterHistory()
    {
        var f = await TemporaryStatisticsFixture.Create();
        for (var i = 0; i < 100; i++) await f.Record("event" + i, "新精灵");
        await f.Record("tomorrow", "新精灵", 1);
        Assert.AreEqual(2, f.Account.Temporary!.Encounters.Count);
        Assert.AreEqual(101, f.Account.Temporary.Encounters.Sum(x => x.Count));
        Assert.AreEqual(0, f.Account.PendingEncounters.Count);
        Assert.AreEqual(1, f.Items.Count);
        Assert.AreEqual(101, f.Items.Single().Count);
    }

    [TestMethod]
    public async Task EmptyOcrCanBeCompletedOnceAndDoesNotLoseAnotherUnknownEncounter()
    {
        var f = await TemporaryStatisticsFixture.Create();
        await f.Record("previous", "");
        var bucket = await f.Record("current", "");
        await f.Record("current", "");
        var completed = await f.Service.RecordTemporaryEncounterAsync("100", "current", "小火苗", "小火苗", TemporaryStatisticsFixture.Date, bucket);
        await f.Service.RecordTemporaryEncounterAsync("100", "current", "小火苗", "小火苗", TemporaryStatisticsFixture.Date, bucket);
        Assert.AreEqual(2, f.Account.Temporary!.Encounters.Sum(x => x.Count));
        Assert.AreEqual(1, f.Account.Temporary.Encounters.Single(x => x.Id == completed).Count);
        Assert.AreEqual(1, f.Account.Temporary.Encounters.Single(x => x.RawText == "").Count);
    }

    [TestMethod]
    public async Task ExactCatalogRefreshOnlyUpdatesDisplayAndNeverCounts()
    {
        var f = await TemporaryStatisticsFixture.Create();
        await f.Record("exact", "烈火王");
        await f.Record("near", "小火猫");
        Assert.AreEqual(1, await f.Service.RefreshTemporaryNamesAsync(TemporaryStatisticsFixture.Catalog));
        Assert.AreEqual("小火苗", f.Account.Temporary!.Encounters.Single(x => x.RawText == "烈火王").Name);
        Assert.AreEqual("", f.Account.Temporary.Encounters.Single(x => x.RawText == "小火猫").Name);
        Assert.AreEqual(0, f.Account.Seasons.Sum(x => x.Encounters.Count));
        Assert.AreEqual(0, await f.Service.RefreshTemporaryNamesAsync(TemporaryStatisticsFixture.Catalog));
    }

    [TestMethod]
    public async Task ConfirmationDeletesAllSourceDatesAndAllowsEditedNameCountAndSeason()
    {
        var f = await TemporaryStatisticsFixture.Create();
        await f.Record("a", "原文", 0); await f.Record("b", "原文", 1); await f.Record("c", "保留", 2);
        f.Config.Seasons = [TemporaryStatisticsFixture.S4, TemporaryStatisticsFixture.S5];
        var selected = f.Items.Single(x => x.Name == "原文");
        var transfer = TemporaryStatisticsFixture.Transfer(selected) with { Name = "修正名", Count = 7, SeasonId = "S4" };
        await f.Service.TransferTemporaryStatisticsAsync([transfer]);
        await f.Service.TransferTemporaryStatisticsAsync([transfer]);
        Assert.AreEqual(7, f.Account.Seasons.Single(x => x.Id == "S4").Encounters.Single().Count);
        Assert.AreEqual("保留", f.Account.Temporary!.Encounters.Single().RawText);
        var json = JsonSerializer.Serialize(f.Store.ReadSaved<StatisticsDocument>(SettingsKeys.StatisticsData));
        Assert.IsFalse(json.Contains("原文"));
        foreach (var id in selected.EncounterIds) Assert.IsFalse(json.Contains(id));
    }

    [TestMethod]
    public async Task ZeroCountStillDeletesAllSelectedData()
    {
        var f = await TemporaryStatisticsFixture.Create(); await f.Record("a", "原文"); f.Config.AddNextSeason();
        await f.Service.TransferTemporaryStatisticsAsync([TemporaryStatisticsFixture.Transfer(f.Items.Single()) with { Count = 0 }]);
        Assert.IsNull(f.Account.Temporary);
        Assert.AreEqual(0, f.Account.Seasons.Sum(x => x.Encounters.Count));
    }

    [TestMethod]
    public async Task FailedPersistenceLeavesOriginalSnapshotAndRetryCountsOnlyOnce()
    {
        var f = await TemporaryStatisticsFixture.Create(); await f.Record("a", "原文"); f.Config.AddNextSeason();
        var input = TemporaryStatisticsFixture.Transfer(f.Items.Single());
        var original = JsonSerializer.Serialize(f.Account);
        f.Store.BeforeSave = key => key == SettingsKeys.StatisticsData ? Task.FromException(new IOException("disk full")) : Task.CompletedTask;
        await Assert.ThrowsExactlyAsync<IOException>(() => f.Service.TransferTemporaryStatisticsAsync([input]));
        Assert.AreEqual(original, JsonSerializer.Serialize(f.Account));
        Assert.AreEqual(original, JsonSerializer.Serialize(f.Store.ReadSaved<StatisticsDocument>(SettingsKeys.StatisticsData)!.Accounts.Single()));
        f.Store.BeforeSave = null;
        await f.Service.TransferTemporaryStatisticsAsync([input]); await f.Service.TransferTemporaryStatisticsAsync([input]);
        Assert.IsNull(f.Account.Temporary);
        Assert.AreEqual(1, f.Account.Seasons.Single().Encounters.Single().Count);
    }

    [TestMethod]
    public async Task NewCountWhileDialogOpenRejectsWholeBatchWithoutDeletingAnything()
    {
        var f = await TemporaryStatisticsFixture.Create(); await f.Record("a", "甲"); await f.Record("b", "乙");
        f.Config.AddNextSeason(); var inputs = f.Items.Select(TemporaryStatisticsFixture.Transfer).ToArray();
        f.Config.Seasons = [TemporaryStatisticsFixture.S4]; await f.Record("c", "乙"); f.Config.AddNextSeason();
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => f.Service.TransferTemporaryStatisticsAsync(inputs));
        Assert.AreEqual(3, f.Account.Temporary!.Encounters.Sum(x => x.Count));
        Assert.AreEqual(0, f.Account.Seasons.Sum(x => x.Encounters.Count));
    }

    [TestMethod]
    public async Task DeletedUnknownBucketIsNotResurrectedByLateOcr()
    {
        var f = await TemporaryStatisticsFixture.Create(); var bucket = await f.Record("a", ""); f.Config.AddNextSeason();
        await f.Service.TransferTemporaryStatisticsAsync([TemporaryStatisticsFixture.Transfer(f.Items.Single()) with { Name = "人工名" }]);
        Assert.IsNull(await f.Service.RecordTemporaryEncounterAsync("100", "a", "迟到", "", TemporaryStatisticsFixture.Date, bucket));
        Assert.IsNull(f.Account.Temporary);
    }

    [TestMethod]
    [DataRow(false)] [DataRow(true)]
    public async Task OldCloudCannotRestoreDeletedBucketsOrEraseNewUnsyncedCounts(bool useBaseline)
    {
        var f = await TemporaryStatisticsFixture.Create(); await f.Record("a", "已处理");
        var stale = f.Service.CurrentDocument;
        var baseline = useBaseline ? StatisticsDocumentMerger.ComputeAccountFingerprints(stale) : null;
        f.Config.AddNextSeason(); await f.Service.TransferTemporaryStatisticsAsync([TemporaryStatisticsFixture.Transfer(f.Items.Single())]);
        f.Config.Seasons = [TemporaryStatisticsFixture.S4]; await f.Record("b", "新数据");
        await f.Service.MergeRemoteAsync(stale, baseline, false);
        await f.Service.MergeRemoteAsync(stale, null, true);
        Assert.AreEqual("新数据", f.Account.Temporary!.Encounters.Single().RawText);
        Assert.AreEqual(1, f.Account.Seasons.Single().Encounters.Single().Count);
    }

    [TestMethod]
    public async Task ConcurrentDevicesFailWithoutOverwritingEitherUnsyncedBranch()
    {
        var f = await TemporaryStatisticsFixture.Create(); await f.Record("a", "共同");
        var store2 = new ControlledSettingsStore(); store2.Seed(SettingsKeys.StatisticsData, f.Service.CurrentDocument);
        var second = new StatisticsService(store2, NullLogger<StatisticsService>.Instance, f.Config); await second.LoadAsync();
        await f.Record("left", "本地新增");
        await second.RecordTemporaryEncounterAsync("100", "right", "云端新增", "", TemporaryStatisticsFixture.Date);
        var local = JsonSerializer.Serialize(f.Service.CurrentDocument); var remote = second.CurrentDocument;
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => f.Service.MergeRemoteAsync(remote, null, false));
        Assert.AreEqual(local, JsonSerializer.Serialize(f.Service.CurrentDocument));
        Assert.IsTrue(remote.Accounts.Single().Temporary!.Encounters.Any(x => x.RawText == "云端新增"));
    }

    [TestMethod]
    public async Task SequentialDeviceHandoffKeepsDeletionAndDoesNotGrowEventMarkers()
    {
        var f = await TemporaryStatisticsFixture.Create();
        for (var i = 0; i < 30; i++)
        {
            f.Config.Seasons = [TemporaryStatisticsFixture.S4]; await f.Record("event" + i, "精灵" + i);
            f.Config.AddNextSeason(); await f.Service.TransferTemporaryStatisticsAsync([TemporaryStatisticsFixture.Transfer(f.Items.Single())]);
        }
        Assert.AreEqual(1, f.Account.SyncVersion!.Devices.Count);
        Assert.IsNull(f.Account.Temporary); Assert.AreEqual(0, f.Account.PendingEncounters.Count);
        var store2 = new ControlledSettingsStore(); store2.Seed(SettingsKeys.StatisticsData, f.Service.CurrentDocument);
        var second = new StatisticsService(store2, NullLogger<StatisticsService>.Instance, f.Config); await second.LoadAsync();
        await second.RecordEncounterAsync(TemporaryStatisticsFixture.S5, "正常新数据", TemporaryStatisticsFixture.Date);
        await f.Service.MergeRemoteAsync(second.CurrentDocument, null, false);
        Assert.AreEqual(2, f.Account.SyncVersion.Devices.Count);
        Assert.IsTrue(f.Account.Seasons.Single().Encounters.Any(x => x.Name == "正常新数据"));
    }
    [TestMethod]
    public async Task ActualLocalSerializerPreservesDateBucketsAndVersionAcrossRestart()
    {
        var f = await TemporaryStatisticsFixture.Create(); await f.Record("a", "原始 OCR");
        var json = await RocoPilot.Core.Helpers.Json.StringifyAsync(f.Service.CurrentDocument);
        var restored = await RocoPilot.Core.Helpers.Json.ToObjectAsync<StatisticsDocument>(json);
        Assert.AreEqual(DateOnly.FromDateTime(TemporaryStatisticsFixture.Date.DateTime), restored.Accounts.Single().Temporary!.Encounters.Single().Date);
        Assert.AreEqual(StatisticsDocumentMerger.ComputeAccountFingerprints(f.Service.CurrentDocument)["100"],
            StatisticsDocumentMerger.ComputeAccountFingerprints(restored)["100"]);
    }

    [TestMethod]
    public async Task ExplicitImportResolvesVersionsWithoutOldCloudRollingItBack()
    {
        var f = await TemporaryStatisticsFixture.Create(); await f.Record("a", "共同");
        var store = new ControlledSettingsStore(); store.Seed(SettingsKeys.StatisticsData, f.Service.CurrentDocument);
        var remote = new StatisticsService(store, NullLogger<StatisticsService>.Instance, f.Config); await remote.LoadAsync();
        await remote.RecordTemporaryEncounterAsync("100", "r", "云端", "", TemporaryStatisticsFixture.Date);
        await f.Record("l", "本地");
        var chosen = remote.CurrentDocument;
        await f.Service.ReplaceAsync(chosen);
        await f.Service.MergeRemoteAsync(chosen, null, false);
        Assert.AreEqual(2, f.Account.Temporary!.Encounters.Sum(x => x.Count));
        Assert.AreEqual(2, f.Account.SyncVersion!.Devices.Count);
    }

    [TestMethod]
    public async Task ConcurrentRemoteAccountDeletionCannotDiscardLocalUnsyncedData()
    {
        var f = await TemporaryStatisticsFixture.Create(); await f.Record("a", "共同");
        var baseline = StatisticsDocumentMerger.ComputeAccountFingerprints(f.Service.CurrentDocument);
        await f.Record("b", "未上传");
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => f.Service.MergeRemoteAsync(new(), baseline, false));
        Assert.AreEqual(2, f.Account.Temporary!.Encounters.Sum(x => x.Count));
    }

    [TestMethod]
    public async Task UnchangedLegacyCloudBaselineCannotEraseUnsyncedDataBeforeFirstTemporaryRecord()
    {
        var f = await TemporaryStatisticsFixture.Create();
        var oldCloud = f.Service.CurrentDocument;
        var baseline = StatisticsDocumentMerger.ComputeAccountFingerprints(oldCloud);
        await f.Service.RecordEncounterAsync(TemporaryStatisticsFixture.S4, "旧赛季未同步", TemporaryStatisticsFixture.Date.AddDays(-1));
        await f.Record("a", "新赛季");
        await f.Service.MergeRemoteAsync(oldCloud, baseline, false);
        Assert.AreEqual(1, f.Account.Seasons.Single().Encounters.Single().Count);
        Assert.AreEqual(1, f.Account.Temporary!.Encounters.Single().Count);
    }

    [TestMethod]
    public async Task ExplicitImportOfLegacyCloudResolvesConflictAndKeepsVersionProtection()
    {
        var f = await TemporaryStatisticsFixture.Create(); await f.Record("a", "暂存");
        var legacy = new StatisticsDocument { Accounts = [new() { Uid = "100" }] };
        await f.Service.ReplaceAsync(legacy);
        await f.Service.MergeRemoteAsync(legacy, null, false);
        Assert.IsNull(f.Account.Temporary);
        Assert.IsNotNull(f.Account.SyncVersion);
    }

}

internal sealed class TemporaryStatisticsFixture
{
    public static readonly EncounterSeasonDefinition S4 = new() { Id = "S4", Name = "S4赛季", DateRange = "2026/9/10-2026/11/4" };
    public static readonly EncounterSeasonDefinition S5 = new() { Id = "S5", Name = "S5赛季", DateRange = "2026/11/5-2027/1/6" };
    public static readonly EncounterSeasonDefinition S6 = new() { Id = "S6", Name = "S6赛季", DateRange = "2027/1/7-2027/3/10" };
    public static readonly DateTimeOffset Date = new(2026, 11, 5, 12, 0, 0, TimeSpan.FromHours(8));
    public static SpiritCatalogDocument Catalog => new() { Spirits = [
        new() { Id = "1", Name = "小火苗", BaseId = "1", BaseName = "小火苗", ChainId = "fire" },
        new() { Id = "2", Name = "烈火王", BaseId = "1", BaseName = "小火苗", ChainId = "fire" }] };
    public ControlledSettingsStore Store { get; } = new();
    public TemporarySeasonConfig Config { get; } = new();
    public StatisticsService Service { get; private set; } = null!;
    public AccountStatisticsData Account => Service.CurrentDocument.Accounts.Single();
    public IReadOnlyList<TemporaryStatisticsItem> Items => TemporaryStatisticsItem.Build(Account, Config.Load());
    public static async Task<TemporaryStatisticsFixture> Create()
    {
        var f = new TemporaryStatisticsFixture();
        f.Service = new(f.Store, NullLogger<StatisticsService>.Instance, f.Config);
        await f.Service.AddAccountAsync("100"); f.Service.SetSelectedAccountUid("100"); f.Service.SetActiveAccountUid("100");
        return f;
    }
    public Task<string?> Record(string id, string raw, int days = 0) => Service.RecordTemporaryEncounterAsync("100", id, raw, "", Date.AddDays(days));
    public static TemporaryStatisticsTransfer Transfer(TemporaryStatisticsItem item) => new(item.AccountUid,
        item.Snapshot, item.EncounterIds, item.CaptureIds, item.Name, item.SuggestedSeasonId, item.Count);
}

internal sealed class TemporarySeasonConfig : IEncounterSeasonConfigService
{
    public List<EncounterSeasonDefinition> Seasons { get; set; } = [TemporaryStatisticsFixture.S4];
    public void AddNextSeason() => Seasons = [TemporaryStatisticsFixture.S4, TemporaryStatisticsFixture.S5];
    public EncounterSeasonConfig Load() => new() { CurrentSeasonId = Seasons.Last().Id, Seasons = Seasons };
    public EncounterSeasonDefinition? GetCurrentSeason() => Seasons.Last();
}
