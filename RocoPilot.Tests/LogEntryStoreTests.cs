using Microsoft.VisualStudio.TestTools.UnitTesting;
using RocoPilot.Models.Logging;
using RocoPilot.Services.Logging;

namespace RocoPilot.Tests;

[TestClass]
public sealed class LogEntryStoreTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 16, 21, 4, 12, 381, TimeSpan.FromHours(8));

    [TestMethod]
    public void KeepsMostRecentEntriesAndTheirCountsWhenCapacityIsExceeded()
    {
        var store = new LogEntryStore(capacity: 3);
        store.Add(Now, LogSeverity.Information, "A", "第 1 条");
        store.Add(Now, LogSeverity.Warning, "A", "第 2 条");
        store.Add(Now, LogSeverity.Error, "A", "第 3 条");
        store.Add(Now, LogSeverity.Information, "A", "第 4 条");
        store.Add(Now, LogSeverity.Fatal, "A", "第 5 条");

        var batch = store.ReadFrom(0);

        CollectionAssert.AreEqual(
            new[] { "第 3 条", "第 4 条", "第 5 条" },
            batch.Entries.Select(entry => entry.Message).ToArray());
        CollectionAssert.AreEqual(new long[] { 2, 3, 4 }, batch.Entries.Select(entry => entry.Sequence).ToArray());
        Assert.AreEqual(2, batch.FirstSequence);
        Assert.AreEqual(5, batch.NextSequence);
        Assert.AreEqual(new LogEntryCounts(Information: 1, Warning: 0, Error: 2), batch.Counts);
    }

    [TestMethod]
    public void ReaderContinuesFromItsLastPosition()
    {
        var store = new LogEntryStore();
        store.Add(Now, LogSeverity.Information, "A", "第 1 条");
        store.Add(Now, LogSeverity.Information, "A", "第 2 条");
        var first = store.ReadFrom(0);

        store.Add(Now, LogSeverity.Warning, "A", "第 3 条");
        var second = store.ReadFrom(first.NextSequence);
        var third = store.ReadFrom(second.NextSequence);

        Assert.AreEqual(2, first.Entries.Count);
        Assert.AreEqual("第 3 条", second.Entries.Single().Message);
        Assert.AreEqual(0, third.Entries.Count);
        Assert.AreEqual(3, third.Counts.Total);
    }

    [TestMethod]
    public void ReaderThatFellBehindGetsEverythingStillRetained()
    {
        var store = new LogEntryStore(capacity: 2);
        for (var index = 1; index <= 5; index++)
        {
            store.Add(Now, LogSeverity.Information, "A", $"第 {index} 条");
        }

        var batch = store.ReadFrom(1);

        CollectionAssert.AreEqual(
            new[] { "第 4 条", "第 5 条" },
            batch.Entries.Select(entry => entry.Message).ToArray());
        Assert.AreEqual(3, batch.FirstSequence);
    }

    [TestMethod]
    public void RaisesChangedAfterEachEntryIsReadable()
    {
        var store = new LogEntryStore();
        var seen = new List<int>();
        store.Changed += (_, _) => seen.Add(store.ReadFrom(0).Entries.Count);

        store.Add(Now, LogSeverity.Information, "A", "第 1 条");
        store.Add(Now, LogSeverity.Information, "A", "第 2 条");

        CollectionAssert.AreEqual(new[] { 1, 2 }, seen);
    }

    [TestMethod]
    public void FormatsEntriesLikeLogFileLines()
    {
        var store = new LogEntryStore();
        store.Add(Now, LogSeverity.Information, "RocoPilot.Services.RuntimeTaskService", "实时任务已停止");
        store.Add(Now, LogSeverity.Error, "RocoPilot.App", "任务异常退出", "System.IO.IOException: 窗口不可用");

        var text = LogEntryFormatter.Format(store.ReadFrom(0).Entries);

        Assert.AreEqual(
            "[2026-11-16 21:04:12.381] [INF] [RocoPilot.Services.RuntimeTaskService] 实时任务已停止" + Environment.NewLine
            + "[2026-11-16 21:04:12.381] [ERR] [RocoPilot.App] 任务异常退出" + Environment.NewLine
            + "System.IO.IOException: 窗口不可用" + Environment.NewLine,
            text);
    }
}
