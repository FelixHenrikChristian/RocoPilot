using Microsoft.VisualStudio.TestTools.UnitTesting;

using RocoPilot.Models.Statistics;

namespace RocoPilot.Tests;

[TestClass]
public sealed class StatisticsUidRulesTests
{
    [TestMethod]
    public void ExtractsOnlyDigitsFromAnyOcrResult()
    {
        var success = StatisticsUidRules.TryNormalize(" UID： '1' 2-3_4/5，6。7 ", out var uid);

        Assert.IsTrue(success);
        Assert.AreEqual("1234567", uid);
    }

    [TestMethod]
    public void RejectsOcrResultWithoutDigits()
    {
        var success = StatisticsUidRules.TryNormalize("UID：识别失败", out var uid);

        Assert.IsFalse(success);
        Assert.AreEqual(string.Empty, uid);
    }

    [TestMethod]
    public void RejectsFilteredUidLongerThanMaximumLength()
    {
        var success = StatisticsUidRules.TryNormalize(new string('1', 33), out var uid);

        Assert.IsFalse(success);
        Assert.AreEqual(string.Empty, uid);
    }
}
