using Microsoft.VisualStudio.TestTools.UnitTesting;
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

}
