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
public sealed class DailyTaskScreenPointTests
{
    [TestMethod]
    [DataRow(1080, 608)]
    [DataRow(1920, 1080)]
    public void ReferencePointsMapIntoClientArea(int width, int height)
    {
        using var frame = new CapturedFrame(width, height, new byte[width * height * 4]);
        var window = new CaptureTargetWindow { Hwnd = 1, ClientWidth = width, ClientHeight = height };
        var screen = new DailyTaskScreen(frame, window, new ImageMatchingService());
        var scale = Math.Min(width / 2048d, height / 1152d);

        var tab = screen.ReferenceToClient(173, 516);
        Assert.AreEqual(173 * scale, (double)tab.X, 1d, "tab");
        Assert.AreEqual(516 * scale + (height - 1152 * scale) * .5, (double)tab.Y, 1d, "tab");

        var close = screen.ReferenceToClient(1900, 61);
        Assert.AreEqual(1900 * scale + (width - 2048 * scale), (double)close.X, 1d, "close");
        Assert.AreEqual(61 * scale, (double)close.Y, 1d, "close");

        Assert.AreEqual(1555 * scale + (width - 2048 * scale),
            (double)screen.ReferenceToClient(1555, 0).X, 1d, "slider");
    }

    [TestMethod]
    public void ConcretePointsStayInsideSmallClientArea()
    {
        using var frame = new CapturedFrame(1080, 608, new byte[1080 * 608 * 4]);
        var window = new CaptureTargetWindow { Hwnd = 1, ClientWidth = 1080, ClientHeight = 608 };
        var screen = new DailyTaskScreen(frame, window, new ImageMatchingService());

        Assert.AreEqual((91, 272), screen.ReferenceToClient(173, 516));
        Assert.AreEqual((1002, 32), screen.ReferenceToClient(1900, 61));
        Assert.AreEqual(820, screen.ReferenceToClient(1555, 0).X);
    }
}

[TestClass]
[TestCategory("LocalScreenshots")]
public class StaminaCraftingTests
{
    [TestMethod]
    [DataRow("map-continent", 2048, false, false)]
    [DataRow("map-academy", 1280, false, false)]
    [DataRow("map-garden", 960, false, false)]
    [DataRow("map-garden", 960, true, false)]
    [DataRow("map-academy", 1280, true, false)]
    [DataRow("map-continent", 1600, false, true)]
    public async Task DailyCraftsOnlyAfterSelectingSeasonAndMaximum(string initialMap, int width, bool insufficient, bool useReportedRecipe)
    {
        var replay = new Replay(initialMap, width, insufficient, useReportedRecipe);
        var runner = new StaminaCraftingRunner(replay, new ImageMatchingService(), replay, replay);
        var window = new CaptureTargetWindow { Hwnd = 1, ClientWidth = width, ClientHeight = width * 9 / 16, ClientOffsetX = 12, ClientOffsetY = 30 };
        var result = await runner.RunAsync(new(window, new(), new(), DateTimeOffset.Now),
            IndependentTaskSettings.CreateDefault(), _ => { }, CancellationToken.None);
        Assert.AreEqual("done", replay.Stage);
        Assert.AreEqual(insufficient ? "跳过：体力不足" : "成功：已制造赛季球", result);
    }

    [TestMethod]
    [DataRow("convert-purifying-potion", 1600, "跳过：蜂蜜或体力不足")]
    [DataRow("convert-mark-selected-max-one", 2048, "成功：已制造凝练印记")]
    [DataRow("convert-bright-mark-dust-selected", 1080, "成功：已制造凝练印记")]
    public async Task ConvertsOnlySelectedAvailableMark(string firstRecipe, int width, string expected)
    {
        var replay = new Replay("map-garden", width, false, false, firstRecipe);
        var runner = new StaminaCraftingRunner(replay, new ImageMatchingService(), replay, replay);
        var window = new CaptureTargetWindow { Hwnd = 1, ClientWidth = width, ClientHeight = width * 9 / 16, ClientOffsetX = 12, ClientOffsetY = 30 };
        var settings = new IndependentTaskSettings { StaminaCraftingProduct = StaminaCraftingProductKind.CondensedMark };
        var result = await runner.RunAsync(new(window, new(), new(), DateTimeOffset.Now), settings, _ => { }, CancellationToken.None);
        Assert.AreEqual(expected, result);
        Assert.AreEqual("done", replay.Stage);
    }

