using System.Diagnostics;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using RocoPilot.Contracts.Services;
using RocoPilot.Contracts.Services.Capture;
using RocoPilot.Core.Input;
using RocoPilot.Models.Capture;
using RocoPilot.Models.Input;
using RocoPilot.Models.Runtime;
using RocoPilot.Services.ImageMatching;
using RocoPilot.Services.IndependentTasks;

namespace RocoPilot.Tests;

[TestClass]
[TestCategory("LocalScreenshots")]
public class StaminaCraftingTests
{
    [TestMethod]
    [DataRow("map-continent", 2048, false)]
    [DataRow("map-academy", 1280, false)]
    [DataRow("map-garden", 960, false)]
    [DataRow("map-garden", 960, true)]
    [DataRow("map-academy", 1280, true)]
    public async Task DailyCraftsOnlyAfterSelectingSeasonAndMaximum(string initialMap, int width, bool insufficient)
    {
        var replay = new Replay(initialMap, width, insufficient);
        var runner = new StaminaCraftingRunner(replay, new ImageMatchingService(), replay, replay);
        var window = new CaptureTargetWindow { Hwnd = 1, ClientWidth = width, ClientHeight = width * 9 / 16, ClientOffsetX = 12, ClientOffsetY = 30 };
        var result = await runner.RunAsync(new(window, new(), new(), DateTimeOffset.Now), _ => { }, CancellationToken.None);
        Assert.AreEqual("done", replay.Stage);
        Assert.AreEqual(insufficient ? "跳过：体力不足" : "成功：已制造赛季球", result);
    }

    private sealed class Replay(string initialMap, int width, bool insufficient) : IScreenCaptureService, IMouseInputService, IKeyboardInputService
    {
        public string Stage = "start";
        private int _frames;
        private bool _resultCloseSent;
        private CapturedFrame? _staticResult;
        private readonly Stopwatch _resultWait = new();

        public CapturedFrame Capture(CaptureTargetWindow window, CaptureMethod method)
        {
            Assert.IsTrue(++_frames < (Stage is "skip-craft" or "craft-result" or "exit-recipe" ? 45 : 8), $"流程停在 {Stage}");
            if (Stage == "craft-result" && _staticResult is not null) return _staticResult.AddReference();
            var name = Stage switch
            {
                "start" or "done" => "world-arrival",
                "open" => initialMap,
                "options" => "map-options",
                "garden" => "garden-cauldron-marked",
                "season-selected" when _frames == 1 => "recipe-list-marked",
                "slider-maximum" when _frames == 1 => "season-selected",
                "skip-craft" when _frames <= 2 => "slider-maximum",
                "skip-intro" or "skip-craft" => "legendary-skip",
                "exit-recipe" => "season-selected",
                "exit-dialogue" => "choices",
                _ => Stage
            };
            if (Stage == "skip-craft" && _frames == 3) _resultWait.Restart();
            var path = Path.Combine(SourceDirectory(), "LocalScreenshots", "2026-11-07", "stamina-crafting", name + ".png");
            if (name == "season-selected" && insufficient)
                path = Path.Combine(SourceDirectory(), "LocalScreenshots", "2026-11-08", "stamina-crafting", "season-insufficient-stamina.png");
            if (name == "craft-result" && initialMap != "map-continent")
                path = Path.Combine(SourceDirectory(), "LocalScreenshots", "2026-11-08", "stamina-crafting", "craft-result-bonus-61.png");
            if (name == "legendary-skip") path = Path.Combine(SourceDirectory(), "Fixtures", name + ".png");
            if (!File.Exists(path)) Assert.Inconclusive($"缺少本地回放图：{path}");
            var frame = FlowerSeedScreenRecognizerTests.LoadFixture(path, width, width * 9 / 16, 12, 30, transform: mat =>
            {
                if (name == "recipe-list-marked")
                {
                    using var client = new OpenCvSharp.Mat(mat, new OpenCvSharp.Rect(2, 45, 2048, 1152));
                    using var copy = client.Clone();
                    copy.CopyTo(mat);
                    if (initialMap == "map-academy")
                    {
                        // 将赛季角标所在卡片从第三行第三列换到第一行第一列。
                        using var from = new OpenCvSharp.Mat(mat, new OpenCvSharp.Rect(584, 460, 146, 146));
                        using var to = new OpenCvSharp.Mat(mat, new OpenCvSharp.Rect(271, 151, 146, 146));
                        using var original = to.Clone();
                        from.CopyTo(to);
                        original.CopyTo(from);
                    }
                }
            });
            if (initialMap == "map-garden" && Stage == "craft-result") _staticResult = frame.AddReference();
            return frame;
        }

