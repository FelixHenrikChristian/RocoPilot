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
public sealed class EveningRestTests
{
    [TestMethod]
    [DataRow(2048, "collect")]
    [DataRow(1280, "collect")]
    [DataRow(960, "collect")]
    [DataRow(960, "full")]
    [DataRow(1280, "unavailable")]
    public async Task ReturnsToWorldAfterFullOrUnavailable(int width, string scenario)
    {
        var game = new Replay(width, scenario);
        var runner = new EveningRestRunner(game, new ImageMatchingService(), game, game);
        var window = new CaptureTargetWindow { Hwnd = 1, ClientWidth = width, ClientHeight = width * 9 / 16,
            ClientOffsetX = 12, ClientOffsetY = 30 };
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await runner.RunAsync(new(window, new(), new(), DateTimeOffset.Now), _ => { }, timeout.Token);
        Assert.AreEqual("done", game.Scene);
        Assert.AreEqual(scenario == "collect" ? 1 : 0, game.Teleports);
    }

    private sealed class Replay(int width, string scenario) : IScreenCaptureService, IMouseInputService, IKeyboardInputService
    {
        public string Scene = "start";
        public int Teleports;
        private int _samples;

        public CapturedFrame Capture(CaptureTargetWindow window, CaptureMethod method)
        {
            var sample = Scene;
            if (Scene is "start" or "arrival" or "done")
                return FlowerSeedScreenRecognizerTests.LoadFixture("legendary-world.png", width, width * 9 / 16, 12, 30);
            if (Scene == "waiting")
            {
                sample = ++_samples switch { 1 => "event-48", 2 => "event-864", _ => "event-full" };
            }
            if (Scene == "unavailable") sample = "event-0";
            var path = Path.Combine(SourceDirectory(), "LocalScreenshots", "2026-11-08", "evening-rest", sample + ".png");
            if (!File.Exists(path)) Assert.Inconclusive($"缺少本地截图：{path}");
            return FlowerSeedScreenRecognizerTests.LoadFixture(path, width, width * 9 / 16, 12, 30, image =>
            {
                if (Scene == "unavailable")
                {
                    // 保留“探索”页头，用另一项活动替换第一行。
                    using var row = new OpenCvSharp.Mat(image, new OpenCvSharp.Rect(100, 220, 400, 80));
                    using var first = new OpenCvSharp.Mat(image, new OpenCvSharp.Rect(100, 130, 400, 80));
                    row.CopyTo(first);
                }
            });
        }

        public Task SendSequenceAsync(nint hwnd, string key, KeyboardInputOptions? options, CancellationToken token)
        {
            Scene = (Scene, key) switch
            {
                ("start" or "arrival", "F2") => "recommended",
                ("event-full" or "unavailable", "Esc") => "done",
                ("waiting", "Esc") when _samples >= 3 => "done",
                _ => throw new AssertFailedException($"{Scene} 意外输入 {key}")
            };
            return Task.CompletedTask;
        }

        public Task ClickAsync(nint hwnd, int x, int y, KeyboardInputMethod method, CancellationToken token)
        {
            var (next, px, py) = Scene switch
            {
                "recommended" when scenario == "full" => ("event-full", 387, 1090),
                "recommended" when scenario == "unavailable" => ("unavailable", 387, 1090),
                "recommended" => (Teleports == 0 ? "event-0" : "waiting", 387, 1090),
                "event-0" => ("shelter-map", 1553, 1084),
                "shelter-map" => ("arrival", 1635, 1084),
                _ => throw new AssertFailedException($"{Scene} 意外点击")
            };
            Assert.AreEqual(px, x * 2048d / width, 15, Scene);
            Assert.AreEqual(py, y * 2048d / width, 15, Scene);
            if (Scene == "shelter-map") Teleports++;
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
