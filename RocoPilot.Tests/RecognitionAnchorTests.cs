using Microsoft.VisualStudio.TestTools.UnitTesting;
using RocoPilot.Models.Recognition;
using RocoPilot.Services.Recognition;

namespace RocoPilot.Tests;

[TestClass]
public sealed class RecognitionAnchorTests
{
    [TestMethod]
    [DataRow(RecognitionAnchor.TopLeft, 150, 120)]
    [DataRow(RecognitionAnchor.TopCenter, 1150, 120)]
    [DataRow(RecognitionAnchor.TopRight, 2150, 120)]
    public void WideClientKeepsControlSizeAndAnchorsPosition(RecognitionAnchor anchor, int x, int y)
    {
        var area = RecognitionRegionImageHelper.MapReferenceArea(50, 20, 4000, 1000,
            2000, 1000, 100, 100, 200, 80, anchor);
        Assert.AreEqual((x, y, 200, 80), (area.X, area.Y, area.Width, area.Height));
    }

    [TestMethod]
    public void TallClientAnchorsBottomAndClipsToClientBounds()
    {
        var area = RecognitionRegionImageHelper.MapReferenceArea(12, 30, 1000, 1000,
            2000, 1000, 1800, 900, 300, 200, RecognitionAnchor.BottomRight);
        Assert.AreEqual((912, 980, 100, 50), (area.X, area.Y, area.Width, area.Height));
    }
}
