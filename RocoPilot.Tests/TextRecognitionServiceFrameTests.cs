using Microsoft.VisualStudio.TestTools.UnitTesting;

using RocoPilot.Models.Capture;
using RocoPilot.Models.Recognition;
using RocoPilot.Models.TextRecognition;
using RocoPilot.Services.TextRecognition;
using RocoPilot.Services.TextRecognition.Backends;

namespace RocoPilot.Tests;

[TestClass]
public sealed class TextRecognitionServiceFrameTests
{
    [TestMethod]
    public async Task SendsFrameDirectlyToFrameCapableBackend()
    {
        var backend = new FrameCapableBackend();
        var service = new TextRecognitionService([backend]);
        using var frame = new CapturedFrame(2, 2, new byte[16]);
        var region = new RecognitionRegion { Id = "single-line", Width = 2, Height = 2 };

        var result = await service.RecognizeAsync(
            frame,
            region,
            TextRecognitionLayout.SingleLine,
            TextRecognitionMethod.PaddleOcrV5);

        Assert.AreEqual("direct-frame", result.Text);
        Assert.IsTrue(backend.ReceivedFrame);
        Assert.IsFalse(backend.ReceivedImageBytes);
    }

    [TestMethod]
    public async Task PrefersAvailableSingleLineBackend()
    {
        var frameBackend = new FrameCapableBackend();
        var singleLineBackend = new SingleLineBackend();
        var service = new TextRecognitionService([frameBackend], [singleLineBackend]);
        using var frame = new CapturedFrame(2, 2, new byte[16]);
        var region = new RecognitionRegion { Id = "single-line", Width = 2, Height = 2 };

        var result = await service.RecognizeAsync(
            frame,
            region,
            TextRecognitionLayout.SingleLine,
            TextRecognitionMethod.PaddleOcrV5);

        Assert.AreEqual("onnx-single-line", result.Text);
        Assert.IsTrue(singleLineBackend.ReceivedFrame);
        Assert.IsFalse(frameBackend.ReceivedFrame);
    }

    private sealed class FrameCapableBackend : ITextRecognitionBackend, IFrameTextRecognitionBackend
    {
        public bool ReceivedFrame { get; private set; }

        public bool ReceivedImageBytes { get; private set; }

        public TextRecognitionMethod Method => TextRecognitionMethod.PaddleOcrV5;

        public TextRecognitionMethodOption GetOption()
        {
            return new TextRecognitionMethodOption(Method, "test", "test", true);
        }

        public Task<TextRecognitionResult> RecognizeAsync(byte[] imageBytes, CancellationToken cancellationToken)
        {
            ReceivedImageBytes = true;
            return Task.FromResult(CreateResult("image-bytes"));
        }

        public Task<TextRecognitionResult> RecognizeAsync(
            CapturedFrame frame,
            RecognitionRegion region,
            TextRecognitionLayout layout,
            CancellationToken cancellationToken)
        {
            ReceivedFrame = true;
            Assert.AreEqual(TextRecognitionLayout.SingleLine, layout);
            Assert.AreEqual("single-line", region.Id);
            return Task.FromResult(CreateResult("direct-frame"));
        }

        private TextRecognitionResult CreateResult(string text)
        {
            return new TextRecognitionResult(Method, "test", null, [text], 1);
        }
    }

    private sealed class SingleLineBackend : ISingleLineTextRecognitionBackend
    {
        public bool ReceivedFrame { get; private set; }

        public TextRecognitionMethod Method => TextRecognitionMethod.PaddleOcrV5;

        public bool IsAvailable => true;

        public Task<TextRecognitionResult> RecognizeAsync(
            CapturedFrame frame,
            RecognitionRegion region,
            CancellationToken cancellationToken)
        {
            ReceivedFrame = true;
            return Task.FromResult(new TextRecognitionResult(Method, "onnx", null, ["onnx-single-line"], 1));
        }
    }
}
