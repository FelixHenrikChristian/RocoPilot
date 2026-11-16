using Microsoft.VisualStudio.TestTools.UnitTesting;
using RocoPilot.Models.Logging;
using RocoPilot.Services.Logging;
using RocoPilot.ViewModels;

namespace RocoPilot.Tests;

[TestClass]
public sealed class LogViewModelTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 16, 21, 4, 12, 381, TimeSpan.FromHours(8));

    [TestMethod]
    public void ShowsRetainedEntriesWhenActivatedAndAppendsNewOnes()
    {
        var store = new LogEntryStore();
        store.Add(Now, LogSeverity.Information, "RocoPilot.App", "RocoPilot 启动");
        var viewModel = new LogViewModel(store);
        var replaced = 0;
        viewModel.EntriesReplaced += (_, _) => replaced++;

        viewModel.Activate();
        var entries = viewModel.Entries;
        store.Add(Now, LogSeverity.Warning, "RocoPilot.Services.RuntimeTaskService", "截图失败");

        // 新日志追加到原列表，已展开的行和滚动位置不受影响。
        Assert.AreSame(entries, viewModel.Entries);
        Assert.AreEqual(1, replaced);
        CollectionAssert.AreEqual(new[] { "RocoPilot 启动", "截图失败" }, Messages(viewModel));
        Assert.AreEqual("共 2 条", viewModel.StatusText);
        Assert.IsTrue(viewModel.HasEntries);
        Assert.AreEqual(1, viewModel.InformationCount);
        Assert.AreEqual(1, viewModel.WarningCount);
        Assert.AreEqual(0, viewModel.ErrorCount);
    }

    [TestMethod]
    public void LevelSwitchesAndKeywordNarrowVisibleEntries()
    {
        var store = new LogEntryStore();
        store.Add(Now, LogSeverity.Information, "RocoPilot.Services.RuntimeTaskService", "实时任务已启动");
        store.Add(Now, LogSeverity.Warning, "RocoPilot.Services.Capture.ScreenCaptureService", "截图失败",
            "System.IO.IOException: 窗口不可用");
        store.Add(Now, LogSeverity.Error, "RocoPilot.Services.IndependentTaskService", "独立任务异常退出");
        store.Add(Now, LogSeverity.Fatal, "RocoPilot.App", "未处理异常");
        var viewModel = new LogViewModel(store);
        viewModel.Activate();

        viewModel.ShowInformation = false;
        CollectionAssert.AreEqual(new[] { "截图失败", "独立任务异常退出", "未处理异常" }, Messages(viewModel));
        Assert.AreEqual("显示 3 / 4 条", viewModel.StatusText);

        viewModel.ShowError = false;
        CollectionAssert.AreEqual(new[] { "截图失败" }, Messages(viewModel));

        viewModel.ShowInformation = true;
        viewModel.ShowError = true;
        viewModel.SearchText = " ioexception ";
        CollectionAssert.AreEqual(new[] { "截图失败" }, Messages(viewModel));

        viewModel.SearchText = "independenttask";
        CollectionAssert.AreEqual(new[] { "独立任务异常退出" }, Messages(viewModel));

        viewModel.SearchText = "未处理";
        CollectionAssert.AreEqual(new[] { "未处理异常" }, Messages(viewModel));

        viewModel.SearchText = "不存在的关键词";
        Assert.AreEqual(0, viewModel.Entries.Count);
        Assert.IsTrue(viewModel.IsEmpty);
        Assert.AreEqual("没有符合条件的日志", viewModel.EmptyText);
        Assert.AreEqual("显示 0 / 4 条", viewModel.StatusText);
        Assert.AreEqual(1, viewModel.InformationCount);
        Assert.AreEqual(1, viewModel.WarningCount);
        Assert.AreEqual(2, viewModel.ErrorCount);
    }

    [TestMethod]
    public void NewEntriesFollowTheActiveFilter()
    {
        var store = new LogEntryStore();
        var viewModel = new LogViewModel(store);
        viewModel.Activate();
        Assert.AreEqual("本次运行还没有日志", viewModel.EmptyText);
        viewModel.ShowInformation = false;

        store.Add(Now, LogSeverity.Information, "A", "挑战完成 1 次");
        store.Add(Now, LogSeverity.Warning, "A", "挑战失败，原地重试");

        CollectionAssert.AreEqual(new[] { "挑战失败，原地重试" }, Messages(viewModel));
        Assert.AreEqual("显示 1 / 2 条", viewModel.StatusText);
        Assert.AreEqual(1, viewModel.InformationCount);
    }

    [TestMethod]
    public void DropsEntriesTheStoreNoLongerRetains()
    {
        var store = new LogEntryStore(capacity: 2);
        var viewModel = new LogViewModel(store);
        viewModel.Activate();

        store.Add(Now, LogSeverity.Information, "A", "第 1 条");
        store.Add(Now, LogSeverity.Information, "A", "第 2 条");
        store.Add(Now, LogSeverity.Information, "A", "第 3 条");

        CollectionAssert.AreEqual(new[] { "第 2 条", "第 3 条" }, Messages(viewModel));
        Assert.AreEqual("共 2 条，更早的记录在日志文件中", viewModel.StatusText);
    }

    [TestMethod]
    public void StopsFollowingWhileInactiveAndCatchesUpOnActivate()
    {
        var store = new LogEntryStore();
        var viewModel = new LogViewModel(store);
        viewModel.Activate();
        store.Add(Now, LogSeverity.Information, "A", "第 1 条");

        viewModel.Deactivate();
        store.Add(Now, LogSeverity.Information, "A", "第 2 条");
        Assert.AreEqual(0, viewModel.Entries.Count);

        viewModel.Activate();
        CollectionAssert.AreEqual(new[] { "第 1 条", "第 2 条" }, Messages(viewModel));
    }

    [TestMethod]
    public void PostsOneUpdateToTheUiContextForBurstOfEntries()
    {
        var store = new LogEntryStore();
        var context = new QueueingContext();
        var previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(context);
        LogViewModel viewModel;
        try
        {
            viewModel = new LogViewModel(store);
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previous);
        }

        viewModel.Activate();
        store.Add(Now, LogSeverity.Information, "A", "第 1 条");
        store.Add(Now, LogSeverity.Information, "A", "第 2 条");
        store.Add(Now, LogSeverity.Information, "A", "第 3 条");
        Assert.AreEqual(0, viewModel.Entries.Count);
        Assert.AreEqual(1, context.PendingCount);

        context.RunPending();
        Assert.AreEqual(3, viewModel.Entries.Count);

        store.Add(Now, LogSeverity.Information, "A", "第 4 条");
        Assert.AreEqual(1, context.PendingCount);
        context.RunPending();
        CollectionAssert.AreEqual(new[] { "第 1 条", "第 2 条", "第 3 条", "第 4 条" }, Messages(viewModel));

        // 离开页面后不再为新日志占用界面线程。
        viewModel.Deactivate();
        store.Add(Now, LogSeverity.Information, "A", "第 5 条");
        Assert.AreEqual(0, context.PendingCount);
    }

    [TestMethod]
    public void FormatsVisibleEntriesForCopyAndExport()
    {
        var store = new LogEntryStore();
        store.Add(Now, LogSeverity.Information, "RocoPilot.App", "RocoPilot 启动");
        store.Add(Now, LogSeverity.Warning, "RocoPilot.Services.Capture.ScreenCaptureService", "截图失败",
            "System.IO.IOException: 窗口不可用");
        var viewModel = new LogViewModel(store);
        viewModel.Activate();
        viewModel.ShowInformation = false;

        Assert.AreEqual(
            "[2026-11-16 21:04:12.381] [WRN] [RocoPilot.Services.Capture.ScreenCaptureService] 截图失败"
            + Environment.NewLine + "System.IO.IOException: 窗口不可用" + Environment.NewLine,
            viewModel.FormatVisibleEntries());
    }

    [TestMethod]
    public void ItemShowsSingleLineHeadlineAndShortSource()
    {
        var item = new LogEntryItemViewModel(new LogEntry(0, Now, LogSeverity.Fatal,
            "RocoPilot.Services.RuntimeTasks.AutoBattleInputExecutor", "第一行\r\n  第二行", null));

        Assert.AreEqual("21:04:12.381", item.Time);
        Assert.AreEqual("2026-11-16 21:04:12.381", item.FullTime);
        Assert.AreEqual("致命", item.LevelText);
        Assert.AreEqual("AutoBattleInputExecutor", item.Source);
        Assert.AreEqual("第一行 第二行", item.Headline);
        Assert.IsTrue(item.IsError);
        Assert.IsFalse(item.HasException);
        Assert.AreEqual(string.Empty, new LogEntryItemViewModel(
            new LogEntry(1, Now, LogSeverity.Information, string.Empty, "无来源", null)).Source);

        var changed = new List<string?>();
        item.PropertyChanged += (_, args) => changed.Add(args.PropertyName);
        item.IsExpanded = true;

        Assert.IsFalse(item.IsCollapsed);
        CollectionAssert.Contains(changed, nameof(item.IsCollapsed));
    }

    private static string[] Messages(LogViewModel viewModel) =>
        viewModel.Entries.Select(item => item.Entry.Message).ToArray();

    private sealed class QueueingContext : SynchronizationContext
    {
        private readonly Queue<(SendOrPostCallback Callback, object? State)> _pending = new();

        public int PendingCount => _pending.Count;

        public override void Post(SendOrPostCallback d, object? state) => _pending.Enqueue((d, state));

        public void RunPending()
        {
            while (_pending.TryDequeue(out var item))
            {
                item.Callback(item.State);
            }
        }
    }
}
