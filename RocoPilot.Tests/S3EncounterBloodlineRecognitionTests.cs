using Microsoft.VisualStudio.TestTools.UnitTesting;

using RocoPilot.Models.Runtime;
using RocoPilot.Services;

namespace RocoPilot.Tests;

[TestClass]
public sealed class S3EncounterBloodlineRecognitionTests
{
    [TestMethod]
    public void ParsesQiYiBloodlineTip()
    {
        Assert.IsTrue(S3EncounterBloodlineRecognition.TryParse(
            "小加尔想了想，染上了几笔奇异的颜色！",
            out var kind));
        Assert.AreEqual(EncounterBloodlineKind.QiYi, kind);
        Assert.AreEqual("奇异", S3EncounterBloodlineRecognition.GetDisplayName(kind));
    }

    [TestMethod]
    public void ParsesHunXueBloodlineTipFromHunLuanText()
    {
        Assert.IsTrue(S3EncounterBloodlineRecognition.TryParse(
            "小加尔想了想，染上了几笔混乱的颜色！",
            out var kind));
        Assert.AreEqual(EncounterBloodlineKind.HunXue, kind);
        Assert.AreEqual("混血", S3EncounterBloodlineRecognition.GetDisplayName(kind));
    }

    [TestMethod]
    public void ParsesWuRanBloodlineTip()
    {
        Assert.IsTrue(S3EncounterBloodlineRecognition.TryParse(
            "小加尔想了想，染上了几笔污染的颜色！",
            out var kind));
        Assert.AreEqual(EncounterBloodlineKind.WuRan, kind);
    }

    [TestMethod]
    public void ParsesNormalTraitTip()
    {
        Assert.IsTrue(S3EncounterBloodlineRecognition.TryParse(
            "特性：坚韧不拔",
            out var kind));
        Assert.AreEqual(EncounterBloodlineKind.Normal, kind);
        Assert.AreEqual("普通", S3EncounterBloodlineRecognition.GetDisplayName(kind));
    }

    [TestMethod]
    public void ReturnsUnrecognizedForUnknownTip()
    {
        Assert.IsFalse(S3EncounterBloodlineRecognition.TryParse(
            "战斗提示无关内容一二三",
            out var kind));
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
