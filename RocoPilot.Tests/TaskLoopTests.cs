using Microsoft.VisualStudio.TestTools.UnitTesting;
using RocoPilot.Contracts.Services;
using RocoPilot.Contracts.Services.Capture;
using RocoPilot.Models.Capture;
using RocoPilot.Models.Input;
using RocoPilot.Models.Runtime;
using RocoPilot.Services.IndependentTasks.Engine;

namespace RocoPilot.Tests;

/// <summary>任务循环的兜底行为：漏点补发、画面慢不重复输入、超时、失焦暂停、输入发不出去。</summary>
[TestClass]
public sealed class TaskLoopTests
{
    private enum Step { Open, Opened }

    [TestMethod]
    public async Task ClickThatHadNoEffectIsSentAgain()
    {
        var game = new Game { IgnoredClicks = 1 };
        Assert.AreEqual("done", await RunAsync(game));
        Assert.AreEqual(2, game.Clicks);
    }

    [TestMethod]
    public async Task SlowScreenIsWaitedForWithoutRepeatingTheInput()
    {
        // 点击生效了，但下一个界面 4 秒后才出来：原目标已经不在，不能再点。
        var game = new Game { TransitionMs = 4000 };
        Assert.AreEqual("done", await RunAsync(game));
        Assert.AreEqual(1, game.Clicks);
    }

    [TestMethod]
    public async Task StepTimeoutReportsAStallAndKeepsTheCommittedOutcome()
    {
        var game = new Game { IgnoredClicks = int.MaxValue };
        var error = await Assert.ThrowsExactlyAsync<TaskStalledException>(
            () => RunAsync(game, TimeSpan.FromSeconds(1), committed: "成功：已出售"));
        StringAssert.Contains(error.Message, "等待超时");
        Assert.AreEqual("成功：已出售", error.CommittedOutcome);
    }

    [TestMethod]
    public async Task LosingForegroundPausesTheTimeoutAndSendsNothing()
    {
        var game = new Game { Foreground = false };
        List<string> progress = [];
        var run = RunAsync(game, TimeSpan.FromSeconds(1), progress: progress);
        await Task.Delay(2000);
        Assert.AreEqual(0, game.Clicks);
        game.Foreground = true;
        Assert.AreEqual("done", await run);
        CollectionAssert.Contains(progress, "已暂停，等待切回游戏");
    }

    [TestMethod]
    public async Task InputThatCouldNotBeSentIsRetriedOnTheNextFrame()
    {
        var game = new Game { FailingClicks = 2 };
        Assert.AreEqual("done", await RunAsync(game));
        Assert.AreEqual(1, game.Clicks);
    }

    private static async Task<string> RunAsync(Game game, TimeSpan? timeout = null, string? committed = null,
        List<string>? progress = null)
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var state = new RuntimeTaskState(new CaptureTargetWindow { Hwnd = 1, ClientWidth = 4, ClientHeight = 4 },
            new(), new(), DateTimeOffset.Now);
        var loop = new TaskLoop<Step>("测试", state, game, game, game, update => progress?.Add(update.Operation),
            step => step.ToString(), Step.Open, cancellation.Token)
        {
            Timeout = _ => timeout ?? TimeSpan.FromSeconds(20),
            CommittedOutcome = committed
        };
        while (true)
        {
            using var frame = await loop.NextFrameAsync();
            if (frame is null) continue;
            var screen = game.Screen;
            var result = await loop.RunStepAsync(async () =>
            {
                switch (loop.Step)
                {
                    case Step.Open:
                        if (screen == "button" && await loop.ClickAsync(1, 1)) loop.Advance(Step.Opened);
                        break;
                    case Step.Opened:
                        if (screen == "opened") return "done";
                        break;
                }

                return null;
            });
            if (result is not null) return result;
        }
    }

    private sealed class Game : IScreenCaptureService, IKeyboardInputService, IMouseInputService
    {
        // 静止界面：画面不变时始终返回同一帧（同一个抓帧时间）。
        private readonly CapturedFrame _frame = new(4, 4, new byte[64]);
        private DateTimeOffset _openAt = DateTimeOffset.MaxValue;
        public int IgnoredClicks, FailingClicks, TransitionMs, Clicks;
        public volatile bool Foreground = true;

        public string Screen => _openAt == DateTimeOffset.MaxValue ? "button"
            : DateTimeOffset.UtcNow >= _openAt ? "opened" : "transition";

        public CapturedFrame Capture(CaptureTargetWindow window, CaptureMethod method) => _frame.AddReference();

        public Task ClickAsync(nint hwnd, int x, int y, KeyboardInputMethod method, CancellationToken token)
        {
            if (FailingClicks-- > 0) throw new InvalidOperationException("鼠标目标位置被其他窗口遮挡，请切回游戏后重试。");
            Assert.AreEqual("button", Screen, "目标已经不在时不应再点击");
            Clicks++;
            if (IgnoredClicks-- <= 0) _openAt = DateTimeOffset.UtcNow.AddMilliseconds(TransitionMs);
            return Task.CompletedTask;
        }

        public bool IsWindowAvailable(nint hwnd) => true;
        public bool IsWindowForeground(nint hwnd) => Foreground;
        public bool RequiresForeground(KeyboardInputMethod method) => true;
        public void EnsureReady(KeyboardInputMethod method) { }
        public void Release(CaptureTargetWindow window, CaptureMethod method) { }
        public bool TryParseSequence(string sequence, out IReadOnlyList<KeyStroke> keys, out string error) => throw new NotSupportedException();
        public Task SendSequenceAsync(nint hwnd, string key, KeyboardInputOptions? options, CancellationToken token) => throw new NotSupportedException();
        public Task SendSequenceAsync(nint hwnd, IReadOnlyList<KeyStroke> keys, KeyboardInputOptions? options, CancellationToken token) => throw new NotSupportedException();
        public void LookDown(nint hwnd, KeyboardInputMethod method) => throw new NotSupportedException();
        public Task PressAtCurrentPositionAsync(nint hwnd, int holdMs, KeyboardInputMethod method, CancellationToken token) => throw new NotSupportedException();
        public Task DragAsync(nint hwnd, int x, int y, int endX, int endY, KeyboardInputMethod method, CancellationToken token) => throw new NotSupportedException();
        public Task ScrollAsync(nint hwnd, int x, int y, int delta, KeyboardInputMethod method, CancellationToken token) => throw new NotSupportedException();
        public Task ScrollAtCurrentPositionAsync(nint hwnd, int delta, KeyboardInputMethod method, CancellationToken token) => throw new NotSupportedException();
    }
}
