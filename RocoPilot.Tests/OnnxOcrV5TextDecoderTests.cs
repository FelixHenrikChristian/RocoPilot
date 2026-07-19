using Microsoft.VisualStudio.TestTools.UnitTesting;

using RocoPilot.Services.TextRecognition.Backends;

namespace RocoPilot.Tests;

[TestClass]
public sealed class OnnxOcrV5TextDecoderTests
{
    [TestMethod]
    public void DecodesCtcOutputAndCollapsesRepeatedCharacters()
    {
        var labels = new[] { "A", "B", "C" };
        var scores = new[]
        {
            0f, 0f, 0f, 0f,
            0f, 8f, 0f, 0f,
            0f, 9f, 0f, 0f,
            7f, 0f, 0f, 0f,
            0f, 0f, 6f, 0f
        };

        var text = OnnxOcrV5TextDecoder.Decode(labels, scores, timeStepCount: 5, labelCount: 4);

        Assert.AreEqual("AB", text);
    }
}
