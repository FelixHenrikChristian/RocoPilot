using Microsoft.VisualStudio.TestTools.UnitTesting;

using RocoPilot.Models.Runtime;
using RocoPilot.Services;

namespace RocoPilot.Tests;

[TestClass]
public sealed class EncounterBloodlineRecognitionTests
{
    [TestMethod]
    [DataRow("奇异", EncounterBloodlineKind.QiYi)]
    [DataRow("混乱", EncounterBloodlineKind.HunXue)]
    [DataRow("污染", EncounterBloodlineKind.WuRan)]
    [DataRow("特性", EncounterBloodlineKind.Normal)]
    [DataRow("星陨提示：奇异", EncounterBloodlineKind.QiYi)]
    [DataRow("星星……混乱", EncounterBloodlineKind.HunXue)]
    [DataRow("任意前缀污染任意后缀", EncounterBloodlineKind.WuRan)]
    [DataRow("奇 \n异", EncounterBloodlineKind.QiYi)]
    [DataRow("特性：奇异能力", EncounterBloodlineKind.Normal)]
    public void ParsesKeywordsWithoutRequiringSeasonContext(string text, EncounterBloodlineKind expected)
    {
        Assert.IsTrue(EncounterBloodlineRecognition.TryParse(text, out var kind));
        Assert.AreEqual(expected, kind);
    }

    [TestMethod]
    public void ParsesQiYiBloodlineTip()
    {
        Assert.IsTrue(EncounterBloodlineRecognition.TryParse(
            "小加尔想了想，染上了几笔奇异的颜色！",
            out var kind));
        Assert.AreEqual(EncounterBloodlineKind.QiYi, kind);
        Assert.AreEqual("奇异", EncounterBloodlineRecognition.GetDisplayName(kind));
    }

    [TestMethod]
    public void ParsesHunXueBloodlineTipFromHunLuanText()
    {
        Assert.IsTrue(EncounterBloodlineRecognition.TryParse(
            "小加尔想了想，染上了几笔混乱的颜色！",
            out var kind));
        Assert.AreEqual(EncounterBloodlineKind.HunXue, kind);
        Assert.AreEqual("混血", EncounterBloodlineRecognition.GetDisplayName(kind));
    }

    [TestMethod]
    public void ParsesWuRanBloodlineTip()
    {
        Assert.IsTrue(EncounterBloodlineRecognition.TryParse(
            "小加尔想了想，染上了几笔污染的颜色！",
            out var kind));
        Assert.AreEqual(EncounterBloodlineKind.WuRan, kind);
    }

    [TestMethod]
    public void ParsesNormalTraitTip()
    {
        Assert.IsTrue(EncounterBloodlineRecognition.TryParse(
            "特性：坚韧不拔",
            out var kind));
        Assert.AreEqual(EncounterBloodlineKind.Normal, kind);
        Assert.AreEqual("普通", EncounterBloodlineRecognition.GetDisplayName(kind));
    }

    [TestMethod]
    [DataRow("战斗提示无关内容一二三")]
    [DataRow("星陨")]
    [DataRow("奇")]
    [DataRow("")]
    [DataRow(null)]
    public void ReturnsUnrecognizedForUnknownTip(string? text)
    {
        Assert.IsFalse(EncounterBloodlineRecognition.TryParse(text, out var kind));
        Assert.AreEqual(EncounterBloodlineKind.Unrecognized, kind);
    }

    [TestMethod]
    public void DefaultFilterCapturesQiYiWuRanAndUnrecognized()
    {
        var filter = BloodlineCaptureFilterSettings.CreateDefault();

        Assert.IsTrue(filter.ShouldCapture(EncounterBloodlineKind.QiYi));
        Assert.IsTrue(filter.ShouldCapture(EncounterBloodlineKind.WuRan));
        Assert.IsTrue(filter.ShouldCapture(EncounterBloodlineKind.Unrecognized));
        Assert.IsFalse(filter.ShouldCapture(EncounterBloodlineKind.HunXue));
        Assert.IsFalse(filter.ShouldCapture(EncounterBloodlineKind.Normal));
    }

    [TestMethod]
    public void DisabledFilterAlwaysCaptures()
    {
        var filter = BloodlineCaptureFilterSettings.CreateDefault();
        filter.IsEnabled = false;
        filter.CaptureQiYi = false;

        Assert.IsTrue(filter.ShouldCapture(EncounterBloodlineKind.QiYi));
        Assert.IsTrue(filter.ShouldCapture(EncounterBloodlineKind.Normal));
    }
}
