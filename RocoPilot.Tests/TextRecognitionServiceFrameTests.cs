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
    public async Task SendsFrameDirectlyToBackendSelectedByMethod()
    {
        var paddleBackend = new FrameCapableBackend(TextRecognitionMethod.PaddleOcrV5, "paddle-frame");
        var onnxBackend = new FrameCapableBackend(TextRecognitionMethod.OnnxOcrV5, "onnx-frame");
        var service = new TextRecognitionService([paddleBackend, onnxBackend]);
        using var frame = new CapturedFrame(2, 2, new byte[16]);
        var region = new RecognitionRegion { Id = "single-line", Width = 2, Height = 2 };
        var recognizeMethod = service.GetType().GetMethod(
            "RecognizeAsync",
            [
                typeof(CapturedFrame),
                typeof(RecognitionRegion),
                typeof(TextRecognitionMethod),
                typeof(CancellationToken)
            ]);

        Assert.IsNotNull(recognizeMethod, "帧识别不应再依赖单行或多行布局。");
        var recognitionTask = (Task<TextRecognitionResult>)recognizeMethod.Invoke(
            service,
            [frame, region, TextRecognitionMethod.OnnxOcrV5, CancellationToken.None])!;
        var result = await recognitionTask;

        Assert.AreEqual("onnx-frame", result.Text);
        Assert.IsTrue(onnxBackend.ReceivedFrame);
        Assert.IsFalse(paddleBackend.ReceivedFrame);
    }

    [TestMethod]
    public void PrefersOnnxAsDefaultMethod()
    {
        var service = new TextRecognitionService(
        [
            new FrameCapableBackend(TextRecognitionMethod.PaddleOcrV5, "paddle-frame"),
            new FrameCapableBackend(TextRecognitionMethod.OnnxOcrV5, "onnx-frame")
        ]);

        Assert.AreEqual(TextRecognitionMethod.OnnxOcrV5, service.GetDefaultMethod()?.Method);
    }

    private sealed class FrameCapableBackend : ITextRecognitionBackend, IFrameTextRecognitionBackend
    {
        public bool ReceivedFrame { get; private set; }

        public bool ReceivedImageBytes { get; private set; }

        private readonly string _text;

        public FrameCapableBackend(TextRecognitionMethod method, string text)
        {
            Method = method;
            _text = text;
        }

        public TextRecognitionMethod Method { get; }

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
            CancellationToken cancellationToken)
        {
            ReceivedFrame = true;
            Assert.AreEqual("single-line", region.Id);
            return Task.FromResult(CreateResult(_text));
        }

        private TextRecognitionResult CreateResult(string text)
        {
            return new TextRecognitionResult(Method, "test", null, [text], 1);
        }
    }

}
