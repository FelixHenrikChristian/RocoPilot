using System.Diagnostics;
using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using OpenCvSharp;
using RocoPilot.Models.Capture;
using RocoPilot.Services.TextRecognition;
using RocoPilot.Services.TextRecognition.Backends;

namespace RocoPilot.Tests;

[TestClass]
[TestCategory("LocalScreenshots")]
public sealed class BattlePassTextLocatorTests
{
    [TestMethod]
    [DataRow(2048, 0)]
    [DataRow(1280, 0)]
    [DataRow(2048, 1)]
    [DataRow(1280, 1)]
    [DataRow(2048, 2)]
    [DataRow(1280, 2)]
    public async Task FindsMovedTextAndRejectsMissingOrAmbiguousText(int width, int mode)
    {
        using var backend = new OnnxOcrV5SingleLineTextRecognitionBackend();
        var ocr = new TextRecognitionService([new OnnxOcrV5TextRecognitionBackend(backend)]);
        using var locator = new OnnxTextLocator(ocr);
        using var frame = FlowerSeedScreenRecognizerTests.LoadFixture(Path.Combine(BattlePassTests.Folder(), "season-menu.png"),
            width, width * 9 / 16, transform: image =>
            {
                using var original = new Mat(image, new Rect(176, 458, 94, 42));
                using var label = original.Clone();
                if (mode != 2) original.SetTo(new Scalar(40, 10, 20));
                if (mode == 0) return;
                using var destination = new Mat(image, new Rect(1400, 200, label.Width, label.Height));
                label.CopyTo(destination);
            });
        var window = new CaptureTargetWindow { ClientWidth = width, ClientHeight = width * 9 / 16 };
        var hit = await locator.FindAsync(frame, window, "通行证", default);
        if (mode != 1) { Assert.IsNull(hit); return; }
        Assert.IsNotNull(hit);
        Assert.AreEqual(1447, (hit.X + hit.Width / 2d) * 2048 / width, 10);
        Assert.AreEqual(221, (hit.Y + hit.Height / 2d) * 2048 / width, 10);
    }

    [TestMethod]
    public async Task DoesNotTreatTaskPageTitleAsAnEntry()
    {
        using var backend = new OnnxOcrV5SingleLineTextRecognitionBackend();
        var ocr = new TextRecognitionService([new OnnxOcrV5TextRecognitionBackend(backend)]);
        using var locator = new OnnxTextLocator(ocr);
        foreach (var name in new[] { "tasks-three-ready", "tasks-claimed", "reward-popup", "level-popup" })
        {
            using var frame = FlowerSeedScreenRecognizerTests.LoadFixture(Path.Combine(BattlePassTests.Folder(), name + ".png"));
            Assert.IsNull(await locator.FindAsync(frame, new() { ClientWidth = frame.Width, ClientHeight = frame.Height },
                "通行证", default), name);
        }
    }

    [TestMethod]
    public async Task FindsExactTextAcrossSeasonsAndResolutions()
    {
        using var backend = new OnnxOcrV5SingleLineTextRecognitionBackend();
        var ocr = new TextRecognitionService([new OnnxOcrV5TextRecognitionBackend(backend)]);
        using var locator = new OnnxTextLocator(ocr);
        var rows = new List<object>();
        var misses = new List<string>();
        foreach (var name in new[] { "season-menu", "s4-season-menu-video-reference", "s2-season-menu-watermarked-reference" })
        {
            var path = Path.Combine(BattlePassTests.Folder(), name + ".png");
            if (!File.Exists(path)) Assert.Inconclusive("缺少本地参考图");
            using var original = Cv2.ImRead(path);
            foreach (var width in new[] { original.Width, 2048, 1920, 1600, 1280, 960 }.Distinct())
            {
                var height = (int)Math.Round(original.Height * width / (double)original.Width);
                using var frame = FlowerSeedScreenRecognizerTests.LoadFixture(path, width, height, 12, 30);
                var window = new CaptureTargetWindow { ClientWidth = width, ClientHeight = height, ClientOffsetX = 12, ClientOffsetY = 30 };
                var timer = Stopwatch.StartNew();
                var hit = await locator.FindAsync(frame, window, "通行证", default);
                var expected = name switch { "season-menu" => new Rect(175, 456, 95, 45), "s4-season-menu-video-reference" => new Rect(1985, 435, 180, 65), _ => new Rect(262, 286, 135, 54) };
                var scale = width / (double)original.Width;
                var cx = hit is null ? -1 : hit.X + hit.Width / 2d - 12;
                var cy = hit is null ? -1 : hit.Y + hit.Height / 2d - 30;
                var valid = hit is not null && expected.Contains(new Point((int)(cx / scale), (int)(cy / scale)));
                if (!valid) misses.Add($"{name}/{width}");
                rows.Add(new { name, width, height, valid, x = cx, y = cy, ms = timer.ElapsedMilliseconds });
                Console.WriteLine($"{name} {width}x{height}: {valid}, ({cx:F1},{cy:F1}), {timer.ElapsedMilliseconds}ms");
            }
        }
        File.WriteAllText(Path.Combine(BattlePassTests.Folder(), "onnx-full-image-ocr.json"), JsonSerializer.Serialize(rows, new JsonSerializerOptions { WriteIndented = true }));
        Assert.IsEmpty(misses, string.Join(", ", misses));
    }
}
