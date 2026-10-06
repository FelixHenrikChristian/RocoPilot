using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using RocoPilot.Contracts.Services;
using RocoPilot.Contracts.Services.Capture;
using RocoPilot.Contracts.Services.ImageMatching;
using RocoPilot.Contracts.Services.TextRecognition;
using RocoPilot.Models.Capture;
using RocoPilot.Models.ImageMatching;
using RocoPilot.Models.Input;
using RocoPilot.Models.Recognition;
using RocoPilot.Models.Runtime;
using RocoPilot.Models.TextRecognition;
using RocoPilot.Services.IndependentTasks;

namespace RocoPilot.Tests;

[TestClass]
public sealed class FlowerSeedChallengeRunnerTests
{
    [TestMethod]
    public async Task ScanningProgressUsesNumberedFlowersAndStableOperations()
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var capture = new FreshListCapture();
        var scrolls = 0;
        var mouse = new Mouse { DuringScroll = () => { if (++scrolls >= 3) capture.Scene = 7; } };
        var runner = CreateRunner(capture, new Images(), new Keyboard(), mouse,
            ocr: new InteractionOcr { Text = string.Empty });
        List<IndependentTaskProgress> updates = [];

        var options = await runner.ScanAsync(CreateState(), updates.Add, cancellation.Token);

