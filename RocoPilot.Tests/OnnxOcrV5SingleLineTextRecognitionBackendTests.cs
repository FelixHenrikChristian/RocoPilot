using Microsoft.VisualStudio.TestTools.UnitTesting;

using OpenCvSharp;

using RocoPilot.Models.TextRecognition;
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
    public async Task RecognizesEncodedImageWithOnnxMethod()
    {
        using var recognizer = new OnnxOcrV5SingleLineTextRecognitionBackend();
        using var image = new Mat(48, 160, MatType.CV_8UC3, Scalar.Black);
        Cv2.PutText(image, "Test", new Point(8, 36), HersheyFonts.HersheySimplex, 1, Scalar.White, 2);
        var imageBytes = image.ImEncode(".png");

        var onnxBackendType = recognizer.GetType().Assembly.GetType(
            "RocoPilot.Services.TextRecognition.Backends.OnnxOcrV5TextRecognitionBackend");

        Assert.IsNotNull(onnxBackendType, "ONNX OCR 应作为正式识别方法注册。");
        var onnxBackend = Activator.CreateInstance(onnxBackendType, recognizer);
        Assert.IsNotNull(onnxBackend);

        var option = (TextRecognitionMethodOption)onnxBackendType.GetMethod("GetOption")!.Invoke(onnxBackend, null)!;
        Assert.AreEqual("OnnxOcrV5", option.Method.ToString());

        var recognizeMethod = onnxBackendType.GetMethod(
            "RecognizeAsync",
            [typeof(byte[]), typeof(CancellationToken)]);
        Assert.IsNotNull(recognizeMethod);
        var recognitionTask = (Task<TextRecognitionResult>)recognizeMethod.Invoke(
            onnxBackend,
            [imageBytes, CancellationToken.None])!;
        var result = await recognitionTask;

        Assert.AreEqual("OnnxOcrV5", result.Method.ToString());
    }

}
