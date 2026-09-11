using Microsoft.VisualStudio.TestTools.UnitTesting;
using RocoPilot.Contracts.Services.Capture;
using RocoPilot.Models.Capture;
using RocoPilot.Models.Recognition;
using RocoPilot.Models.Runtime;
using RocoPilot.Services.RuntimeTasks;

namespace RocoPilot.Tests;

[TestClass]
public sealed class RuntimeSessionTests
{
    [TestMethod]
    public async Task StopsLoopsAndDrainsWorkersBeforeReleasingCaptureExactlyOnce()
    {
        var capture = new CaptureStub();
        var session = CreateSession(capture);
        var workerStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var workerFinish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var loopCancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        session.Start(async (current, token) =>
        {
            _ = current.RunBackground(async () =>
            {
                workerStarted.SetResult();
                await workerFinish.Task;
                return true;
            });
            try { await Task.Delay(Timeout.Infinite, token); }
            finally { loopCancelled.SetResult(); }
        }, (_, token) => Task.Delay(Timeout.Infinite, token));
        await workerStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var stopping = session.DisposeAsync().AsTask();
        await loopCancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.IsFalse(stopping.IsCompleted);
        Assert.AreEqual(0, capture.Releases);
        workerFinish.SetResult();
        await stopping.WaitAsync(TimeSpan.FromSeconds(5));
        await session.DisposeAsync();
        Assert.AreEqual(1, capture.Releases);
    }

    [TestMethod]
    public async Task FrameMailboxRejectsOldBattleAndOwnsItsReference()
    {
        var session = CreateSession(new CaptureStub());
        var frame = CapturedFrame.RentBgra32(2, 2);
        frame.Pixels[0] = 37;
        session.PublishFrame(frame, 1);
        frame.Dispose();
        Assert.IsNull(session.RentFrame(2));
        using var rented = session.RentFrame(1);
        Assert.IsNotNull(rented);
        Assert.AreEqual((byte)37, rented.Pixels[0]);
        session.ClearFrame();
        Assert.IsNull(session.RentFrame(1));
        Assert.AreEqual((byte)37, rented.Pixels[0]);
        await session.DisposeAsync();
    }

    [TestMethod]
    public async Task WorkerFailureStillReleasesCapture()
    {
        var capture = new CaptureStub();
        var session = CreateSession(capture);
        session.Track(Task.FromException(new InvalidOperationException("worker failure")));
        try
        {
            await session.DisposeAsync();
            Assert.Fail("应传播后台任务失败。");
        }
        catch (InvalidOperationException) { }
        Assert.AreEqual(1, capture.Releases);
    }

    [TestMethod]
    public async Task CancellationCallbackFailureStillDrainsWorkersBeforeRelease()
    {
        var capture = new CaptureStub();
        var session = CreateSession(capture);
        using var pause = new RocoPilot.Tests.TestDoubles.AsyncPause();
        using var registration = session.Token.Register(() => throw new InvalidOperationException("cancel failed"));
        session.Track(pause.PauseAsync(string.Empty));
        await pause.WaitUntilEnteredAsync();
        var stopping = session.DisposeAsync().AsTask();
        Assert.IsFalse(stopping.IsCompleted);
        Assert.AreEqual(0, capture.Releases);
        pause.Dispose();
        await Assert.ThrowsExactlyAsync<AggregateException>(() => stopping.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.AreEqual(1, capture.Releases);
    }

    [TestMethod]
    public async Task ReleaseFailureStillDisposesCancellationAndIsNotRetried()
    {
        var capture = new CaptureStub { FailRelease = true };
        var session = CreateSession(capture);
        await Assert.ThrowsExactlyAsync<IOException>(() => session.DisposeAsync().AsTask());
        await Assert.ThrowsExactlyAsync<IOException>(() => session.DisposeAsync().AsTask());
        Assert.AreEqual(1, capture.Releases);
        Assert.ThrowsExactly<ObjectDisposedException>(() => _ = session.Token);
    }

    private static RuntimeSession CreateSession(CaptureStub capture) => new(
        new RuntimeTaskState(new CaptureTargetWindow { Hwnd = 1 }, new RecognitionRegionConfig(), new RuntimeTaskStartOptions(), DateTimeOffset.Now), capture);

    private sealed class CaptureStub : IScreenCaptureService
    {
        public int Releases;
        public bool FailRelease;
        public CapturedFrame? Capture(CaptureTargetWindow window, CaptureMethod method) => null;
        public void Release(CaptureTargetWindow window, CaptureMethod method)
        {
            Releases++;
            if (FailRelease) throw new IOException("release failed");
        }
    }
}
