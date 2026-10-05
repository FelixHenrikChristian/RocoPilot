using Microsoft.VisualStudio.TestTools.UnitTesting;

using RocoPilot.Configuration;
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
    [DataRow(TextRecognitionMethod.OnnxOcrV5, "onnx-frame")]
    [DataRow(TextRecognitionMethod.PaddleOcrV5, "paddle-frame")]
    public async Task SendsFrameDirectlyToBackendSelectedByMethod(TextRecognitionMethod method, string expectedText)
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
            [frame, region, method, CancellationToken.None])!;
        var result = await recognitionTask;

        Assert.AreEqual(expectedText, result.Text);
        Assert.AreEqual(method == TextRecognitionMethod.OnnxOcrV5, onnxBackend.ReceivedFrame);
        Assert.AreEqual(method == TextRecognitionMethod.PaddleOcrV5, paddleBackend.ReceivedFrame);
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public void UsesConfiguredDefaultRegardlessOfAvailability(bool isAvailable)
    {
        var alternateMethod = Enum.GetValues<TextRecognitionMethod>().First(method => method != TextRecognitionDefaults.Method);
        var service = new TextRecognitionService(
        [
            new FrameCapableBackend(alternateMethod, "alternate-frame"),
            new FrameCapableBackend(TextRecognitionDefaults.Method, "default-frame", isAvailable)
        ]);

        var option = service.GetDefaultMethod();

        Assert.IsNotNull(option);
        Assert.AreEqual(TextRecognitionDefaults.Method, option.Method);
        Assert.AreEqual(isAvailable, option.IsAvailable);

        var methods = service.GetMethods();
        Assert.HasCount(2, methods);
        Assert.AreEqual(TextRecognitionDefaults.Method, methods[0].Method);
        Assert.AreEqual(alternateMethod, methods[1].Method);
    }

    [TestMethod]
    public void MissingConfiguredBackendDoesNotSelectAnAlternative()
    {
        var alternateMethod = Enum.GetValues<TextRecognitionMethod>().First(method => method != TextRecognitionDefaults.Method);
        var service = new TextRecognitionService([new FrameCapableBackend(alternateMethod, "alternate-frame")]);

        Assert.IsNull(service.GetDefaultMethod());
    }

    private sealed class FrameCapableBackend : ITextRecognitionBackend, IFrameTextRecognitionBackend
    {
        public bool ReceivedFrame { get; private set; }

        public bool ReceivedImageBytes { get; private set; }

        private readonly string _text;
        private readonly bool _isAvailable;

        public FrameCapableBackend(TextRecognitionMethod method, string text, bool isAvailable = true)
        {
            Method = method;
            _text = text;
            _isAvailable = isAvailable;
        }

        public TextRecognitionMethod Method { get; }

        public TextRecognitionMethodOption GetOption()
        {
            return new TextRecognitionMethodOption(Method, "test", "test", _isAvailable);
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
