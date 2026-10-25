using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using RocoPilot.Services.Logging;
using RocoPilot.ViewModels;
using Serilog.Events;
using Serilog.Parsing;

namespace RocoPilot.Tests;

[TestClass]
public sealed class LogViewModelTests
{
    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public void FilterRefreshAndIncomingLogDisplayEachEntryOnce(bool filterFirst)
    {
        var fixture = new LogFixture();
        fixture.ViewModel.Attach(fixture.Pending.Enqueue);
        if (filterFirst) fixture.ViewModel.ShowDebug = true;
        Emit(fixture.Buffer, "new");
        if (!filterFirst) fixture.ViewModel.ShowDebug = true;
        fixture.Drain();

        Assert.HasCount(1, fixture.ViewModel.Entries);
        Assert.AreSame(fixture.Buffer.Snapshot().Single(), fixture.ViewModel.Entries.Single());
        fixture.ViewModel.Detach();
    }

    [TestMethod]
    public void ReenteringPageDoesNotAppendPreviouslyQueuedLogsAgain()
    {
        var fixture = new LogFixture();
        fixture.ViewModel.Attach(fixture.Pending.Enqueue);
        Emit(fixture.Buffer, "queued");
        fixture.ViewModel.Detach();
        fixture.ViewModel.Attach(fixture.Pending.Enqueue);
        fixture.Drain();

        Assert.HasCount(1, fixture.ViewModel.Entries);
        fixture.ViewModel.Detach();
    }

    [TestMethod]
    public void LogBurstSharesOneRefreshAndPreservesExistingRows()
    {
        var fixture = new LogFixture();
        Emit(fixture.Buffer, "existing");
        fixture.ViewModel.Attach(fixture.Pending.Enqueue);
        var changes = new List<System.Collections.Specialized.NotifyCollectionChangedAction>();
        fixture.ViewModel.Entries.CollectionChanged += (_, e) => changes.Add(e.Action);
        Emit(fixture.Buffer, "first");
        Emit(fixture.Buffer, "second");

        Assert.HasCount(1, fixture.Pending);
        fixture.Drain();
        CollectionAssert.AreEqual(new[] { "existing", "first", "second" },
            fixture.ViewModel.Entries.Select(entry => entry.Message).ToArray());
        Assert.IsTrue(changes.All(action => action == System.Collections.Specialized.NotifyCollectionChangedAction.Add));
        fixture.ViewModel.Detach();
    }

    [TestMethod]
    public void ClearRemovesVisibleAndFilteredLogsFromBuffer()
    {
        var fixture = new LogFixture();
        Emit(fixture.Buffer, "visible");
        Emit(fixture.Buffer, "hidden", LogEventLevel.Debug);
        fixture.ViewModel.Attach(fixture.Pending.Enqueue);
        Assert.AreEqual(1, fixture.ViewModel.Entries.Count);

        fixture.ViewModel.ClearCommand.Execute(null);
        fixture.ViewModel.ShowDebug = true;
        fixture.ViewModel.SearchText = "hidden";
        fixture.Drain();
        fixture.ViewModel.SearchText = string.Empty;
        fixture.Drain();

        Assert.AreEqual(0, fixture.Buffer.Snapshot().Count);
        Assert.AreEqual(0, fixture.ViewModel.Entries.Count);
        fixture.ViewModel.Detach();
    }

    [TestMethod]
    public void RefreshDropsEvictedEntriesAndAppliesLatestFilter()
    {
        var buffer = new InMemoryLogSink(3);
        var queue = new Queue<Action>();
        var viewModel = new LogViewModel(NullLogger<LogViewModel>.Instance, buffer);
        Emit(buffer, "old");
        Emit(buffer, "keep");
        viewModel.Attach(queue.Enqueue);
        Emit(buffer, "debug", LogEventLevel.Debug);
        Emit(buffer, "last");
        while (queue.TryDequeue(out var refresh)) refresh();
        CollectionAssert.AreEqual(new[] { "keep", "last" }, viewModel.Entries.Select(e => e.Message).ToArray());

        viewModel.ShowDebug = true;
        viewModel.SearchText = "debug";
        while (queue.TryDequeue(out var refresh)) refresh();
        Assert.AreEqual("debug", viewModel.Entries.Single().Message);
        viewModel.SearchText = string.Empty;
        while (queue.TryDequeue(out var refresh)) refresh();
        CollectionAssert.AreEqual(new[] { "keep", "debug", "last" }, viewModel.Entries.Select(e => e.Message).ToArray());
        viewModel.Detach();
    }

