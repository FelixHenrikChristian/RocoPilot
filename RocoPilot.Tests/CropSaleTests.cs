using System.Diagnostics;
using System.Runtime.CompilerServices;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using OpenCvSharp;
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
public sealed class CropSaleTests
{
    private static string Folder(string date = "2026-11-15", [CallerFilePath] string file = "") =>
        Path.Combine(Path.GetDirectoryName(file)!, "LocalScreenshots", date, "crop-sale");

    private static CaptureTargetWindow Window(int width) => new()
    {
        Hwnd = 1, ClientWidth = width, ClientHeight = width * 9 / 16, ClientOffsetX = 12, ClientOffsetY = 30
    };

    private static CapturedFrame Load(string name, int width) => FlowerSeedScreenRecognizerTests.LoadFixture(
        Path.Combine(Folder(), name + ".png"), width, width * 9 / 16, 12, 30);

    private static CapturedFrame LoadReportedStation() => FlowerSeedScreenRecognizerTests.LoadFixture(
        Path.Combine(Folder("2026-11-28"), "crop-sale-sunflower-stalled.png"), clientX: 12, clientY: 30, transform: mat =>
        {
            using var client = new OpenCvSharp.Mat(mat, new OpenCvSharp.Rect(1, 31, 1600, 900));
            using var copy = client.Clone();
            copy.CopyTo(mat);
        });

    [TestMethod]
    [DataRow(2048)]
    [DataRow(1600)]
    [DataRow(1280)]
    [DataRow(960)]
    public async Task ReadsOnlyTheExpectedIncomeAtTheBottom(int width)
    {
        using var backend = new OnnxOcrSingleLineTextRecognitionBackend();
        var ocr = new TextRecognitionService([new OnnxOcrTextRecognitionBackend(backend)]);
        var images = new ImageMatchingService();
        foreach (var amount in new long[] { 9000, 42000, 132000, 582000, 912000, 1182000, 2052000 })
        {
            using var frame = Load($"sunflower-income-{amount}", width);
            var screen = new DailyTaskScreen(frame, Window(width), images);
            Assert.AreEqual(amount, await CropSaleRunner.ReadIncomeAsync(frame, screen, ocr, default), $"{width} / {amount}");
        }
        using var empty = Load("flowers-top", width);
        Assert.IsNull(await CropSaleRunner.ReadIncomeAsync(empty, new DailyTaskScreen(empty, Window(width), images), ocr, default));
    }

    [TestMethod]
    [DataRow(2048)]
    [DataRow(1600)]
    [DataRow(1280)]
    [DataRow(960)]
    [DataRow(1600, true)]
    public Task LocatesAllFlowersOnlyOnTheirOwnCards(int width, bool useReportedTop = false) =>
        CheckFlowerCardsAsync(width, useReportedTop, 1, 0, 0);

    [TestMethod]
    [DataRow(1600, 1d, 4, -4)]
    [DataRow(1600, 1d, -4, 4)]
    [DataRow(1600, .97, 0, 0)]
    [DataRow(1600, 1.03, 0, 0)]
    [DataRow(1600, 1.03, 4, -4)]
    public Task FlowerCardsTolerateSmallShiftsAndRescaling(int width, double zoom, int dx, int dy) =>
        CheckFlowerCardsAsync(width, false, zoom, dx, dy);

    [TestMethod]
    [DataRow("native-list-top", 0, 12, 95)]
    [DataRow("native-list-rows-2-to-4", 3, 9, 177)]
    [DataRow("native-list-rows-3-to-5", 6, 9, 149)]
    [DataRow("native-list-rows-4-to-6", 9, 9, 144)]
    [DataRow("native-list-rows-5-to-7", 12, 9, 146)]
    [DataRow("native-list-bottom", 15, 9, 229)]
    public async Task Native1600ScrollPositionsOnlyMatchTheirVisibleFlowers(string name, int first, int count, int top)
    {
        const int width = 1600;
        var path = Path.Combine(Folder("2026-11-28"), name + ".png");
        using var frame = FlowerSeedScreenRecognizerTests.LoadFixture(path, clientX: 12, clientY: 30);
        Assert.AreEqual((1612, 930), (frame.Width, frame.Height), name);
        var screen = new DailyTaskScreen(frame, Window(width), new ImageMatchingService());
        var cards = await CropSaleRunner.FindFlowerCardsAsync(screen, default);
        Assert.HasCount(count, cards, name);
        for (var i = 0; i < count; i++)
        {
            var x = 93 + i % 3 * 164 + 6 * screen.Scale;
            var y = top + i / 3 * 191.5 + 3 * screen.Scale;
            var card = cards.Single(region => Math.Abs(screen.ClientX(region.X) - x) <= 4 &&
                Math.Abs(screen.ClientY(region.Y) - y) <= 4);
            await CheckCardContentsAsync(screen, card, first + i, true, name);
        }
    }

