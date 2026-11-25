using Microsoft.VisualStudio.TestTools.UnitTesting;
using RocoPilot.Contracts.Services;
using RocoPilot.Contracts.Services.Capture;
using RocoPilot.Models.Capture;
using RocoPilot.Models.Input;
using RocoPilot.Models.Runtime;
using RocoPilot.Services.ImageMatching;
using RocoPilot.Services.IndependentTasks;

namespace RocoPilot.Tests;

[TestClass]
[TestCategory("LocalScreenshots")]
public sealed class BattlePassRunnerTests
{
    [TestMethod]
    [DataRow(2048, true, true, 4, false, true)]
    [DataRow(1280, false, true, 4, true, true)]
    [DataRow(1280, false, true, 3, false, true)]
    [DataRow(960, true, true, 4, false, true)]
    [DataRow(960, false, false, 4, true, false)]
    [DataRow(1280, false, false, 4, false, false)]
    [DataRow(2048, false, false, 4, true, true)]
    [DataRow(2048, false, false, 2, false, true)]
    [DataRow(960, false, false, 2, false, false)]
    [DataRow(1280, false, true, 1, false, false)]
    public async Task ClaimsAndReturnsThroughSeasonMenu(int width, bool levelUp, bool enabled, int completed, bool initialRewards, bool rewardsEnabled)
    {
        var game = new Replay(width, levelUp, enabled, completed, initialRewards, rewardsEnabled);
        using var backend = new RocoPilot.Services.TextRecognition.Backends.OnnxOcrV5SingleLineTextRecognitionBackend();
        var ocr = new RocoPilot.Services.TextRecognition.TextRecognitionService([new RocoPilot.Services.TextRecognition.Backends.OnnxOcrV5TextRecognitionBackend(backend)]);
        using var locator = new RocoPilot.Services.TextRecognition.OnnxTextLocator(ocr);
        var runner = new BattlePassRunner(game, new ImageMatchingService(), game, game, locator);
        var window = new CaptureTargetWindow { Hwnd = 1, ClientWidth = width, ClientHeight = width * 9 / 16,
            ClientOffsetX = 12, ClientOffsetY = 30 };
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var result = await runner.RunAsync(new(window, new(), new(), DateTimeOffset.Now), _ => { }, timeout.Token);
        Assert.AreEqual("done", game.Scene);
        Assert.AreEqual(1, game.GiftClicks);
        Assert.AreEqual(initialRewards ? 1 : 0, game.TaskClicks);
        Assert.AreEqual(rewardsEnabled ? 1 : 0, game.LevelClaims);
        Assert.AreEqual($"通行证任务领取了{(enabled ? completed : 0)}/4", result);
    }

    private sealed class Replay(int width, bool levelUp, bool enabled, int completed, bool initialRewards, bool rewardsEnabled) : IScreenCaptureService, IMouseInputService, IKeyboardInputService
    {
        public string Scene = "world";
        private readonly System.Diagnostics.Stopwatch taskCardsAppearing = new();
        private int pendingFrames;
        private int rewardPendingFrames;
        private int rewardEscapes;
        public int GiftClicks, TaskClicks, LevelClaims;

        public CapturedFrame Capture(CaptureTargetWindow window, CaptureMethod method)
        {
            if (Scene is "world" or "done")
                return FlowerSeedScreenRecognizerTests.LoadFixture("legendary-world.png", width, width * 9 / 16, 12, 30);

            if (Scene == "pending" && ++pendingFrames >= 3) Scene = "reward";
            if (Scene == "reward-pending" && ++rewardPendingFrames >= 3) Scene = "reward-items";
            if (Scene == "tasks" && !taskCardsAppearing.IsRunning) taskCardsAppearing.Start();
            var file = Scene switch
            {
                "menu" or "exit" => "season-menu",
                "reward" => "reward-popup",
                "level" => "level-popup",
                "initial-rewards" or "rewards" or "reward-pending" or "rewards-claimed" => "level-rewards-window-client",
                "reward-items" => "level-rewards-popup-window-client",
                // 入场动画前 0.6 秒已经能点全部领取，但只能认出前两张卡。
                "tasks" when completed == 3 && taskCardsAppearing.ElapsedMilliseconds < 600 => "../../2026-11-25/battle-pass/tasks-two-ready",
                "tasks" when completed == 2 => "../../2026-11-24/battle-pass/tasks-two-complete-no-claim",
                "tasks" or "pending" when enabled => completed == 3 ? "tasks-three-ready" : "tasks-four-ready",
                _ => "tasks-claimed"
            };
            return FlowerSeedScreenRecognizerTests.LoadFixture(Path.Combine(BattlePassTests.Folder(), file + ".png"), width, width * 9 / 16, 12, 30, image =>
            {
                // 合成只剩第二张卡可领、没有“全部领取”的任务页。
                if (file == "tasks-four-ready" && completed == 1)
                {
                    using var claimed = OpenCvSharp.Cv2.ImRead(Path.Combine(BattlePassTests.Folder(), "tasks-claimed.png"));
                    foreach (var areaToReplace in new[] { new OpenCvSharp.Rect(640, 335, 321, 540),
                        new OpenCvSharp.Rect(1284, 335, 640, 540), new OpenCvSharp.Rect(1408, 1040, 250, 83) })
                    {
                        using var fromClaimed = new OpenCvSharp.Mat(claimed, areaToReplace);
                        using var target = new OpenCvSharp.Mat(image, areaToReplace);
                        fromClaimed.CopyTo(target);
                    }
                }
                if (file == "level-rewards-window-client" && (!rewardsEnabled || Scene == "rewards-claimed"))
                    OpenCvSharp.Cv2.Rectangle(image, new OpenCvSharp.Rect(1410, 1040, 245, 83), new OpenCvSharp.Scalar(150, 130, 110), -1);
                // 缺少部分领取后的实拍：合成最后一项仍为 0/5 的对照，不覆盖原图。
                if (file != "tasks-claimed" || completed != 3) return;
                using var source = OpenCvSharp.Cv2.ImRead(Path.Combine(BattlePassTests.Folder(), "tasks-three-ready.png"));
                var area = new OpenCvSharp.Rect(1604, 335, 316, 540);
                using var from = new OpenCvSharp.Mat(source, area);
                using var to = new OpenCvSharp.Mat(image, area);
                from.CopyTo(to);
            });
        }

