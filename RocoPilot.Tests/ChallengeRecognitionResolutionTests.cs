using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using OpenCvSharp;
using RocoPilot.Contracts.Services;
using RocoPilot.Models.Capture;
using RocoPilot.Models.Recognition;
using RocoPilot.Models.Runtime;
using RocoPilot.Services.ImageMatching;
using RocoPilot.Services.IndependentTasks;
using RocoPilot.Services.RuntimeTasks;

namespace RocoPilot.Tests;

[TestClass]
[TestCategory("LocalScreenshots")]
public sealed class ChallengeRecognitionResolutionTests
{
    public static IEnumerable<object[]> Clients()
    {
        foreach (var (width, height) in new[]
        {
            (960, 540), (1024, 576), (1280, 720), (1600, 900),
            (1920, 1080), (2048, 1152), (2560, 1440),
            // 非 16:9 是等比缩放并按顶中／顶右锚定的合成布局，不是原生实拍。
            (1920, 1440), (3440, 1440)
        })
        foreach (var offset in new[] { 0, 12 })
            yield return [width, height, offset, offset == 0 ? 0 : 30];
    }

    [TestMethod]
    [DynamicData(nameof(Clients))]
    public async Task CondensedMarkDistinguishesSelectedAndContract(int width, int height, int x, int y)
    {
        var recognizer = new ManualChallengeScreenRecognizer(new ImageMatchingService(), null!, null!, null!);
        foreach (var (file, selected) in new[] { ("flower-seed-capture.png", true), ("flower-seed-capture-duck.png", false) })
        {
            using var frame = LoadFrame(file, width, height, x, y, rightAligned: true);
            var result = await recognizer.ReadCondensedMarkAsync(frame, x, y, width, height, CancellationToken.None);
            Assert.AreEqual((selected, false), result, file);
        }
    }

    [TestMethod]
    [DynamicData(nameof(Clients))]
    public async Task CondensedShortageTemplateIsFoundAtScaledTopCenter(int width, int height, int x, int y)
    {
        // 合成提示用于验证真实匹配器的缩放与搜索区；不能替代不足弹窗实拍。
        var images = new ImageMatchingService();
        using var prompt = Cv2.ImRead(Path.Combine(images.TemplateDirectory,
            "2048x1152/flower-seed/condensed-insufficient.png"));
        foreach (var inSearchArea in new[] { true, false })
        {
            using var frame = LoadFrame("flower-seed-capture.png", width, height, x, y, transform: source =>
            {
                source.SetTo(Scalar.Black);
                using var destination = new Mat(source, new Rect(714, inSearchArea ? 150 : 700, prompt.Width, prompt.Height));
                prompt.CopyTo(destination);
            });
            var recognizer = new ManualChallengeScreenRecognizer(images, null!, null!, null!);
            var result = await recognizer.ReadCondensedMarkAsync(frame, x, y, width, height, CancellationToken.None);
            Assert.AreEqual((false, inSearchArea), result);
        }
    }

    [TestMethod]
    [DynamicData(nameof(Clients))]
    public async Task CondensedShortageRealScreenshot(int width, int height, int x, int y)
    {
        using var frame = LoadFrame("flower-seed-condensed-insufficient.png", width, height, x, y);
        var recognizer = new ManualChallengeScreenRecognizer(new ImageMatchingService(), null!, null!, null!);
        Assert.IsTrue((await recognizer.ReadCondensedMarkAsync(frame, x, y, width, height, CancellationToken.None)).Insufficient);
    }

    [TestMethod]
    [DynamicData(nameof(Clients))]
    public async Task SkipTurnArrowRequiresSecondQueueSlot(int width, int height, int x, int y)
    {
        var state = CreateState(width, height, x, y);
        var recognizer = CreateBattleRecognizer();
        foreach (var file in new[] { "flower-seed-battle.png", "legendary-battle.png", "flower-seed-battle-snow.png" })
        for (var slot = 0; slot < 5; slot++)
        {
            // 首位为实拍；其余状态只移动实拍箭头，slot 4 表示移除箭头。
            using var frame = LoadFrame(file, width, height, x, y, transform: source =>
            {
                if (slot == 0) return;
                var arrowArea = new Rect(936, 174, 30, 28);
                using var arrow = new Mat(source, arrowArea).Clone();
                Cv2.Rectangle(source, arrowArea, Scalar.Black, -1);
                if (slot == 4) return;
                using var destination = new Mat(source, new Rect(936 + slot * 77, 174, 30, 28));
                arrow.CopyTo(destination);
            }, sourceArea: file == "flower-seed-battle-snow.png" ? new Rect(1, 31, 1600, 900) : null);
            var matched = await recognizer.IsMyTurnAsync(state, frame, 1, CancellationToken.None);
            Assert.AreEqual(slot == 1, matched, $"{file}，箭头位置 {slot + 1}（5 为无箭头）");
        }
    }

