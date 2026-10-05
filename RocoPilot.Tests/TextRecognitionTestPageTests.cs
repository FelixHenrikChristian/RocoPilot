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

}