        Assert.HasCount(3, options);
        Assert.IsTrue(updates.Any(update => update.Recognition == "正在定位列表顶部"));
        Assert.IsTrue(updates.Any(update => update.Recognition == "当前花种：1. 未识别名称、2. 未识别名称"));
        Assert.IsTrue(updates.Any(update => update.Recognition == "当前花种：2. 未识别名称、3. 未识别名称"));
        Assert.IsTrue(updates.All(update => !update.Recognition.Contains("OCR", StringComparison.Ordinal)));
        Assert.IsTrue(updates.All(update => update.Operation is "回到花种列表顶部" or "向下滚动花种列表" or "扫描完成，共3个花种"));
        Assert.AreEqual("扫描完成，共3个花种", updates[^1].Operation);
    }

    [TestMethod]
    public async Task RepeatedWgcFrameIsRecognizedOnlyOnce()
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var capture = new CaptureStub(cancellation, 2);
        var images = new Images();
        var keyboard = new Keyboard();
        var mouse = new Mouse();
        var runner = CreateRunner(capture, images, keyboard, mouse);

        await Assert.ThrowsAsync<OperationCanceledException>(() => runner.RunAsync(CreateState(), new FlowerSeedOption(1, "友爱星飞"), _ => { }, cancellation.Token));

        Assert.AreEqual(1, images.FramesRead);
        Assert.AreEqual(0, keyboard.Keys.Count);
        Assert.AreEqual(0, mouse.Clicks);
    }

    [TestMethod]
    public async Task FrameCapturedDuringInputCannotTriggerTheNextClickAndKeyboardUsesConfiguredHold()
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var capture = new CaptureStub(cancellation, 0);
        var images = new Images();
        var keyboard = new Keyboard { DuringKey = () => capture.Show(1) };
        var mouse = new Mouse();
        var runner = CreateRunner(capture, images, keyboard, mouse);

        await Assert.ThrowsAsync<OperationCanceledException>(() => runner.RunAsync(CreateState(), new FlowerSeedOption(1, "友爱星飞"), _ => { }, cancellation.Token));

        CollectionAssert.AreEqual(new[] { "F3" }, keyboard.Keys.Select(key => key.Key).ToArray());
        Assert.AreEqual(175, keyboard.Keys[0].Options.HoldDurationMs);
        Assert.AreEqual(KeyboardInputMethod.Interception, keyboard.Keys[0].Options.Method);
        Assert.AreEqual(1, images.FramesRead);
        Assert.AreEqual(0, mouse.Clicks);
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task OcrOverlayUsesClientRelativeRegionsAndOnlyPublishesWhenEnabled(bool enabled)
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var capture = new CaptureStub(cancellation, 3, 108, 76);
        var overlay = new Overlay();
        var ocr = new InteractionOcr();
        var runner = CreateRunner(capture, new Images(), new Keyboard(), new Mouse(), overlay, ocr);

        await Assert.ThrowsAsync<OperationCanceledException>(() => runner.RunAsync(
            CreateState(enabled, hasBorder: true), new FlowerSeedOption(1, "友爱星飞"), _ => { }, cancellation.Token));

        Assert.AreEqual(1, ocr.Calls);
        Assert.AreEqual(1, overlay.Hides);
        if (!enabled)
        {
            Assert.HasCount(0, overlay.Shown);
            Assert.HasCount(0, overlay.Results);
            return;
        }
        Assert.HasCount(2, overlay.Shown);
        Assert.HasCount(1, overlay.Results);
        var config = overlay.Shown[0];
        Assert.AreEqual((96, 54), (config.ResolutionWidth, config.ResolutionHeight));
        Assert.HasCount(1, config.Regions);
        var region = config.Regions[0];
        Assert.AreEqual((49, 20, 7, 2), (region.X, region.Y, region.Width, region.Height));
        Assert.AreEqual(("花种交互选项", "OCR：你好"), overlay.Results[0]);
        Assert.AreEqual(region.Id, overlay.Results[0].Id);
        Assert.HasCount(0, overlay.Shown[1].Regions);
        Assert.AreEqual((96, 54), (overlay.Shown[1].ResolutionWidth, overlay.Shown[1].ResolutionHeight));
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task ScrollingClearsThePreviousOcrOverlayAndKeepsGlobalNumbersWhenDisplayWasDisabled(bool initiallyEnabled)
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var capture = new FreshListCapture();
        var state = CreateState(overlayEnabled: initiallyEnabled);
        var images = new Images();
        var overlay = new Overlay();
        var ocr = new InteractionOcr { Text = string.Empty };
        var scrolls = 0;
        var mouse = new Mouse
        {
            DuringScroll = () =>
            {
                scrolls++;
                if (state.Options.RecognitionOverlayEnabled)
                    Assert.HasCount(0, overlay.Shown[^1].Regions);
                if (scrolls < 3) return;
                capture.Scene = 7;
                state.Options.RecognitionOverlayEnabled = true;
            }
        };
        overlay.DuringShow = config =>
        {
            if (config.Regions.Count > 0 && config.Regions[0].Y == 28) cancellation.Cancel();
        };
        var runner = CreateRunner(capture, images, new Keyboard(), mouse, overlay, ocr);

        await Assert.ThrowsAsync<OperationCanceledException>(() => runner.ScanAsync(state, _ => { }, cancellation.Token));

        Assert.AreEqual(3, scrolls);
        Assert.AreEqual(images.FramesRead * 2, ocr.Calls);
        var visible = overlay.Shown.Where(config => config.Regions.Count > 0).ToArray();
        Assert.HasCount(initiallyEnabled ? 2 : 1, visible);
        if (initiallyEnabled)
        {
            CollectionAssert.AreEqual(new[] { "花种 1", "花种 2" }, visible[0].Regions.Select(region => region.Id).ToArray());
            CollectionAssert.AreEqual(new[] { 19, 32 }, visible[0].Regions.Select(region => region.Y).ToArray());
        }
        CollectionAssert.AreEqual(new[] { "花种 2", "花种 3" }, visible[^1].Regions.Select(region => region.Id).ToArray());
        CollectionAssert.AreEqual(new[] { 28, 41 }, visible[^1].Regions.Select(region => region.Y).ToArray());
        Assert.IsTrue(visible[^1].Regions.All(region => !region.IsMatched));
        Assert.IsTrue(overlay.Results.All(result => result.Text == "OCR：无文本 → 图鉴：未匹配"));
        Assert.AreEqual(1, overlay.Hides);
    }

    [TestMethod]
    public async Task DisablingOcrOverlayDuringRecognitionStopsPublishing()
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var capture = new CaptureStub(cancellation, 3, 108, 76);
        var state = CreateState(overlayEnabled: true, hasBorder: true);
        var overlay = new Overlay();
        var ocr = new InteractionOcr { DuringRead = () => state.Options.RecognitionOverlayEnabled = false };
        var runner = CreateRunner(capture, new Images(), new Keyboard(), new Mouse(), overlay, ocr);

        await Assert.ThrowsAsync<OperationCanceledException>(() => runner.RunAsync(
            state, new FlowerSeedOption(1, "友爱星飞"), _ => { }, cancellation.Token));

        Assert.AreEqual(1, ocr.Calls);
        Assert.HasCount(0, overlay.Shown);
        Assert.HasCount(0, overlay.Results);
        Assert.AreEqual(1, overlay.Hides);
    }

    private static FlowerSeedChallengeRunner CreateRunner(IScreenCaptureService capture, Images images, Keyboard keyboard, Mouse mouse,
        Overlay? overlay = null, ITextRecognitionService? ocr = null)
        => new(capture, new Window(), keyboard, mouse,
            new FlowerSeedScreenRecognizer(images, ocr ?? new UnusedOcr(), null!, null!, NullLogger<FlowerSeedScreenRecognizer>.Instance),
            overlay ?? new Overlay(),
            NullLogger<FlowerSeedChallengeRunner>.Instance);

    private static RuntimeTaskState CreateState(bool overlayEnabled = false, bool hasBorder = false) => new(new CaptureTargetWindow
    {
        Hwnd = (nint)1, ClientWidth = 96, ClientHeight = 54,
        Width = hasBorder ? 108 : 96, Height = hasBorder ? 76 : 54,
        WindowClientOffsetX = hasBorder ? 6 : 0, WindowClientOffsetY = hasBorder ? 22 : 0
    }, new RecognitionRegionConfig(), new RuntimeTaskStartOptions
    {
        CaptureMethod = CaptureMethod.WindowsGraphicsCapture,
        RecognitionOverlayEnabled = overlayEnabled,
        AutoBattleSettings = new() { KeyboardInputMethod = KeyboardInputMethod.Interception, KeyboardHoldDurationMs = 175 }
    }, DateTimeOffset.UtcNow);

    private sealed class CaptureStub(CancellationTokenSource cancellation, byte initialScene, int width = 96, int height = 54)
        : IScreenCaptureService, IDisposable
    {
        private CapturedFrame? _cached;
        private int _calls;
        public byte? NextScene;
        public CapturedFrame? Capture(CaptureTargetWindow targetWindow, CaptureMethod method)
        {
            if (_cached is null) Show(initialScene);
            if (NextScene is { } scene)
            {
                Show(scene);
                NextScene = null;
            }
            if (++_calls >= 4) cancellation.Cancel();
            return _cached!.AddReference();
        }
        public void Show(byte scene)
        {
            _cached?.Dispose();
            var pixels = new byte[width * height * 4];
            if (scene is 3 or 4 or 5) Array.Fill(pixels, (byte)245);
            pixels[0] = scene;
            _cached = new CapturedFrame(width, height, pixels);
        }
        public void Release(CaptureTargetWindow targetWindow, CaptureMethod method) { }
        public void Dispose() => _cached?.Dispose();
    }

    private sealed class FreshListCapture : IScreenCaptureService
    {
        public byte Scene = 6;
        public CapturedFrame Capture(CaptureTargetWindow targetWindow, CaptureMethod method)
        {
            var pixels = new byte[96 * 54 * 4];
            Array.Fill(pixels, (byte)245);
            pixels[0] = Scene;
            return new(96, 54, pixels);
        }
        public void Release(CaptureTargetWindow targetWindow, CaptureMethod method) { }
    }

    private sealed class Images : IImageMatchingService
    {
        public int FramesRead;
        public ImageMatchAlgorithm DefaultAlgorithm => ImageMatchAlgorithm.OpenCvSqDiffNormalized;
        public string TemplateDirectory => "";
        public IReadOnlyList<string> ListTemplatePaths() => [];
        public Task<ImageMatchResult> MatchAsync(CapturedFrame frame, RecognitionRegion region, string templatePath,
            ImageMatchOptions? options = null, CancellationToken cancellationToken = default)
        {
            if (templatePath.EndsWith("/flower-tab.png", StringComparison.Ordinal)) FramesRead++;
            if (frame.Pixels[0] is 4 or 5 or 6 or 7 && templatePath.EndsWith("/flower-tab.png", StringComparison.Ordinal))
                return Task.FromResult(new ImageMatchResult(true, 1, 24, 3, 6, 2, templatePath));
            if (frame.Pixels[0] == 3 && templatePath.EndsWith("/interaction-f.png", StringComparison.Ordinal))
                return Task.FromResult(new ImageMatchResult(true, 1, 51, 42, 2, 2, templatePath));
            var matched = frame.Pixels[0] == 0 && templatePath.EndsWith("/magic-point.png", StringComparison.Ordinal)
                || frame.Pixels[0] == 1 && templatePath.EndsWith("/manual-header.png", StringComparison.Ordinal);
            return Task.FromResult(matched ? new ImageMatchResult(true, 1, 10, 1, 2, 2, templatePath) : ImageMatchResult.NoMatch(0, templatePath));
        }
        public Task<ImageMatchCollectionResult> FindMatchesAsync(CapturedFrame frame, RecognitionRegion region,
            string templatePath, int maximumMatches, ImageMatchOptions? options = null,
            double maximumOverlapRatio = .5, CancellationToken cancellationToken = default)
            => Task.FromResult(frame.Pixels[0] switch
            {
                4 or 5 => new ImageMatchCollectionResult([new(true, 1, 80, frame.Pixels[0] == 4 ? 20 : 26, 10, 4, templatePath)], 1, templatePath),
                6 or 7 => new ImageMatchCollectionResult(Enumerable.Range(0, 2).Select(index => new ImageMatchResult(
                    true, 1, 80, (frame.Pixels[0] == 6 ? 20 : 29) + index * 13, 10, 4, templatePath)).ToArray(), 1, templatePath),
                _ => ImageMatchCollectionResult.NoMatch(0, templatePath)
            });
    }

    private sealed class Keyboard : IKeyboardInputService
    {
        public Action? DuringKey;
        public List<(string Key, KeyboardInputOptions Options)> Keys { get; } = [];
        public bool IsWindowAvailable(nint hwnd) => true;
        public bool IsWindowForeground(nint hwnd) => true;
        public bool RequiresForeground(KeyboardInputMethod method) => true;
        public void EnsureReady(KeyboardInputMethod method) { }
        public bool TryParseSequence(string sequence, out IReadOnlyList<KeyStroke> keyStrokes, out string error)
        { keyStrokes = []; error = ""; return true; }
        public async Task SendSequenceAsync(nint hwnd, string sequence, KeyboardInputOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            Keys.Add((sequence, options!));
            DuringKey?.Invoke();
            await Task.Delay(10, cancellationToken);
        }
        public Task SendSequenceAsync(nint hwnd, IReadOnlyList<KeyStroke> keyStrokes, KeyboardInputOptions? options = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class Mouse : IMouseInputService
    {
        public int Clicks;
        public Action? DuringScroll;
        public Task ClickAsync(nint hwnd, int x, int y, KeyboardInputMethod method = KeyboardInputMethod.SendInput,
            CancellationToken cancellationToken = default) { Clicks++; return Task.CompletedTask; }
        public Task ScrollAsync(nint hwnd, int x, int y, int delta, KeyboardInputMethod method = KeyboardInputMethod.SendInput,
            CancellationToken cancellationToken = default) { DuringScroll?.Invoke(); return Task.CompletedTask; }
        public Task ScrollAtCurrentPositionAsync(nint hwnd, int delta, KeyboardInputMethod method = KeyboardInputMethod.SendInput,
            CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class Window : IGameWindowService
    {
        public string TargetProcessName => "game";
        public CaptureTargetWindow? FindGameWindow() => null;
        public bool TryBringGameWindowToForeground(CaptureTargetWindow window) => true;
    }

    private sealed class Overlay : IRecognitionOverlayService
    {
        public List<RecognitionRegionConfig> Shown { get; } = [];
        public List<(string Id, string Text)> Results { get; } = [];
        public int Hides;
        public Action<RecognitionRegionConfig>? DuringShow;
        public void Show(RuntimeTaskState state, RecognitionRegionConfig? regionConfig = null)
        {
            Shown.Add(regionConfig!);
            DuringShow?.Invoke(regionConfig!);
        }
        public void ShowOcrResult(string regionId, string text) => Results.Add((regionId, text));
        public void ShowImageMatchResult(string regionId, double score) => throw new NotSupportedException();
        public void Hide() => Hides++;
    }

    private sealed class InteractionOcr : ITextRecognitionService
    {
        public int Calls;
        public string Text = "你好";
        public Action? DuringRead;
        public IReadOnlyList<TextRecognitionMethodOption> GetMethods() => [];
        public TextRecognitionMethodOption? GetDefaultMethod() => null;
        public Task<TextRecognitionResult> RecognizeAsync(byte[] imageBytes, TextRecognitionMethod method,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<TextRecognitionResult> RecognizeAsync(CapturedFrame frame, RecognitionRegion region,
            TextRecognitionMethod method, CancellationToken cancellationToken = default)
        {
            Calls++;
            DuringRead?.Invoke();
            return Task.FromResult(new TextRecognitionResult(method, "ONNX", null, [Text], 1));
        }
    }

    private sealed class UnusedOcr : ITextRecognitionService
    {
        public IReadOnlyList<TextRecognitionMethodOption> GetMethods() => [new(TextRecognitionMethod.OnnxOcrV5, "ONNX", "", true)];
        public TextRecognitionMethodOption? GetDefaultMethod() => GetMethods()[0];
        public Task<TextRecognitionResult> RecognizeAsync(byte[] imageBytes, TextRecognitionMethod method,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<TextRecognitionResult> RecognizeAsync(CapturedFrame frame, RecognitionRegion region, TextRecognitionMethod method,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