    [TestMethod]
    [DynamicData(nameof(Clients))]
    public async Task SkipTurnSecondArrowRealScreenshot(int width, int height, int x, int y)
    {
        using var frame = LoadFrame("flower-seed-skip-turn-second.png", width, height, x, y);
        Assert.IsTrue(await CreateBattleRecognizer().IsMyTurnAsync(CreateState(width, height, x, y), frame, 1, CancellationToken.None));
    }

    [TestMethod]
    [DataRow("flower-seed-skip-turn-first-4x3.png", false)]
    [DataRow("flower-seed-skip-turn-second-4x3.png", true)]
    public async Task NativeFourByThreeArrowUsesItsOwnConfiguration(string file, bool expected)
    {
        using var frame = FlowerSeedScreenRecognizerTests.LoadFixture(file, clientX: 12, clientY: 30);
        Assert.AreEqual((1932, 1470), (frame.Width, frame.Height));
        var state = CreateState(1920, 1440, 12, 30, "1920x1440");
        Assert.AreEqual(expected, await CreateBattleRecognizer().IsMyTurnAsync(state, frame, 1, CancellationToken.None));
    }

    [TestMethod]
    [DynamicData(nameof(Clients))]
    public void SkipTurnQueueIgnoresSceneChangesAndDetectsChangedSkill(int width, int height, int x, int y)
    {
        var state = CreateState(width, height, x, y);
        var recognizer = CreateBattleRecognizer();
        using var baselineFrame = LoadFrame("flower-seed-battle.png", width, height, x, y);
        var baseline = recognizer.CaptureSkillQueueAnchor(state, baselineFrame, CancellationToken.None);
        Assert.IsNotNull(baseline);
        foreach (var (file, changed) in new[]
        {
            ("flower-seed-battle.png", false),
            ("flower-seed-battle-detail.png", false), // 场景和详情变化，但首位图标相同。
            ("legendary-battle.png", true) // 首位技能图标不同。
        })
        {
            using var frame = LoadFrame(file, width, height, x, y);
            Assert.AreEqual(changed, recognizer.HasSkillQueueChanged(state, frame, baseline, CancellationToken.None), file);
        }
        Assert.IsFalse(recognizer.HasSkillQueueChanged(state, baselineFrame, null, CancellationToken.None));
    }

    private static BattleScreenRecognizer CreateBattleRecognizer() =>
        new(new RuntimeFrameRecognizer(new ImageMatchingService(), null!, new Overlay()));

    private sealed class Overlay : IRecognitionOverlayService
    {
        public void Show(RuntimeTaskState state, RecognitionRegionConfig? regionConfig = null) { }
        public void ShowOcrResult(string regionId, string text) { }
        public void ShowImageMatchResult(string regionId, double score) { }
        public void Hide() { }
    }

    private static RuntimeTaskState CreateState(int width, int height, int x, int y, string reference = "2048x1152")
    {
        var config = JsonSerializer.Deserialize<RecognitionRegionConfig>(File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory, $"Configuration/RecognitionRegions/{reference}.json")),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
        return new(new CaptureTargetWindow
        {
            ClientWidth = width, ClientHeight = height, ClientOffsetX = x, ClientOffsetY = y
        }, config, new RuntimeTaskStartOptions(), DateTimeOffset.UtcNow);
    }

    private static CapturedFrame LoadFrame(string file, int width, int height, int x, int y,
        bool rightAligned = false, Action<Mat>? transform = null, Rect? sourceArea = null)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", file);
        if (!File.Exists(path)) Assert.Inconclusive($"缺少本地截图素材：{file}，见 SCREENSHOT_TESTING.md。");
        using var original = Cv2.ImRead(path);
        using var source = sourceArea is { } area ? new Mat(original, area).Clone() : original.Clone();
        if (sourceArea is not null) Cv2.Resize(source, source, new Size(2048, 1152), interpolation: InterpolationFlags.Area);
        Assert.AreEqual(new Size(2048, 1152), source.Size(), file);
        transform?.Invoke(source);
        var scale = Math.Min(width / 2048d, height / 1152d);
        using var resized = new Mat();
        Cv2.Resize(source, resized, new Size((int)Math.Round(2048 * scale), (int)Math.Round(1152 * scale)),
            interpolation: InterpolationFlags.Area);
        using var canvas = new Mat(height + y, width + x, MatType.CV_8UC3, Scalar.Black);
        using (var destination = new Mat(canvas, new Rect(x + (width - resized.Width) / (rightAligned ? 1 : 2),
            y, resized.Width, resized.Height))) resized.CopyTo(destination);
        using var bgra = new Mat();
        Cv2.CvtColor(canvas, bgra, ColorConversionCodes.BGR2BGRA);
        var pixels = new byte[bgra.Width * bgra.Height * 4];
        Marshal.Copy(bgra.Data, pixels, 0, pixels.Length);
        return new(bgra.Width, bgra.Height, pixels);
    }
}
