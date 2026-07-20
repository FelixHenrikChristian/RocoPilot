using System.Reflection;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using RocoPilot.Models.TextRecognition;
using RocoPilot.Views.Test;

namespace RocoPilot.Tests;

[TestClass]
public sealed class TextRecognitionTestPageTests
{
    [TestMethod]
    public void IncludesRecognitionElapsedTimeInFinishedStatus()
    {
        var result = new TextRecognitionResult(
            TextRecognitionMethod.OnnxOcrV5,
            "ONNX Runtime PP-OCRv5（单行）",
            "Chinese/English",
            ["Test"],
            1);
        var formatter = typeof(TextRecognitionTestPage).GetMethod(
            "BuildFinishedStatus",
            BindingFlags.NonPublic | BindingFlags.Static,
            binder: null,
            types: [typeof(string), typeof(TextRecognitionResult), typeof(TimeSpan)],
            modifiers: null);

        Assert.IsNotNull(formatter, "识别完成状态应包含 OCR 耗时。");
        var status = (string)formatter.Invoke(null, ["识别完成", result, TimeSpan.FromMilliseconds(12.34)])!;

        Assert.AreEqual("识别完成 · ONNX Runtime PP-OCRv5（单行） · 识别语言：Chinese/English · 耗时 12.3 ms", status);
    }

    [TestMethod]
    public void PlacesSingleLineOnnxMethodBeforeOtherTestMethods()
    {
        var paddleOption = new TextRecognitionMethodOption(
            TextRecognitionMethod.PaddleOcrV5,
            "PaddleOCR",
            "test",
            true);
        var onnxOption = new TextRecognitionMethodOption(
            TextRecognitionMethod.OnnxOcrV5,
            "ONNX OCR v5（单行加速）",
            "test",
            true);
        var orderingMethod = typeof(TextRecognitionTestPage).GetMethod(
            "BuildRecognitionMethods",
            BindingFlags.NonPublic | BindingFlags.Static,
            binder: null,
            types: [typeof(IReadOnlyList<TextRecognitionMethodOption>)],
            modifiers: null);

        Assert.IsNotNull(orderingMethod, "测试页应优先展示 ONNX 单行 OCR。");
        var methods = (IReadOnlyList<TextRecognitionMethodOption>)orderingMethod.Invoke(
            null,
            new object?[] { new[] { paddleOption, onnxOption } })!;

        Assert.AreEqual(TextRecognitionMethod.OnnxOcrV5, methods[0].Method);
        Assert.AreEqual(TextRecognitionMethod.PaddleOcrV5, methods[1].Method);
    }
}
