using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using RocoPilot.Configuration;
using RocoPilot.Contracts.Services;
using RocoPilot.Models.Capture;
using RocoPilot.Models.Recognition;
using RocoPilot.Models.Runtime;
using RocoPilot.Services;
using RocoPilot.Services.ImageMatching;
using RocoPilot.Services.RuntimeTasks;
using RocoPilot.Services.TextRecognition;
using RocoPilot.Services.TextRecognition.Backends;

namespace RocoPilot.Tests;

[TestClass]
[TestCategory("LocalScreenshots")]
public sealed class ShinyReplayTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    [DataRow("battle-transform", false, RecognitionRegionIds.BattleShinyTip)]
    [DataRow("battle-transform", false, RecognitionRegionIds.BattleBloodlineTip)]
    [DataRow("battle-discovery", false, RecognitionRegionIds.BattleShinyTip)]
    [DataRow("video-chess", true, RecognitionRegionIds.BattleShinyTip)]
    public async Task ReplaysActualRegion(string name, bool video, string regionId)
    {
        var root = Root();
        using var frame = FlowerSeedScreenRecognizerTests.LoadFixture(Path.Combine(root, "RocoPilot.Tests", "LocalScreenshots", "2026-11-16", "shiny", name + ".png"));
        var config = JsonSerializer.Deserialize<RecognitionRegionConfig>(
            File.ReadAllText(Path.Combine(root, "RocoPilot", "Configuration", "RecognitionRegions", "2048x1152.json")),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
        var window = video
            ? new CaptureTargetWindow { ClientWidth = 1206, ClientHeight = 539, ClientOffsetY = 739 }
            : new CaptureTargetWindow { ClientWidth = 2048, ClientHeight = 1152, ClientOffsetX = 1, ClientOffsetY = 31 };
        var state = new RuntimeTaskState(window, config, new RuntimeTaskStartOptions(), DateTimeOffset.Now);
        using var backend = new OnnxOcrSingleLineTextRecognitionBackend();
        var recognizer = new RuntimeFrameRecognizer(new ImageMatchingService(),
            new TextRecognitionService([new OnnxOcrTextRecognitionBackend(backend)]), new Overlay());
        var parse = typeof(RuntimeTaskService).GetMethod("IsShinyTip", BindingFlags.Static | BindingFlags.NonPublic)!;
        await backend.PrewarmAsync();
        var times = new List<double>();
        var hits = 0;
        string text = "";
        for (var i = 0; i < 25; i++)
        {
            var start = Stopwatch.GetTimestamp();
            text = await recognizer.RecognizeRegionTextAsync(state, frame, [regionId], default);
            times.Add(Stopwatch.GetElapsedTime(start).TotalMilliseconds);
            if ((bool)parse.Invoke(null, [text])!) hits++;
        }
        times.Sort();
        TestContext.WriteLine($"{name}/{regionId}: hits={hits}/25; median={times[12]:F2} ms; p95={times[23]:F2} ms; text={text}");
        if (!video)
        {
            Assert.AreEqual(25, hits, $"OCR 结果：{text}");
            if (name == "battle-transform")
            {
                StringAssert.Contains(text, "对面");
                StringAssert.Contains(text, "异色精灵");
            }
        }
        // 第三方视频只能记录映射结果，无法推断原生游戏窗口尺寸。
        // 对照仅定位视频截图中的文字，生产配置不变。
        if (video)
        {
            var region = config.Regions.Single(r => r.Id == regionId);
            config.ResolutionWidth = frame.Width;
            config.ResolutionHeight = frame.Height;
            region.X = 895;
            region.Y = 829;
            region.Width = 155;
            region.Height = 33;
            state = new RuntimeTaskState(new CaptureTargetWindow { ClientWidth = frame.Width, ClientHeight = frame.Height },
                config, new RuntimeTaskStartOptions(), DateTimeOffset.Now);
            times.Clear();
            hits = 0;
            for (var i = 0; i < 25; i++)
            {
                var start = Stopwatch.GetTimestamp();
                text = await recognizer.RecognizeRegionTextAsync(state, frame, [regionId], default);
                times.Add(Stopwatch.GetElapsedTime(start).TotalMilliseconds);
                if ((bool)parse.Invoke(null, [text])!) hits++;
            }
            times.Sort();
            TestContext.WriteLine($"video text control: hits={hits}/25; median={times[12]:F2} ms; p95={times[23]:F2} ms; text={text}");
            Assert.AreEqual(25, hits);
        }
    }

    private static string Root([System.Runtime.CompilerServices.CallerFilePath] string path = "") =>
        Path.GetDirectoryName(Path.GetDirectoryName(path))!;

    private sealed class Overlay : IRecognitionOverlayService
    {
        public void Show(RuntimeTaskState state, RecognitionRegionConfig? config = null) { }
        public void ShowOcrResult(string id, string text) { }
        public void ShowImageMatchResult(string id, double score) { }
        public void Hide() { }
    }
}
