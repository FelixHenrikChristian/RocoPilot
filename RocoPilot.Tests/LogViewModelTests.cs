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