    private static async Task CheckFlowerCardsAsync(int width, bool useReportedTop, double zoom, int dx, int dy)
    {
        var images = new ImageMatchingService();
        var pages = new[] { "flowers-top", "flowers-middle", "flowers-bottom-selected" };
        var flowers = IndependentTaskSettings.SaleFlowers;
        var scale = width / 2048d;
        var shifted = zoom != 1 || dx != 0 || dy != 0;
        for (var page = 0; page < (useReportedTop ? 1 : pages.Length); page++)
        {
            using var frame = useReportedTop ? LoadReportedStation() : FlowerSeedScreenRecognizerTests.LoadFixture(
                Path.Combine(Folder(), pages[page] + ".png"), width, width * 9 / 16, 12, 30, shifted ? Transform : null);
            var screen = new DailyTaskScreen(frame, Window(width), images);
            var cards = await CropSaleRunner.FindFlowerCardsAsync(screen, default);
            var inspectedCards = 0;
            var baseline = page switch { 0 => 122, 1 => 185 - 3 * 245, _ => 488 - 6 * 245 };
            for (var i = 0; i < flowers.Count; i++)
            {
                var x = 120 + i % 3 * 210;
                var y = baseline + i / 3 * 245;
                if (y < 96 || y + 139 > 1020 || page == 2 && i == 17) continue;
                var expectedX = (420 + (x - 420) * zoom + 6) * scale + dx;
                var expectedY = (560 + (y - 560) * zoom + 3) * scale + dy;
                var located = cards.Where(card => Math.Abs(screen.ClientX(card.X) - expectedX) <= 5 &&
                    Math.Abs(screen.ClientY(card.Y) - expectedY) <= 5).ToArray();
                inspectedCards += located.Length;
                // 列表第四行在原图已贴近裁切边界，放大后可能只剩半张卡。
                var requireMatch = !shifted || page != 0 || i < 9;
                if (requireMatch) Assert.HasCount(1, located, $"{flowers[i]} 的卡片位置，页 {page}，宽度 {width}");
                foreach (var card in located)
                    await CheckCardContentsAsync(screen, card, i, requireMatch,
                        $"页 {page}，宽度 {width}，缩放 {zoom}，偏移 ({dx},{dy})");
            }
            Assert.AreEqual(cards.Count, inspectedCards, $"存在未归入实际卡片的搜索区域，页 {page}，宽度 {width}");
        }

        void Transform(Mat source)
        {
            using var affine = Cv2.GetRotationMatrix2D(new Point2f(420, 560), 0, zoom);
            affine.Set(0, 2, affine.At<double>(0, 2) + dx / scale);
            affine.Set(1, 2, affine.At<double>(1, 2) + dy / scale);
            Cv2.WarpAffine(source, source, affine, source.Size(), InterpolationFlags.Linear);
        }
    }

    private static async Task CheckCardContentsAsync(DailyTaskScreen screen, RocoPilot.Models.Recognition.RecognitionRegion card,
        int expectedFlower, bool requireMatch, string scenario)
    {
        var flowers = IndependentTaskSettings.SaleFlowers;
        for (var candidate = 0; candidate < flowers.Count; candidate++)
        {
            var match = await CropSaleRunner.MatchFlowerCardAsync(screen, card, flowers[candidate], default);
            var context = $"{scenario}，卡片 {flowers[expectedFlower]}，模板 {flowers[candidate]}";
            if (candidate != expectedFlower) Assert.IsNull(match, context);
            else if (requireMatch) Assert.IsNotNull(match, context);
        }
    }

