using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using RocoPilot.Contracts.Services;
using RocoPilot.Contracts.Services.Capture;
using RocoPilot.Contracts.Services.Recognition;
using RocoPilot.Contracts.Services.TextRecognition;
using RocoPilot.Models.Capture;
using RocoPilot.Models.Recognition;
using RocoPilot.Models.TextRecognition;
using RocoPilot.Services.Statistics;

namespace RocoPilot.Tests;

[TestClass]
public sealed class StatisticsUidDetectionServiceTests
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task RecognizesMappedFrameRegionAndReleasesFrames(bool windowFrame)
    {
        var capture = new CaptureStub(windowFrame);
        var ocr = new OcrStub(capture, windowFrame);
        var service = new StatisticsUidDetectionService(new WindowStub(), capture, new ConfigStub(), ocr,
            NullLogger<StatisticsUidDetectionService>.Instance);

        var result = await service.DetectAsync(CaptureMethod.BitBlt, TextRecognitionMethod.OnnxOcrV5);

        Assert.IsTrue(result.Success, result.Message);
        Assert.AreEqual("1234567", result.Uid);
        Assert.AreEqual(2, ocr.Calls);
        Assert.AreEqual(1, capture.Releases);
        foreach (var frame in capture.Frames)
            Assert.ThrowsExactly<ObjectDisposedException>(() => frame.AddReference());
    }

    [TestMethod]
    public async Task CancelledOcrReleasesFrameAndCaptureSession()
    {
        var capture = new CaptureStub(false);
        var ocr = new OcrStub(capture, false) { Cancel = true };
        var service = new StatisticsUidDetectionService(new WindowStub(), capture, new ConfigStub(), ocr,
            NullLogger<StatisticsUidDetectionService>.Instance);

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            service.DetectAsync(CaptureMethod.BitBlt, TextRecognitionMethod.OnnxOcrV5));

        Assert.AreEqual(1, capture.Releases);
        Assert.ThrowsExactly<ObjectDisposedException>(() => capture.Frames.Single().AddReference());
    }

    private sealed class WindowStub : IGameWindowService
    {
        public string TargetProcessName => "test";
        public CaptureTargetWindow FindGameWindow() => new()
        {
            Hwnd = 1, Width = 104, Height = 66, ClientWidth = 100, ClientHeight = 60,
            WindowClientOffsetX = 2, WindowClientOffsetY = 4
        };
        public bool TryBringGameWindowToForeground(CaptureTargetWindow window) => true;
    }

    private sealed class CaptureStub(bool windowFrame) : IScreenCaptureService
    {
        public List<CapturedFrame> Frames { get; } = [];
        public int Releases;
        public CapturedFrame Capture(CaptureTargetWindow window, CaptureMethod method)
        {
            var frame = CapturedFrame.RentBgra32(windowFrame ? 104 : 100, windowFrame ? 66 : 60);
            Frames.Add(frame);
            return frame;
        }
        public void Release(CaptureTargetWindow window, CaptureMethod method) => Releases++;
    }

    private sealed class OcrStub(CaptureStub capture, bool windowFrame) : ITextRecognitionService
    {
        public int Calls;
        public bool Cancel;
        public IReadOnlyList<TextRecognitionMethodOption> GetMethods() =>
            [new(TextRecognitionMethod.OnnxOcrV5, "test", "test", true)];
        public TextRecognitionMethodOption GetDefaultMethod() => GetMethods().Single();
        public Task<TextRecognitionResult> RecognizeAsync(byte[] bytes, TextRecognitionMethod method,
            CancellationToken cancellationToken = default) => throw new AssertFailedException("UID 不应通过 PNG 中转。");
        public Task<TextRecognitionResult> RecognizeAsync(CapturedFrame frame, RecognitionRegion region,
            TextRecognitionMethod method, CancellationToken cancellationToken = default)
        {
            Calls++;
            Assert.AreSame(capture.Frames.Last(), frame);
            using var retained = frame.AddReference();
            Assert.AreEqual(windowFrame ? 12 : 10, region.X);
            Assert.AreEqual(windowFrame ? 9 : 5, region.Y);
            Assert.AreEqual(20, region.Width);
            Assert.AreEqual(10, region.Height);
            if (Cancel) throw new OperationCanceledException();
            return Task.FromResult(new TextRecognitionResult(method, "test", null, ["1234567"], 1));
        }
    }

    private sealed class ConfigStub : IRecognitionRegionConfigService
    {
        public IReadOnlyList<string> ListConfigPaths() => [];
        public bool TryResolveConfigResolution(int width, int height, out int configWidth, out int configHeight)
        { configWidth = width; configHeight = height; return true; }
        public RecognitionRegionConfig LoadForResolution(int width, int height) => new()
        {
            LoadedFromFile = true, ResolutionWidth = 100, ResolutionHeight = 60,
            Regions = [new() { Id = "statistics-uid", X = 10, Y = 5, Width = 20, Height = 10 }]
        };
        public RecognitionRegionConfig LoadFromPath(string path) => throw new NotSupportedException();
        public void Save(RecognitionRegionConfig config) => throw new NotSupportedException();
        public string GetConfigPath(int width, int height) => "test";
    }
}
