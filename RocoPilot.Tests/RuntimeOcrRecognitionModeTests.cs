using Microsoft.VisualStudio.TestTools.UnitTesting;

using RocoPilot.Models.TextRecognition;
using RocoPilot.Services.TextRecognition;

namespace RocoPilot.Tests;

[TestClass]
public sealed class RuntimeOcrRecognitionModeTests
{
    [TestMethod]
    [DataRow("battle-enemy-name")]
    [DataRow("battle-boss-name")]
    [DataRow("shouling_name")]
    [DataRow("battle-tip-shiny")]
    [DataRow("battle-tip-message")]
    [DataRow("battle-tip")]
    [DataRow("battle-tip-encounter-s3")]
    [DataRow("battle-tip-boss-combo")]
    [DataRow("shouling_tip")]
    public void UsesSingleLineLayoutForKnownRuntimeRegion(string regionId)
    {
        Assert.AreEqual(
            TextRecognitionLayout.SingleLine,
            RuntimeOcrRecognitionMode.ResolveLayout(regionId));
    }

    [TestMethod]
    public void UsesFullLayoutForUnknownRuntimeRegion()
    {
        Assert.AreEqual(
            TextRecognitionLayout.Full,
            RuntimeOcrRecognitionMode.ResolveLayout("unknown-region"));
    }
}
