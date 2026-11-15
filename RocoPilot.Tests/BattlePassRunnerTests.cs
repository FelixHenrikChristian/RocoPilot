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
    [DataRow(2048, true, true, 4)]
    [DataRow(1280, false, true, 4)]
    [DataRow(1280, false, true, 3)]
    [DataRow(960, true, true, 4)]
    [DataRow(960, false, false, 4)]
    public async Task ClaimsAndReturnsThroughSeasonMenu(int width, bool levelUp, bool enabled, int completed)
    {
        var game = new Replay(width, levelUp, enabled, completed);
        using var backend = new RocoPilot.Services.TextRecognition.Backends.OnnxOcrV5SingleLineTextRecognitionBackend();
        var ocr = new RocoPilot.Services.TextRecognition.TextRecognitionService([new RocoPilot.Services.TextRecognition.Backends.OnnxOcrV5TextRecognitionBackend(backend)]);
        var runner = new BattlePassRunner(game, new ImageMatchingService(), game, game, ocr);
        var window = new CaptureTargetWindow { Hwnd = 1, ClientWidth = width, ClientHeight = width * 9 / 16,
            ClientOffsetX = 12, ClientOffsetY = 30 };
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var result = await runner.RunAsync(new(window, new(), new(), DateTimeOffset.Now), _ => { }, timeout.Token);
        Assert.AreEqual("done", game.Scene);
        Assert.AreEqual($"通行证任务领取了{completed}/4", result);
    }

    private sealed class Replay(int width, bool levelUp, bool enabled, int completed) : IScreenCaptureService, IMouseInputService, IKeyboardInputService
    {
        public string Scene = "world";
        private int pendingFrames;

        public CapturedFrame Capture(CaptureTargetWindow window, CaptureMethod method)
        {
            if (Scene is "world" or "done")
                return FlowerSeedScreenRecognizerTests.LoadFixture("legendary-world.png", width, width * 9 / 16, 12, 30);

            if (Scene == "pending" && ++pendingFrames >= 3) Scene = "reward";
            var file = Scene switch
            {
                "menu" or "exit" => "season-menu",
                "reward" => "reward-popup",
                "level" => "level-popup",
                "tasks" or "pending" when enabled => completed == 3 ? "tasks-three-ready" : "tasks-four-ready",
                _ => "tasks-claimed"
            };
            return FlowerSeedScreenRecognizerTests.LoadFixture(Path.Combine(BattlePassTests.Folder(), file + ".png"), width, width * 9 / 16, 12, 30, image =>
            {
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
                "tasks" when !enabled => "exit",
                "claimed" => "exit",
                "exit" => "done",
                _ => throw new AssertFailedException($"{Scene} 意外按下 Esc")
            };
            return Task.CompletedTask;
        }

        public Task ClickAsync(nint hwnd, int x, int y, KeyboardInputMethod method, CancellationToken token)
        {
            var (next, px, py) = Scene switch
            {
                "menu" => ("tasks", 220, 479),
                "tasks" when enabled => ("pending", 1535, 1083),
                _ => throw new AssertFailedException($"{Scene} 意外点击")
            };
            Assert.AreEqual(px, x * 2048d / width, 15, Scene);
            Assert.AreEqual(py, y * 2048d / width, 15, Scene);
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