        public Task SendSequenceAsync(nint hwnd, string key, KeyboardInputOptions? options, CancellationToken token)
        {
            Assert.AreEqual(Scene == "world" ? "F1" : "Esc", key);
            Scene = Scene switch
            {
                "world" => "menu",
                "reward" => levelUp ? "level" : "claimed",
                "level" => "claimed",
                "reward-items" => ++rewardEscapes < 2 ? "reward-items" : "rewards-claimed",
                "rewards" when !rewardsEnabled => "exit",
                "rewards-claimed" => "exit",
                "exit" => "done",
                _ => throw new AssertFailedException($"{Scene} 意外按下 Esc")
            };
            return Task.CompletedTask;
        }

        public Task ClickAsync(nint hwnd, int x, int y, KeyboardInputMethod method, CancellationToken token)
        {
            var (next, px, py) = Scene switch
            {
                "menu" => (initialRewards ? "initial-rewards" : "tasks", 220, 479),
                "initial-rewards" => ("tasks", 166, 304),
                "claimed" => ("rewards", 166, 192),
                "tasks" when !enabled => ("rewards", 166, 192),
                "rewards" when rewardsEnabled => ("reward-pending", 1535, 1083),
                "tasks" when enabled && completed == 1 => ("pending", 1125, 828),
                "tasks" when enabled => ("pending", 1535, 1083),
                _ => throw new AssertFailedException($"{Scene} 意外点击")
            };
            Assert.AreEqual(px, x * 2048d / width, 15, Scene);
            Assert.AreEqual(py, y * 2048d / width, 15, Scene);
            if (Scene == "initial-rewards") TaskClicks++;
            if (next == "rewards") GiftClicks++;
            if (next == "reward-pending") LevelClaims++;
            Scene = next;
            return Task.CompletedTask;
        }

        public bool IsWindowAvailable(nint hwnd) => true;
        public bool IsWindowForeground(nint hwnd) => true;
        public bool RequiresForeground(KeyboardInputMethod method) => true;
        public void EnsureReady(KeyboardInputMethod method) { }
        public void Release(CaptureTargetWindow window, CaptureMethod method) { }
        public void LookDown(nint hwnd, KeyboardInputMethod method) => throw new NotSupportedException();
        public Task PressAtCurrentPositionAsync(nint hwnd, int holdMs, KeyboardInputMethod method, CancellationToken token) => throw new NotSupportedException();
        public Task DragAsync(nint hwnd, int x, int y, int endX, int endY, KeyboardInputMethod method, CancellationToken token) => throw new NotSupportedException();
        public Task ScrollAsync(nint hwnd, int x, int y, int delta, KeyboardInputMethod method, CancellationToken token) => throw new NotSupportedException();
        public Task ScrollAtCurrentPositionAsync(nint hwnd, int delta, KeyboardInputMethod method, CancellationToken token) => throw new NotSupportedException();
        public bool TryParseSequence(string sequence, out IReadOnlyList<KeyStroke> keys, out string error) => throw new NotSupportedException();
        public Task SendSequenceAsync(nint hwnd, IReadOnlyList<KeyStroke> keys, KeyboardInputOptions? options, CancellationToken token) => throw new NotSupportedException();
    }
}
