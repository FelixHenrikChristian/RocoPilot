using Microsoft.VisualStudio.TestTools.UnitTesting;

using OpenCvSharp;

using RocoPilot.Services.TextRecognition.Backends;

namespace RocoPilot.Tests;

[TestClass]
public sealed class OnnxOcrV5SingleLineTextRecognitionBackendTests
{
    [TestMethod]
    public void LoadsBundledOnnxModel()
    {
        using var backend = new OnnxOcrV5SingleLineTextRecognitionBackend();

        Assert.IsTrue(backend.IsAvailable);
    }

    [TestMethod]
    public async Task PrewarmsBundledOnnxModel()
    {
        using var backend = new OnnxOcrV5SingleLineTextRecognitionBackend();

        await backend.PrewarmAsync();

        Assert.IsTrue(backend.IsAvailable);
    }

    [TestMethod]
    public async Task RecognizesEncodedSingleLineImageForTestPage()
    {
        using var backend = new OnnxOcrV5SingleLineTextRecognitionBackend();
        using var image = new Mat(48, 160, MatType.CV_8UC3, Scalar.Black);
        Cv2.PutText(image, "Test", new Point(8, 36), HersheyFonts.HersheySimplex, 1, Scalar.White, 2);
        var imageBytes = image.ImEncode(".png");

        var testBackend = new OnnxOcrV5SingleLineTextRecognitionTestBackend(backend);

        var option = testBackend.GetOption();
        Assert.AreEqual("OnnxOcrV5", option.Method.ToString());

        var result = await testBackend.RecognizeAsync(imageBytes);

        Assert.AreEqual("OnnxOcrV5", result.Method.ToString());
    }

    [TestMethod]
    public void RetainsPaddleMethodForRuntimeSingleLineAcceleration()
    {
        using var backend = new OnnxOcrV5SingleLineTextRecognitionBackend();

        Assert.AreEqual("PaddleOcrV5", backend.Method.ToString());
    }

}
