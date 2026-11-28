using Microsoft.VisualStudio.TestTools.UnitTesting;
using RocoPilot.Contracts.Services;
using RocoPilot.Contracts.Services.Capture;
using RocoPilot.Models.Capture;
using RocoPilot.Models.Input;
using RocoPilot.Models.Runtime;
using RocoPilot.Services.ImageMatching;
using RocoPilot.Services.IndependentTasks;
using RocoPilot.Services.TextRecognition;
using RocoPilot.Services.TextRecognition.Backends;

namespace RocoPilot.Tests;

[TestClass]
[TestCategory("LocalScreenshots")]
public sealed class EveningRestTests
{
    [TestMethod]
    [DataRow(2048, "collect")]
    [DataRow(1600, "collect")]
    [DataRow(1280, "collect")]
    [DataRow(960, "collect")]
    [DataRow(2048, "renamed")]
    [DataRow(1600, "renamed")]
    [DataRow(1280, "renamed")]
    [DataRow(960, "renamed")]
    [DataRow(2048, "unselected")]
    [DataRow(1280, "unselected")]
    [DataRow(960, "unselected")]
    [DataRow(960, "shifted")]
    [DataRow(960, "full")]
    [DataRow(1280, "static-menu")]
    [DataRow(1280, "unavailable")]
    public async Task ReturnsToWorldAfterFullOrUnavailable(int width, string scenario)
    {
        var game = new Replay(width, scenario);
        using var backend = new OnnxOcrSingleLineTextRecognitionBackend();
        var ocr = new TextRecognitionService([new OnnxOcrTextRecognitionBackend(backend)]);
        var runner = new EveningRestRunner(game, new ImageMatchingService(), game, game, ocr);
        var window = new CaptureTargetWindow { Hwnd = 1, ClientWidth = width, ClientHeight = width * 9 / 16,
            ClientOffsetX = 12, ClientOffsetY = 30 };
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var readings = new List<string>();
        var result = await runner.RunAsync(new(window, new(), new(), DateTimeOffset.Now), progress =>
        {
            if (progress.Stage == "收集星光")
            {
                Assert.AreEqual(string.Empty, progress.Recognition);
                readings.Add(progress.Operation);
            }
            if (game.Scene == "waiting" && progress.Operation.Contains("成功"))
                Assert.AreEqual("event-full", game.LastSample);
        }, timeout.Token);
        Assert.AreEqual(scenario == "unavailable" ? "跳过：未找到晚间休憩活动" : "成功：本日星光已达 1200/1200，已尝试领取许愿星", result);
        if (scenario == "collect") CollectionAssert.AreEqual(new[] { "本日星光 0/1200", "本日星光 48/1200", "本日星光 1100/1200", "本日星光 1182/1200" }, readings);
        if (scenario == "renamed") CollectionAssert.AreEqual(new[] { "本日星光 864/1200" }, readings);
        Assert.AreEqual("done", game.Scene);
        Assert.AreEqual(scenario switch { "unavailable" => 0, "full" or "static-menu" => 1, _ => 3 }, game.WishClicks);
        Assert.AreEqual(scenario is "full" or "static-menu" or "unavailable" ? 0 : 1, game.Teleports);
        if (scenario is "unselected" or "shifted") Assert.AreEqual(3, game.ActivityClicks);
    }

    [TestMethod]
    [DataRow(false, 4)]
    [DataRow(true, 3)]
    public async Task LocatesWishAcrossMenuAnimation(bool classic, int frames)
    {
        var width = classic ? 1600 : 2048;
        var height = classic ? 1200 : 1152;
        var window = new CaptureTargetWindow { ClientWidth = width, ClientHeight = height, ClientOffsetX = 12, ClientOffsetY = 30 };
        var images = new ImageMatchingService();
        for (int i = 1; i <= frames; i++)
        {
            using var frame = FlowerSeedScreenRecognizerTests.LoadFixture(WishPath(classic, i), width, height, 12, 30, image =>
            {
                // 合成无角标状态。
                using var badge = new OpenCvSharp.Mat(image, classic ? new OpenCvSharp.Rect(893, 528, 32, 24) : new OpenCvSharp.Rect(1148, 470, 44, 30));
                badge.SetTo(OpenCvSharp.Scalar.Black);
            });
            var point = await EveningRestRunner.LocateWishAsync(new(frame, window, images), frame, default);
            Assert.IsNotNull(point, $"动画帧{i}");
            Assert.AreEqual(classic ? 840 : 1082, point.Value.X, 12);
            Assert.AreEqual(classic ? 571 : 528, point.Value.Y, 15);
        }
    }

    private static string WishPath(bool classic, int frame) => Path.Combine(SourceDirectory(), "LocalScreenshots",
        classic ? "2026-11-25" : "2026-11-24", "许愿星", $"ESC菜单-{(classic ? "4比3-" : "")}许愿星角标12-动画{frame}.png");

    private static string SourceDirectory([System.Runtime.CompilerServices.CallerFilePath] string path = "") => Path.GetDirectoryName(path)!;

    private sealed class Replay(int width, string scenario) : IScreenCaptureService, IMouseInputService, IKeyboardInputService
    {
        public string Scene = "start";
        public string? LastSample;
        public int WishClicks;
        public int Teleports;
        public int ActivityClicks;
        private int _samples;
        private CapturedFrame? _staticMenu;
        private bool SelectActivity => scenario is "unselected" or "shifted";

