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
    [DataRow(2048, true, true)]
    [DataRow(1280, false, true)]
    [DataRow(960, true, false)]
    [DataRow(960, false, false)]
    public async Task ChecksMailboxThenReturnsToWorld(int width, bool badge, bool enabled)
    {
        var game = new Replay(width, badge, enabled);
        var runner = new MailRunner(game, new ImageMatchingService(), game, game);
        var window = new CaptureTargetWindow { Hwnd = 1, ClientWidth = width, ClientHeight = width * 9 / 16,
            ClientOffsetX = 12, ClientOffsetY = 30 };
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        var result = await runner.RunAsync(new(window, new(), new(), DateTimeOffset.Now), _ => { }, timeout.Token);
        Assert.AreEqual("done", game.Scene);
        StringAssert.Contains(result, enabled ? "已领取" : "没有可领取");
    }

    private sealed class Replay(int width, bool badge, bool enabled) : IScreenCaptureService, IMouseInputService, IKeyboardInputService
    {
        public string Scene = "world";

        public CapturedFrame Capture(CaptureTargetWindow window, CaptureMethod method)
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
            Scene = Scene switch
            {
                "world" => "menu",
                "reward" => "claimed",
                "mailbox" when !enabled => "exit",
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
                "menu" => ("mailbox", 178, 580),
                "mailbox" when enabled => ("reward", 869, 1062),
                _ => throw new AssertFailedException($"{Scene} 意外点击")
            };
            Assert.AreEqual(px, x * 2048d / width, 15, Scene);
            Assert.AreEqual(py, y * 2048d / width, 15, Scene);
            Scene = next;
            return Task.CompletedTask;
        }

        private static string SourceDirectory([System.Runtime.CompilerServices.CallerFilePath] string path = "") => Path.GetDirectoryName(path)!;
        public bool IsWindowAvailable(nint hwnd) => true;
        public bool IsWindowForeground(nint hwnd) => true;
        public bool RequiresForeground(KeyboardInputMethod method) => true;
        public void EnsureReady(KeyboardInputMethod method) { }
        public void Release(CaptureTargetWindow window, CaptureMethod method) { }
        public Task DragAsync(nint hwnd, int x, int y, int endX, int endY, KeyboardInputMethod method, CancellationToken token) => throw new NotSupportedException();
        public Task ScrollAsync(nint hwnd, int x, int y, int delta, KeyboardInputMethod method, CancellationToken token) => throw new NotSupportedException();
        public Task ScrollAtCurrentPositionAsync(nint hwnd, int delta, KeyboardInputMethod method, CancellationToken token) => throw new NotSupportedException();
        public bool TryParseSequence(string sequence, out IReadOnlyList<KeyStroke> keys, out string error) => throw new NotSupportedException();
        public Task SendSequenceAsync(nint hwnd, IReadOnlyList<KeyStroke> keys, KeyboardInputOptions? options, CancellationToken token) => throw new NotSupportedException();
    }
}
