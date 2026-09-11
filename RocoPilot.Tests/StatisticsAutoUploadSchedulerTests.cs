using Microsoft.VisualStudio.TestTools.UnitTesting;
using RocoPilot.Services.Statistics.Sync;
using RocoPilot.Tests.TestDoubles;

namespace RocoPilot.Tests;

[TestClass]
public sealed class StatisticsAutoUploadSchedulerTests
{
    [TestMethod]
    public async Task RepeatedRequestsResetDelayAndProduceOneUpload()
    {
        var delay = new ManualAsyncDelay();
        var uploaded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var count = 0;
        await using var scheduler = Create(delay, _ => { count++; uploaded.TrySetResult(); return Task.CompletedTask; });
        scheduler.Request();
        var first = await delay.NextAsync();
        scheduler.Request();
        var second = await delay.NextAsync();
        scheduler.Request();
        var third = await delay.NextAsync();
        Assert.IsTrue(first.Completion.Task.IsCanceled);
        Assert.IsTrue(second.Completion.Task.IsCanceled);
        Assert.AreEqual(0, count);
        third.Complete();
        await uploaded.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.AreEqual(1, count);
    }

    [TestMethod]
    public async Task ChangesDuringUploadScheduleTrailingUploadWithoutCancellingCurrentOne()
    {
        var delay = new ManualAsyncDelay();
        using var pause = new AsyncPause();
        var secondUpload = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var count = 0;
        var firstToken = CancellationToken.None;
        await using var scheduler = Create(delay, async token =>
        {
            if (++count == 1) { firstToken = token; await pause.PauseAsync(string.Empty); }
            else secondUpload.TrySetResult();
        });
        scheduler.Request();
        (await delay.NextAsync()).Complete();
        await pause.WaitUntilEnteredAsync();
        scheduler.Request();
        scheduler.Request();
        Assert.IsFalse(firstToken.IsCancellationRequested);
        Assert.AreEqual(1, count);
        pause.Dispose();
        (await delay.NextAsync()).Complete();
        await secondUpload.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.AreEqual(2, count);
    }

    [TestMethod]
    public async Task CancelPendingStillAllowsFutureRequests()
    {
        var delay = new ManualAsyncDelay();
        var count = 0;
        var uploaded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var scheduler = Create(delay, _ => { count++; uploaded.TrySetResult(); return Task.CompletedTask; });
        scheduler.Request();
        var pending = await delay.NextAsync();
        scheduler.CancelPending();
        Assert.IsTrue(pending.Completion.Task.IsCanceled);
        Assert.AreEqual(0, count);
        scheduler.Request();
        (await delay.NextAsync()).Complete();
        await uploaded.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.AreEqual(1, count);
    }

    [TestMethod]
    public async Task DisposeCancelsWaitingUploadAndRejectsLaterRequests()
    {
        var delay = new ManualAsyncDelay();
        var count = 0;
        var scheduler = Create(delay, _ => { count++; return Task.CompletedTask; });
        scheduler.Request();
        var waiting = await delay.NextAsync();
        await scheduler.DisposeAsync();
        await scheduler.DisposeAsync();
        scheduler.Request();
        Assert.IsTrue(waiting.Completion.Task.IsCanceled);
        Assert.AreEqual(0, count);
        Assert.IsFalse(delay.HasPending);
    }

    [TestMethod]
    public async Task DisposeWaitsForActiveUploadCleanup()
    {
        var delay = new ManualAsyncDelay();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cleanup = new AsyncPause();
        var scheduler = Create(delay, async token =>
        {
            started.TrySetResult();
            try { await Task.Delay(Timeout.Infinite, token); }
            finally { await cleanup.PauseAsync(string.Empty); }
        });
        scheduler.Request();
        (await delay.NextAsync()).Complete();
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var stopping = scheduler.DisposeAsync().AsTask();
        await cleanup.WaitUntilEnteredAsync();
        Assert.IsFalse(stopping.IsCompleted);
        cleanup.Dispose();
        await stopping.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [TestMethod]
    public async Task FailureIsObservedAndNextRequestCanRetry()
    {
        var delay = new ManualAsyncDelay();
        var failed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var retried = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var count = 0;
        await using var scheduler = new StatisticsAutoUploadScheduler(TimeSpan.FromSeconds(8), _ =>
        {
            if (++count == 1) throw new IOException("upload failed");
            retried.TrySetResult();
            return Task.CompletedTask;
        }, _ => failed.TrySetResult(), delay.DelayAsync);
        scheduler.Request();
        (await delay.NextAsync()).Complete();
        await failed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        scheduler.Request();
        (await delay.NextAsync()).Complete();
        await retried.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    private static StatisticsAutoUploadScheduler Create(ManualAsyncDelay delay, Func<CancellationToken, Task> upload) =>
        new(TimeSpan.FromSeconds(8), upload, ex => throw new AssertFailedException(ex.Message), delay.DelayAsync);
}
