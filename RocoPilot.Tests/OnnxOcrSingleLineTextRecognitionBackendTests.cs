using Microsoft.VisualStudio.TestTools.UnitTesting;

using OpenCvSharp;

using RocoPilot.Models.TextRecognition;
using RocoPilot.Services.TextRecognition.Backends;

namespace RocoPilot.Tests;

[TestClass]
public sealed class OnnxOcrSingleLineTextRecognitionBackendTests
{
    [TestMethod]
    public void LoadsBundledOnnxModel()
    {
        using var backend = new OnnxOcrSingleLineTextRecognitionBackend();

        Assert.IsTrue(backend.IsAvailable);
    }

    [TestMethod]
    public async Task PrewarmsBundledOnnxModel()
    {
        using var backend = new OnnxOcrSingleLineTextRecognitionBackend();

        await backend.PrewarmAsync();

        Assert.IsTrue(backend.IsAvailable);
    }

    [TestMethod]
    public async Task RecognizesEncodedImageWithOnnxMethod()
    {
        using var recognizer = new OnnxOcrSingleLineTextRecognitionBackend();
        using var image = new Mat(48, 160, MatType.CV_8UC3, Scalar.Black);
        Cv2.PutText(image, "Test", new Point(8, 36), HersheyFonts.HersheySimplex, 1, Scalar.White, 2);
        var imageBytes = image.ImEncode(".png");

        var backend = new OnnxOcrTextRecognitionBackend(recognizer);
        var option = backend.GetOption();
        Assert.AreEqual(TextRecognitionMethod.OnnxOcr, option.Method);
        Assert.AreEqual("ONNX OCR v6 small", option.Name);

        var result = await backend.RecognizeAsync(imageBytes, CancellationToken.None);

        Assert.AreEqual(TextRecognitionMethod.OnnxOcr, result.Method);
        Assert.AreEqual("ONNX Runtime PP-OCRv6 small", result.MethodName);
        Assert.AreEqual("TEST", result.Text.ToUpperInvariant());
    }
}