    private sealed class Replay(string initialMap, int width, bool insufficient, bool useReportedRecipe, string? firstMarkRecipe = null) : IScreenCaptureService, IMouseInputService, IKeyboardInputService
    {
        public string Stage = "start";
        private int _frames;
        private bool _resultCloseSent;
        private CapturedFrame? _staticResult;
        private readonly Stopwatch _resultWait = new();

        public CapturedFrame Capture(CaptureTargetWindow window, CaptureMethod method)
        {
            // 识别间隔约 0.12 秒：普通界面 5 秒、等结算的界面 20 秒内没有推进视为停滞。
            Assert.IsTrue(++_frames < (Stage is "skip-craft" or "craft-result" or "exit-recipe" ? 160 : 40), $"流程停在 {Stage}");
            if (Stage == "craft-result" && _staticResult is not null) return _staticResult.AddReference();
            var name = Stage switch
            {
                "start" or "done" => "world-arrival",
                "open" => initialMap,
                "options" => "map-options",
                "garden" => "garden-cauldron-marked",
                // F 交互后先保留一帧没有跳过或播放中提示的画面，期间应等待。
                "intro-animation" when _frames == 1 => "world-interaction",
                "season-selected" when _frames == 1 => "recipe-list-marked",
                "slider-maximum" when _frames == 1 => "season-selected",
                "skip-craft" when _frames <= 2 => "slider-maximum",
                "skip-craft" => "legendary-skip",
                "exit-recipe" when firstMarkRecipe != null => "convert-purifying-potion",
                "exit-recipe" => "basic-alchemy",
                "exit-dialogue" => "choices",
                _ => Stage
            };
            if (Stage == "skip-craft" && _frames == 3) _resultWait.Restart();
            var path = Path.Combine(SourceDirectory(), "LocalScreenshots", "2026-11-07", "stamina-crafting", name + ".png");
            if (name.StartsWith("convert-", StringComparison.Ordinal) || name == "basic-ball")
                path = Path.Combine(SourceDirectory(), "LocalScreenshots", "2026-11-29", "stamina-crafting", name + ".png");
            if (name is "world-interaction" or "intro-animation" or "dialogue-before-choices" or "choices")
                path = Path.Combine(SourceDirectory(), "LocalScreenshots", "2026-11-18", "stamina-crafting", name + ".png");
            if (name == "season-selected" && insufficient)
                path = Path.Combine(SourceDirectory(), "LocalScreenshots", "2026-11-08", "stamina-crafting", "season-insufficient-stamina.png");
            if (name == "craft-result" && initialMap != "map-continent")
                path = Path.Combine(SourceDirectory(), "LocalScreenshots", "2026-11-08", "stamina-crafting", "craft-result-bonus-61.png");
            if (name == "basic-alchemy")
                path = Path.Combine(SourceDirectory(), "LocalScreenshots", "2026-11-25", "stamina-crafting", name + ".png");
            var useReportedFrame = useReportedRecipe && name is "recipe-list-marked" or "basic-alchemy";
            if (useReportedFrame)
                path = Path.Combine(SourceDirectory(), "LocalScreenshots", "2026-11-28", "stamina-crafting", "crafting-season-badge-stalled.png");
            if (name == "legendary-skip") path = Path.Combine(SourceDirectory(), "Fixtures", name + ".png");
            if (!File.Exists(path)) Assert.Inconclusive($"缺少本地回放图：{path}");
            var frame = FlowerSeedScreenRecognizerTests.LoadFixture(path, width, width * 9 / 16, 12, 30, transform: mat =>
            {
                if (useReportedFrame || name == "recipe-list-marked")
                {
                    var clientArea = useReportedFrame ? new OpenCvSharp.Rect(1, 31, 1600, 900) : new OpenCvSharp.Rect(2, 45, 2048, 1152);
                    using var client = new OpenCvSharp.Mat(mat, clientArea);
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
            // 首次空格只补全当前句，仍需识别播放中后再次推进。
            if (Stage == "dialogue-before-choices" && key == "Space" && _frames == 1)
                return Task.CompletedTask;
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
                ("world-interaction", "F") => "intro-animation",
                ("intro-animation", "Esc") when _frames > 1 => "dialogue-before-choices",
                ("dialogue-before-choices", "Space") => "choices",
                ("skip-craft", "Esc") => "craft-result",
                ("choices", "1") => firstMarkRecipe == null ? "recipe-list-marked" : "basic-ball",
                ("craft-result", "Esc") => "exit-recipe",
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
                "basic-ball" => (firstMarkRecipe!, 173, 516),
                "convert-bright-mark-dust-selected" => ("convert-mark-selected-max-one", 342, 210),
                "convert-mark-selected-max-one" => ("skip-craft", 1800, 1080),
                "convert-gray-mark-potion-selected" => ("exit-dialogue", 1900, 59),
                "open" when initialMap == "map-continent" => ("options", 333, 1074),
                "open" when initialMap == "map-academy" => ("options", 300, 1074),
                "options" => ("garden", 237, 914),
                "open" or "garden" => ("teleport", 813, 550),
                "teleport" => ("world-arrival", 1634, 1085),
                "recipe-list-marked" when useReportedRecipe => ("season-selected", 505, 528),
                "recipe-list-marked" when initialMap == "map-academy" => ("season-selected", 345, 219),
                "recipe-list-marked" => ("season-selected", 658, 528),
                "slider-maximum" => ("skip-craft", 1800, 1080),
                "season-selected" when insufficient => ("exit-dialogue", 1900, 59),
                "exit-recipe" => ("exit-dialogue", 1900, 59),
                _ => throw new AssertFailedException($"{Stage} 意外点击")
            };
            CheckPoint(x, y, cx, cy);
            Advance(next);
            return Task.CompletedTask;
        }

        public void LookDown(nint hwnd, KeyboardInputMethod method) => throw new NotSupportedException();
        public Task PressAtCurrentPositionAsync(nint hwnd, int holdMs, KeyboardInputMethod method, CancellationToken token) => throw new NotSupportedException();
        public Task DragAsync(nint hwnd, int x, int y, int endX, int endY, KeyboardInputMethod method, CancellationToken cancellationToken)
        {
            Assert.IsFalse(insufficient, "制造按钮置灰时不应拖动滑块。");
            Assert.AreEqual("season-selected", Stage);
            Assert.IsTrue(_frames > 1);
            CheckPoint(x, y, 1239, 1021);
            CheckPoint(endX, endY, 1579, 1021);
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
        public Task ScrollAsync(nint hwnd, int x, int y, int delta, KeyboardInputMethod method, CancellationToken token)
        {
            Assert.AreEqual("convert-purifying-potion", Stage);
            Assert.IsTrue(delta < 0);
            CheckPoint(x, y, 650, 500);
            Advance("convert-gray-mark-potion-selected");
            return Task.CompletedTask;
        }
        public Task ScrollAtCurrentPositionAsync(nint hwnd, int delta, KeyboardInputMethod method, CancellationToken token) => throw new NotSupportedException();
        public bool TryParseSequence(string sequence, out IReadOnlyList<KeyStroke> keys, out string error) => throw new NotSupportedException();
        public Task SendSequenceAsync(nint hwnd, IReadOnlyList<KeyStroke> keys, KeyboardInputOptions? options, CancellationToken token) => throw new NotSupportedException();
    }
}
