using Microsoft.VisualStudio.TestTools.UnitTesting;

using RocoPilot.Models.Statistics;

namespace RocoPilot.Tests;

[TestClass]
public sealed class StatisticsUidSelectionRulesTests
{
    [TestMethod]
    public void UsesRecognizedUidWhenItMatchesSelectedAccount()
    {
        var decision = StatisticsUidSelectionRules.Decide(
            StatisticsUidDetectionResult.Detected("1234567"),
            "1234567");

        Assert.AreEqual(StatisticsUidSelectionAction.UseRecognizedUid, decision.Action);
        Assert.AreEqual("1234567", decision.SuggestedUid);
    }

    [TestMethod]
    public void RequiresConfirmationWhenRecognizedUidDiffersFromSelectedAccount()
    {
        var decision = StatisticsUidSelectionRules.Decide(
            StatisticsUidDetectionResult.Detected("7654321"),
            "1234567");

        Assert.AreEqual(StatisticsUidSelectionAction.RequireConfirmation, decision.Action);
        Assert.AreEqual("7654321", decision.SuggestedUid);
    }

    [TestMethod]
    public void RequiresConfirmationWhenThereIsNoSelectedAccount()
    {
        var decision = StatisticsUidSelectionRules.Decide(
            StatisticsUidDetectionResult.Detected("1234567"),
            selectedAccountUid: null);

        Assert.AreEqual(StatisticsUidSelectionAction.RequireConfirmation, decision.Action);
        Assert.AreEqual("1234567", decision.SuggestedUid);
    }

    [TestMethod]
    public void RequiresConfirmationWhenRecognitionFails()
    {
        var decision = StatisticsUidSelectionRules.Decide(
            StatisticsUidDetectionResult.Failed("没有识别到 UID"),
            "1234567");

        Assert.AreEqual(StatisticsUidSelectionAction.RequireConfirmation, decision.Action);
        Assert.IsNull(decision.SuggestedUid);
    }
}