    [TestMethod]
    [DataRow(2048, 36)]
    [DataRow(1280, 10)]
    [DataRow(960, 1)]
    public Task SellsOneFlowerWithConfiguredPressesAndReturnsToWorld(int width, int quantity) =>
        AssertSaleAsync(width, quantity, "紫晶菇", 3000, useReportedStation: false);

    [TestMethod]
    public Task SellsSunflowerFromReported1600By900Station() =>
        AssertSaleAsync(1600, 3, "向阳花", 9000, useReportedStation: true);

    private static async Task AssertSaleAsync(int width, int quantity, string flower, int income, bool useReportedStation)
    {
        var game = new Replay(width, useReportedStation);
        using var backend = new OnnxOcrSingleLineTextRecognitionBackend();
        var ocr = new TextRecognitionService([new OnnxOcrTextRecognitionBackend(backend)]);
        var runner = new CropSaleRunner(game, new ImageMatchingService(), game, game, ocr);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var outcome = await runner.RunAsync(new(Window(width), new(), new(), DateTimeOffset.Now), flower, quantity, progress => Console.WriteLine(progress), timeout.Token);
        Assert.AreEqual("done", game.Scene);
        Assert.AreEqual(quantity, game.FlowerClicks);
        // 连点前只定位一次；点完之后要看着出售按钮稳定一会儿才点，这期间会多看几帧。
        Assert.AreEqual(quantity == 1 ? 0 : 1, game.SelectedFramesAtLastFlowerClick, "选中后只定位一次连点位置，连点中途不重新识别");
        Assert.IsGreaterThan(game.SelectedFramesAtLastFlowerClick, game.SelectedFrames, "点完后要用新画面读取最终收入");
        if (game.FlowerClickIntervals.Count > 0)
            Console.WriteLine($"连续选花平均间隔 {game.FlowerClickIntervals.Average():F0} ms");
        StringAssert.Contains(outcome, $"已领取{flower}的出售奖励，预期收入 {income} 金币");
    }

    private sealed class Replay(int width, bool useReportedStation) : IScreenCaptureService, IKeyboardInputService, IMouseInputService
    {
        public string Scene = "start";
        public int FlowerClicks { get; private set; }
        public int SelectedFrames { get; private set; }
        public int SelectedFramesAtLastFlowerClick { get; private set; }
        public List<long> FlowerClickIntervals { get; } = [];
        private readonly Stopwatch _sinceLastInput = new();
        private CapturedFrame? _arrivalFrame;
        private int _staleWalkFrames;

        // 输入之间至少隔着“等画面更新 + 反应时间”，不再要求固定的 1 秒。
        private void CheckInputInterval(int minimumMs = 400)
        {
            if (_sinceLastInput.IsRunning)
                Assert.IsTrue(_sinceLastInput.ElapsedMilliseconds >= minimumMs, $"{Scene} 操作间隔只有 {_sinceLastInput.ElapsedMilliseconds} ms");
            _sinceLastInput.Restart();
        }

        public CapturedFrame Capture(CaptureTargetWindow window, CaptureMethod method)
        {
            if (Scene == "arrival")
            {
                _arrivalFrame ??= Load("arrival-other-prompt", width);
                return _arrivalFrame.AddReference();
            }
            if (Scene == "walking" && _staleWalkFrames++ < 8)
                return _arrivalFrame!.AddReference();
            _arrivalFrame?.Dispose();
            _arrivalFrame = null;
            if (useReportedStation && Scene is "top" or "exit") return LoadReportedStation();
            if (Scene == "selected") SelectedFrames++;
            if (Scene == "middle")
            {
                var delay = _sinceLastInput.ElapsedMilliseconds;
                Console.WriteLine($"滚动后 {delay} ms 开始检查列表");
                Assert.IsLessThan(1000L, delay, "找花滚动不应等待界面切换的 1 秒间隔");
            }
            return Load(Scene switch
            {
                "start" or "walking" or "done" => "arrival-other-prompt",
                "map" or "marker" => "map-sam",
                "interact" => "sam-prompt",
                "dialogue" => "sam-dialogue",
                "selected" => useReportedStation ? "sunflower-income-9000" : "flowers-bottom-selected",
                "exit" or "top" => "flowers-top",
                "middle" => "flowers-middle",
                "confirm" => "confirm-multiple",
                _ => Scene
            }, width);
        }

