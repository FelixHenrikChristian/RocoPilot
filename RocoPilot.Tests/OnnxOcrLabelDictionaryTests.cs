using Microsoft.VisualStudio.TestTools.UnitTesting;

using RocoPilot.Services.TextRecognition.Backends;

namespace RocoPilot.Tests;

[TestClass]
public sealed class OnnxOcrLabelDictionaryTests
{
    [TestMethod]
    public void LoadsQuotedYamlDigitsWithoutQuoteCharacters()
    {
        var configurationPath = Path.Combine(
            AppContext.BaseDirectory,
            "Assets",
            "Models",
            "OCR",
            "Onnx",
            "PaddleOcrV6",
            "inference.yml");

        var labels = OnnxOcrLabelDictionary.Load(configurationPath);

        Assert.AreEqual("!", labels[0]);
        CollectionAssert.IsSubsetOf(
            Enumerable.Range(0, 10).Select(number => number.ToString()).ToArray(),
            labels.ToArray());
        Assert.IsFalse(labels.Any(label => label is "'0'" or "'5'" or "'9'"));
    }
}
