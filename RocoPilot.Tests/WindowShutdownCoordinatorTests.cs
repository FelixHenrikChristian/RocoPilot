using Microsoft.VisualStudio.TestTools.UnitTesting;

using RocoPilot.Helpers;

namespace RocoPilot.Tests;

[TestClass]
public sealed class WindowShutdownCoordinatorTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(5);

    [TestMethod]
    public void DefersCloseWhenShutdownCompletesSynchronously()
    {
        var queuedCallbacks = new Queue<Action>();
        var reportedErrors = new List<Exception>();
        var shutdownCallCount = 0;
        var closeCallCount = 0;
        var finalCloseRequestWasAllowed = false;
        WindowShutdownCoordinator? coordinator = null;
        coordinator = new WindowShutdownCoordinator(
            () =>
            {
                shutdownCallCount++;
                return Task.CompletedTask;
            },
            callback =>
            {
                queuedCallbacks.Enqueue(callback);
                return true;
            },
            () =>
            {
                closeCallCount++;
                finalCloseRequestWasAllowed = !coordinator!.HandleCloseRequest();
            },
            reportedErrors.Add);

        Assert.IsTrue(coordinator.HandleCloseRequest(), "第一次关闭请求应被取消。");
        Assert.AreEqual(1, shutdownCallCount, "应立即启动一次清理。");
        Assert.AreEqual(1, queuedCallbacks.Count, "应排队一次最终关闭。");
        Assert.AreEqual(0, closeCallCount, "最终关闭不能在事件处理程序内同步执行。");
        Assert.AreEqual(0, reportedErrors.Count, "成功路径不应报告异常。");

        queuedCallbacks.Dequeue().Invoke();

        Assert.AreEqual(1, closeCallCount, "队列回调应执行一次最终关闭。");
        Assert.IsTrue(finalCloseRequestWasAllowed, "最终关闭触发的请求应被允许。");
    }

    [TestMethod]
    public async Task CoalescesRepeatedCloseRequestsWhileShutdownIsPending()
    {
        var shutdownCompletion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var callbackQueued = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var queuedCallbacks = new Queue<Action>();
        var shutdownCallCount = 0;
        var closeCallCount = 0;
        var coordinator = new WindowShutdownCoordinator(
            () =>
            {
                shutdownCallCount++;
                return shutdownCompletion.Task;
            },
            callback =>
            {
                queuedCallbacks.Enqueue(callback);
                callbackQueued.TrySetResult();
                return true;
            },
            () => closeCallCount++,
            _ => { });

        Assert.IsTrue(coordinator.HandleCloseRequest(), "第一次关闭请求应被取消。");
        Assert.IsTrue(coordinator.HandleCloseRequest(), "清理期间的重复关闭请求应被取消。");
        Assert.AreEqual(1, shutdownCallCount, "重复关闭不能重复启动清理。");
        Assert.AreEqual(0, queuedCallbacks.Count, "清理完成前不能排队最终关闭。");

        shutdownCompletion.SetResult();
        await callbackQueued.Task.WaitAsync(TestTimeout);

        Assert.AreEqual(1, queuedCallbacks.Count, "清理完成后只应排队一次最终关闭。");
        Assert.AreEqual(0, closeCallCount, "排队回调执行前不能关闭窗口。");
        queuedCallbacks.Dequeue().Invoke();
        Assert.AreEqual(1, closeCallCount, "最终关闭只能执行一次。");
    }

    [TestMethod]
    public void QueuesFinalCloseWhenShutdownFails()
    {
        var queuedCallbacks = new Queue<Action>();
        var reportedErrors = new List<Exception>();
        var closeCallCount = 0;
        var coordinator = new WindowShutdownCoordinator(
            () => Task.FromException(new InvalidOperationException("shutdown failed")),
            callback =>
            {
                queuedCallbacks.Enqueue(callback);
                return true;
            },
            () => closeCallCount++,
            reportedErrors.Add);

        Assert.IsTrue(coordinator.HandleCloseRequest(), "清理失败时第一次关闭仍应被取消。");
        Assert.AreEqual(1, reportedErrors.Count, "清理异常应报告一次。");
        Assert.AreEqual(1, queuedCallbacks.Count, "清理异常后仍应排队最终关闭。");
        Assert.AreEqual(0, closeCallCount, "清理异常不能导致同步关闭。");

        queuedCallbacks.Dequeue().Invoke();
        Assert.AreEqual(1, closeCallCount, "排队回调仍应完成关闭。");
    }

    [TestMethod]
    public void DoesNotCloseInlineWhenQueueRejectsCallback()
    {
        var reportedErrors = new List<Exception>();
        var closeCallCount = 0;
        var coordinator = new WindowShutdownCoordinator(
            () => Task.CompletedTask,
            _ => false,
            () => closeCallCount++,
            reportedErrors.Add);

        Assert.IsTrue(coordinator.HandleCloseRequest(), "第一次关闭请求应被取消。");
        Assert.AreEqual(0, closeCallCount, "队列拒绝回调时不能退回同步关闭。");
        Assert.AreEqual(1, reportedErrors.Count, "队列拒绝应报告一次异常。");
        Assert.IsInstanceOfType<InvalidOperationException>(reportedErrors[0]);
    }
}