        public Task SendSequenceAsync(nint hwnd, string key, KeyboardInputOptions? options, CancellationToken token)
        {
            CheckInputInterval();
            if (key == "W")
            {
                Assert.AreEqual(300, options?.HoldDurationMs);
                if (Scene == "walking") Assert.IsNull(_arrivalFrame, "停在旧画面时不应继续迈步");
            }
            Scene = (Scene, key) switch
            {
                ("start", "M") => "map",
                ("arrival", "W") => "walking",
                ("walking", "W") => "interact",
                ("interact", "F") => "dialogue",
                ("dialogue", "1") => "top",
                ("reward", "Esc") => "exit",
                ("exit", "Esc") => "done",
                _ => throw new AssertFailedException($"{Scene} 意外按键 {key}")
            };
            return key == "W" ? Task.Delay(options!.HoldDurationMs, token) : Task.CompletedTask;
        }

        public Task ScrollAsync(nint hwnd, int x, int y, int delta, KeyboardInputMethod method, CancellationToken token)
        {
            Assert.IsFalse(useReportedStation, "向阳花已经可见，应直接点击卡片。");
            CheckInputInterval(200);
            Check(x, y, 417, 568);
            Assert.AreEqual(-6 * 120, delta);
            Assert.AreEqual("top", Scene);
            Scene = "middle";
            return Task.CompletedTask;
        }

        public Task ClickAsync(nint hwnd, int x, int y, KeyboardInputMethod method, CancellationToken token)
        {
            var (next, px, py) = Scene switch
            {
                "map" => ("marker", 237, 914),
                "marker" => ("teleport-sam", 1075, 406),
                "teleport-sam" => ("arrival", 1634, 1085),
                "top" when useReportedStation => ("selected", 211, 202),
                "middle" => ("selected", 631, 752),
                "selected" when useReportedStation && y * 2048d / width < 1000 => ("selected", 211, 200),
                "selected" when y * 2048d / width < 1000 => ("selected", 631, 322),
                "selected" => ("confirm", 238, 1080),
                "confirm" => ("reward", 1186, 948),
                _ => throw new AssertFailedException($"{Scene} 意外点击")
            };
            Check(x, y, px, py);
            var repeat = Scene == "selected" && next == "selected";
            if (repeat) FlowerClickIntervals.Add(_sinceLastInput.ElapsedMilliseconds);
            CheckInputInterval(repeat ? 145 : Scene is "top" or "middle" ? 195 : 400);
            if (next == "selected")
            {
                FlowerClicks++;
                SelectedFramesAtLastFlowerClick = SelectedFrames;
            }
            Scene = next;
            // 与实际鼠标输入一样，定位等待和按压各占 50 ms。
            return next == "selected" ? Task.Delay(100, token) : Task.CompletedTask;
        }

        private void Check(int x, int y, int px, int py)
        {
            Assert.AreEqual(px, x * 2048d / width, 15, Scene);
            Assert.AreEqual(py, y * 2048d / width, 15, Scene);
        }

        public bool IsWindowAvailable(nint hwnd) => true;
        public bool IsWindowForeground(nint hwnd) => true;
        public bool RequiresForeground(KeyboardInputMethod method) => true;
        public void EnsureReady(KeyboardInputMethod method) { }
        public void Release(CaptureTargetWindow window, CaptureMethod method) { }
        public bool TryParseSequence(string sequence, out IReadOnlyList<KeyStroke> keys, out string error) => throw new NotSupportedException();
        public Task SendSequenceAsync(nint hwnd, IReadOnlyList<KeyStroke> keys, KeyboardInputOptions? options, CancellationToken token) => throw new NotSupportedException();
        public void LookDown(nint hwnd, KeyboardInputMethod method) => throw new NotSupportedException();
        public Task PressAtCurrentPositionAsync(nint hwnd, int holdMs, KeyboardInputMethod method, CancellationToken token) => throw new NotSupportedException();
        public Task DragAsync(nint hwnd, int x, int y, int endX, int endY, KeyboardInputMethod method, CancellationToken token) => throw new NotSupportedException();
        public Task ScrollAtCurrentPositionAsync(nint hwnd, int delta, KeyboardInputMethod method, CancellationToken token) => throw new NotSupportedException();
    }
}