        private static string SourceDirectory([System.Runtime.CompilerServices.CallerFilePath] string path = "")
            => Path.GetDirectoryName(path)!;

        public Task SendSequenceAsync(nint hwnd, string key, KeyboardInputOptions? options = null, CancellationToken cancellationToken = default)
        {
            if (Stage is "skip-craft" or "craft-result" && key == "Esc")
            {
                Assert.IsTrue(_resultWait.Elapsed >= TimeSpan.FromSeconds(3), "制造按钮消失后或前次 ESC 后需等待结算。");
                _resultWait.Restart();
            }
            // 模拟结算动画尚未接受第一次关闭输入。
            if (initialMap == "map-garden" && Stage == "craft-result" && key == "Esc" && !_resultCloseSent)
            {
                _resultCloseSent = true;
                return Task.CompletedTask;
            }
            if (Stage == "craft-result") _staticResult?.Dispose();
            Advance((Stage, key) switch
            {
                ("start", "M") => "open",
                ("world-arrival", "W") => "world-interaction",
                ("world-interaction", "F") => "skip-intro",
                ("skip-intro", "Esc") => "dialogue",
                ("skip-craft", "Esc") => "craft-result",
                ("dialogue", "Esc") => "choices",
                ("choices", "1") => "recipe-list-marked",
                ("craft-result", "Esc") => "exit-recipe",
                ("season-selected", "Esc") when insufficient => "exit-dialogue",
                ("exit-recipe", "Esc") => "exit-dialogue",
                ("exit-dialogue", "Esc") => "done",
                _ => throw new AssertFailedException($"{Stage} 意外按键 {key}")
            });
            return Task.CompletedTask;
        }

        public Task ClickAsync(nint hwnd, int x, int y, KeyboardInputMethod method, CancellationToken cancellationToken)
        {
            if (Stage == "slider-maximum") Assert.IsTrue(_frames > 1);
            var (next, cx, cy) = Stage switch
            {
                "open" when initialMap == "map-continent" => ("options", 333, 1074),
                "open" when initialMap == "map-academy" => ("options", 300, 1074),
                "options" => ("garden", 237, 914),
                "open" or "garden" => ("teleport", 813, 550),
                "teleport" => ("world-arrival", 1634, 1085),
                "recipe-list-marked" when initialMap == "map-academy" => ("season-selected", 345, 219),
                "recipe-list-marked" => ("season-selected", 658, 528),
                "slider-maximum" => ("skip-craft", 1800, 1080),
                _ => throw new AssertFailedException($"{Stage} 意外点击")
            };
            CheckPoint(x, y, cx, cy);
            Advance(next);
            return Task.CompletedTask;
        }

        public Task DragAsync(nint hwnd, int x, int y, int endX, int endY, KeyboardInputMethod method, CancellationToken cancellationToken)
        {
            Assert.IsFalse(insufficient, "制造按钮置灰时不应拖动滑块。");
            Assert.AreEqual("season-selected", Stage);
            Assert.IsTrue(_frames > 1);
            CheckPoint(x, y, 1239, 1021);
            CheckPoint(endX, endY, 1510, 1021);
            Advance("slider-maximum");
            return Task.CompletedTask;
        }

        private void CheckPoint(int x, int y, int referenceX, int referenceY)
        {
            Assert.AreEqual(referenceX, x * 2048d / width, 12, Stage);
            Assert.AreEqual(referenceY, y * 2048d / width, 12, Stage);
        }

        private void Advance(string next) { Stage = next; _frames = 0; }
        public bool IsWindowAvailable(nint hwnd) => true;
        public bool IsWindowForeground(nint hwnd) => true;
        public bool RequiresForeground(KeyboardInputMethod method) => true;
        public void EnsureReady(KeyboardInputMethod method) { }
        public void Release(CaptureTargetWindow window, CaptureMethod method) { }
        public Task ScrollAsync(nint hwnd, int x, int y, int delta, KeyboardInputMethod method, CancellationToken token) => throw new NotSupportedException();
        public Task ScrollAtCurrentPositionAsync(nint hwnd, int delta, KeyboardInputMethod method, CancellationToken token) => throw new NotSupportedException();
        public bool TryParseSequence(string sequence, out IReadOnlyList<KeyStroke> keys, out string error) => throw new NotSupportedException();
        public Task SendSequenceAsync(nint hwnd, IReadOnlyList<KeyStroke> keys, KeyboardInputOptions? options, CancellationToken token) => throw new NotSupportedException();
    }
}
