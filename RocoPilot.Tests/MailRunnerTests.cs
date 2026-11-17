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
public sealed class MailRunnerTests
{
    [TestMethod]
    [DataRow(2048, true, true, false)]
    [DataRow(1280, false, true, true)]
    [DataRow(960, true, false, true)]
    [DataRow(960, false, false, false)]
    public async Task ChecksMailboxThenReturnsToWorld(int width, bool badge, bool enabled, bool ignoreFirstInput)
    {
        using var game = new Replay(width, badge, enabled, ignoreFirstInput);
        var runner = new MailRunner(game, new ImageMatchingService(), game, game);
        var window = new CaptureTargetWindow { Hwnd = 1, ClientWidth = width, ClientHeight = width * 9 / 16,
            ClientOffsetX = 12, ClientOffsetY = 30 };
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        var result = await runner.RunAsync(new(window, new(), new(), DateTimeOffset.Now), _ => { }, timeout.Token);
        Assert.AreEqual("done", game.Scene);
        StringAssert.Contains(result, enabled ? "已领取" : "没有可领取");
    }

    private sealed class Replay(int width, bool badge, bool enabled, bool ignoreFirstInput) : IScreenCaptureService, IMouseInputService, IKeyboardInputService, IDisposable
    {
        public string Scene = "world";
        private readonly HashSet<string> _ignoredInputs = [];
        private string? _frameScene;
        private CapturedFrame? _frame;

        public CapturedFrame Capture(CaptureTargetWindow window, CaptureMethod method)
        {
            // 模拟 WGC 在界面不变化时返回同一帧，包括相同的抓帧时间。
            if (_frameScene == Scene) return _frame!.AddReference();
            var frame = LoadFrame();
            _frame?.Dispose();
            _frameScene = Scene;
            _frame = frame;
            return _frame.AddReference();
        }

        private CapturedFrame LoadFrame()
        {
            if (Scene is "world" or "done")
                return FlowerSeedScreenRecognizerTests.LoadFixture("legendary-world.png", width, width * 9 / 16, 12, 30);
            if (Scene == "reward") return CapturedFrame.RentBgra32(width, width * 9 / 16);
            var menu = Scene is "menu" or "exit";
            var file = menu ? "menu-mail-badge" : (Scene == "mailbox" && enabled ? "mail-claim-enabled" : "mail-claim-disabled");
            var path = Path.Combine(SourceDirectory(), "LocalScreenshots", "2026-11-13", "mail", file + ".png");
            return FlowerSeedScreenRecognizerTests.LoadFixture(path, width, width * 9 / 16, 12, 30, image =>
            {
                if (menu && !badge)
                    OpenCvSharp.Cv2.Rectangle(image, new OpenCvSharp.Rect(201, 526, 30, 27), OpenCvSharp.Scalar.Black, -1);
            });
        }

        public Task SendSequenceAsync(nint hwnd, string key, KeyboardInputOptions? options, CancellationToken token)
        {
            Assert.AreEqual("Esc", key);
            var next = Scene switch
            {
                "world" => "menu",
                "reward" => "claimed",
                "mailbox" when !enabled => "exit",
                "claimed" => "exit",
                "exit" => "done",
                _ => throw new AssertFailedException($"{Scene} 意外按下 Esc")
            };
            if (!ignoreFirstInput || !_ignoredInputs.Add(Scene)) Scene = next;
            return Task.CompletedTask;
        }

        public Task ClickAsync(nint hwnd, int x, int y, KeyboardInputMethod method, CancellationToken token)
        {
            var (next, px, py) = Scene switch
            {
                "menu" => ("mailbox", 178, 580),
                "mailbox" when enabled => ("reward", 869, 1062),
                _ => throw new AssertFailedException($"{Scene} 意外点击")
            };
            Assert.AreEqual(px, x * 2048d / width, 15, Scene);
            Assert.AreEqual(py, y * 2048d / width, 15, Scene);
            if (!ignoreFirstInput || !_ignoredInputs.Add(Scene)) Scene = next;
            return Task.CompletedTask;
        }

        private static string SourceDirectory([System.Runtime.CompilerServices.CallerFilePath] string path = "") => Path.GetDirectoryName(path)!;
        public void Dispose() => _frame?.Dispose();
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