    [TestMethod]
    public void ClearedLogsDoNotReturnAfterReenteringPage()
    {
        var fixture = new LogFixture();
        Emit(fixture.Buffer, "old");
        fixture.ViewModel.Attach(fixture.Pending.Enqueue);
        fixture.ViewModel.ClearCommand.Execute(null);
        fixture.ViewModel.Detach();
        fixture.ViewModel.Attach(fixture.Pending.Enqueue);

        Assert.AreEqual(0, fixture.ViewModel.Entries.Count);
        fixture.ViewModel.Detach();
    }

    [TestMethod]
    public void ClearDiscardsOldQueuedNotificationsButAllowsNewLogs()
    {
        var fixture = new LogFixture();
        fixture.ViewModel.Attach(fixture.Pending.Enqueue);
        Emit(fixture.Buffer, "old queued");
        fixture.ViewModel.ClearCommand.Execute(null);
        Emit(fixture.Buffer, "new");
        fixture.Drain();

        Assert.AreEqual(1, fixture.ViewModel.Entries.Count);
        Assert.AreEqual("new", fixture.ViewModel.Entries[0].Message);
        Assert.AreEqual("new", fixture.Buffer.Snapshot().Single().Message);
        fixture.ViewModel.Detach();
    }

    [TestMethod]
    public async Task ClearDiscardsNotificationsDeliveredAfterClearing()
    {
        var fixture = new LogFixture();
        using var arrived = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        fixture.Buffer.EntryWritten += _ =>
        {
            arrived.Set();
            if (!release.Wait(TimeSpan.FromSeconds(10)))
            {
                throw new TimeoutException("等待清空操作超时");
            }
        };
        fixture.ViewModel.Attach(fixture.Pending.Enqueue);
        var emitting = Task.Run(() => Emit(fixture.Buffer, "delayed old"));
        try
        {
            Assert.IsTrue(arrived.Wait(TimeSpan.FromSeconds(5)));
            fixture.ViewModel.ClearCommand.Execute(null);
        }
        finally
        {
            release.Set();
            await emitting;
            fixture.ViewModel.Detach();
        }
        fixture.Drain();

        Assert.AreEqual(0, fixture.ViewModel.Entries.Count);
        Assert.AreEqual(0, fixture.Buffer.Snapshot().Count);
    }

    [TestMethod]
    public void PendingFilterRefreshReadsClearedBuffer()
    {
        var fixture = new LogFixture();
        Emit(fixture.Buffer, "old");
        fixture.ViewModel.Attach(fixture.Pending.Enqueue);
        fixture.ViewModel.ShowInformation = false;
        fixture.ViewModel.ShowInformation = true;
        fixture.ViewModel.ClearCommand.Execute(null);
        fixture.Drain();

        Assert.AreEqual(0, fixture.ViewModel.Entries.Count);
        fixture.ViewModel.Detach();
    }

    private static void Emit(InMemoryLogSink buffer, string message,
        LogEventLevel level = LogEventLevel.Information)
        => buffer.Emit(new LogEvent(DateTimeOffset.Now, level, null,
            new MessageTemplateParser().Parse(message), []));

    private sealed class LogFixture
    {
        public InMemoryLogSink Buffer { get; } = new();
        public Queue<Action> Pending { get; } = new();
        public LogViewModel ViewModel { get; }

        public LogFixture()
            => ViewModel = new LogViewModel(NullLogger<LogViewModel>.Instance, Buffer);

        public void Drain()
        {
            while (Pending.TryDequeue(out var callback))
            {
                callback();
            }
        }
    }
}