        public CapturedFrame Capture(CaptureTargetWindow window, CaptureMethod method)
        {
            var sample = Scene;
            if (Scene == "wish" && scenario == "static-menu")
            {
                _staticMenu ??= FlowerSeedScreenRecognizerTests.LoadFixture(WishPath(false, 1), width, width * 9 / 16, 12, 30);
                return _staticMenu.AddReference();
            }
            if (Scene == "wish")
                return FlowerSeedScreenRecognizerTests.LoadFixture(WishPath(false, WishClicks + 1), width, width * 9 / 16, 12, 30, image =>
                {
                    if (scenario == "full" && WishClicks == 1)
                    {
                        using var popup = new OpenCvSharp.Mat(image, new OpenCvSharp.Rect(980, 455, 190, 165));
                        popup.SetTo(OpenCvSharp.Scalar.Black);
                    }
                });
            if (Scene is "start" or "arrival" or "done")
                return FlowerSeedScreenRecognizerTests.LoadFixture("legendary-world.png", width, width * 9 / 16, 12, 30);
            if (Scene == "waiting")
            {
                sample = ++_samples switch
                {
                    1 => "event-48",
                    2 when scenario == "collect" => "event-0",
                    3 when scenario == "collect" => "event-48",
                    4 when scenario == "collect" => "event-1100",
                    5 when scenario == "collect" => "event-1182",
                    2 => "event-864",
                    3 => "event-1182",
                    _ => "event-full"
                };
            }
            if (Scene == "unavailable") sample = "event-0";
            LastSample = sample;
            var file = sample == "event-1100" ? "event-full" : sample;
            var path = Path.Combine(SourceDirectory(), "LocalScreenshots", "2026-11-08", "evening-rest", file + ".png");
            if (sample == "event-1182")
                path = Path.Combine(SourceDirectory(), "LocalScreenshots", "2026-11-21", "evening-rest", "event-1182.png");
            // 新活动原图验证入口，后续地图及进度用旧素材模拟，不代表新活动现场回放。
            if ((scenario == "renamed" || SelectActivity) && sample == "event-0")
                path = Path.Combine(SourceDirectory(), "LocalScreenshots", "2026-11-17", "evening-rest", "good-night-kingdom-0.png");
            if (Scene == "unselected")
                path = Path.Combine(SourceDirectory(), "LocalScreenshots", "2026-11-17", "evening-rest", "good-night-kingdom-unselected.png");
            if (!File.Exists(path)) Assert.Inconclusive($"缺少本地截图：{path}");
            return FlowerSeedScreenRecognizerTests.LoadFixture(path, width, width * 9 / 16, 12, 30, image =>
            {
                if (sample == "event-1100")
                {
                    // 复制首位 1，合成仍能匹配旧满额模板的 1100/1200。
                    using var first = new OpenCvSharp.Mat(image, new OpenCvSharp.Rect(1756, 848, 16, 31));
                    using var second = new OpenCvSharp.Mat(image, new OpenCvSharp.Rect(1774, 848, 16, 31));
                    first.CopyTo(second);
                }
                if (SelectActivity && Scene == "shelter-map")
                {
                    // 合成名称缺失的地图详情，验证地图名不参与导航判断。
                    using var title = new OpenCvSharp.Mat(image, new OpenCvSharp.Rect(1310, 15, 420, 101));
                    title.SetTo(new OpenCvSharp.Scalar(230, 230, 230));
                }
                if (scenario == "shifted" && (Scene is "unselected" or "event-0" or "waiting"))
                {
                    // 合成活动位于第二行，覆盖选中与未选中两种状态。
                    using var first = new OpenCvSharp.Mat(image, new OpenCvSharp.Rect(100, 130, 400, 80));
                    using var second = new OpenCvSharp.Mat(image, new OpenCvSharp.Rect(100, 222, 400, 80));
                    using var saved = first.Clone();
                    second.CopyTo(first);
                    saved.CopyTo(second);
                }
                if (Scene == "unavailable" || (scenario == "renamed" && sample == "event-1182"))
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
                ("done", "Esc") when scenario != "unavailable" => "wish",
                ("wish", "Esc") => "done",
                ("start" or "arrival", "F2") => "recommended",
                ("event-full" or "unavailable", "Esc") => "done",
                ("waiting", "Esc") when _samples >= 3 => "done",
                _ => throw new AssertFailedException($"{Scene} 意外输入 {key}")
            };
            if (Scene == "done") _staticMenu?.Dispose();
            return Task.CompletedTask;
        }

        public Task ClickAsync(nint hwnd, int x, int y, KeyboardInputMethod method, CancellationToken token)
        {
            var (next, px, py) = Scene switch
            {
                "recommended" when scenario is "full" or "static-menu" => ("event-full", 387, 1090),
                "recommended" when scenario == "unavailable" => ("unavailable", 387, 1090),
                "recommended" when SelectActivity => ("unselected", 387, 1090),
                "recommended" => (Teleports == 0 ? "event-0" : "waiting", 387, 1090),
                "unselected" => (ActivityClicks == 0 ? "unselected" : Teleports == 0 ? "event-0" : "waiting",
                    165, scenario == "shifted" ? 260 : 168),
                "event-0" => ("shelter-map", 1553, 1084),
                "shelter-map" => ("arrival", 1635, 1084),
                "wish" => ("wish", 1082, 528),
                _ => throw new AssertFailedException($"{Scene} 意外点击")
            };
            Assert.AreEqual(px, x * 2048d / width, 15, Scene);
            Assert.AreEqual(py, y * 2048d / width, 15, Scene);
            if (Scene == "wish") WishClicks++;
            if (Scene == "shelter-map") Teleports++;
            if (Scene == "unselected") ActivityClicks++;
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
