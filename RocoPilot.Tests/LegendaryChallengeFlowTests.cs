using Microsoft.VisualStudio.TestTools.UnitTesting;
using RocoPilot.Models.ImageMatching;
using RocoPilot.Models.Runtime;
using RocoPilot.Services.IndependentTasks;

namespace RocoPilot.Tests;

[TestClass]
public sealed class LegendaryChallengeFlowTests
{
    private static readonly ImageMatchResult Confirm = new(true, 1, 1100, 950, 100, 40, "confirm");
    private static readonly ImageMatchResult Exit = Confirm with { X = 800, TemplatePath = "exit" };
    private static readonly ImageMatchResult Contract = Confirm with { TemplatePath = "contract" };
    private static readonly ImageMatchResult Mark = Confirm with { TemplatePath = "mark" };

    [TestMethod]
    [DataRow("圣羽翼王")]
    [DataRow("龙息帕尔")]
    public void ChoosesByNameAndVerifiesMapBeforeInteracting(string name)
    {
        var flow = new ManualChallengeFlow(null, 3, name);
        var selected = Next(flow, ManualChallengeScene.LegendaryList, rows:
            [new("龙息帕尔", Mark), new("圣羽翼王", Contract)]);
        Assert.AreEqual(name == "龙息帕尔" ? Mark : Contract, selected.Button);
        Assert.AreEqual(ManualChallengeAction.None, Next(flow, ManualChallengeScene.Map, text: "其他精灵").Action);
        Assert.AreEqual(ManualChallengeAction.OpenManual, Next(flow, ManualChallengeScene.Interaction, text: "挑战！").Action);
        Assert.AreEqual(ManualChallengeAction.Click, Next(flow, ManualChallengeScene.Map, text: name).Action);
        Assert.AreEqual(ManualChallengeAction.Approach, Next(flow, ManualChallengeScene.World).Action);
        Assert.AreEqual(ManualChallengeAction.Approach, Next(flow, ManualChallengeScene.Interaction, text: "查看留言").Action);
        Assert.AreEqual(ManualChallengeAction.SelectChallenge, flow.Next(new(ManualChallengeScene.Interaction, [],
            Text: "查看留言", HasChallengeOption: true), 1152).Action);
        Assert.AreEqual(ManualChallengeAction.Interact, Next(flow, ManualChallengeScene.Interaction, text: "挑战！").Action);
    }

    [TestMethod]
    [DataRow(1, 3)]
    [DataRow(2, 1)]
    [DataRow(3, 2)]
    public void DifficultyMustBeConfirmedBeforeStartingAndIsCheckedAgainOnReturn(int target, int current)
    {
        var flow = new ManualChallengeFlow(null, 3, "圣羽翼王", legendaryDifficulty: target);
        Next(flow, ManualChallengeScene.LegendaryList, rows: [new("圣羽翼王", Confirm)]);
        Next(flow, ManualChallengeScene.Map, text: "圣羽翼王");
        var option = new ManualChallengeRow(new string('I', target), Mark) { Number = target };
        var wrong = new ManualChallengeScreen(ManualChallengeScene.LegendaryConfirmation, [option], Confirm, Difficulty: current);
        Assert.AreEqual(ManualChallengeAction.None, flow.Next(wrong with { Difficulty = 0 }, 1152).Action);
        Assert.AreEqual(Mark, flow.Next(wrong, 1152).Button);
        Assert.AreEqual(Mark, flow.Next(wrong, 1152).Button);
        Assert.AreEqual(Confirm, flow.Next(wrong with { Difficulty = target }, 1152).Button);
        Next(flow, ManualChallengeScene.Preparation);
        Next(flow, ManualChallengeScene.Battle);
        Next(flow, ManualChallengeScene.Capture);
        Next(flow, ManualChallengeScene.Result);
        Assert.AreEqual(Mark, flow.Next(wrong, 1152).Button);
    }

    [TestMethod]
    public void SkippingAnimationPreservesBattlePhaseAndCounters()
    {
        var flow = Navigate(true);
        Next(flow, ManualChallengeScene.Preparation);
        Assert.AreEqual(ManualChallengeAction.Escape, Next(flow, ManualChallengeScene.SkippableAnimation).Action);
        Assert.AreEqual(0, flow.BattleNumber);
        Assert.AreEqual(ManualChallengeAction.Battle, Next(flow, ManualChallengeScene.Battle).Action);
        Assert.AreEqual(ManualChallengeAction.Escape, Next(flow, ManualChallengeScene.SkippableAnimation).Action);
        Assert.IsTrue(flow.IsBattleActive);
        Assert.AreEqual(ManualChallengeAction.Battle, Next(flow, ManualChallengeScene.Battle).Action);
        Assert.AreEqual(1, flow.BattleNumber);
        Assert.AreEqual(0, flow.CompletedCount);
        Assert.AreEqual(0, flow.ConsecutiveFailures);
    }

    [TestMethod]
    public void StopsBeforeBattleWhenMarksAreDisabled()
    {
        var flow = Navigate(false);
        Assert.AreEqual(Exit, Next(flow, ManualChallengeScene.InsufficientContractMark).Button);
        Assert.IsNotNull(flow.StopReason);
        Assert.AreEqual(ManualChallengeAction.Escape, Next(flow, ManualChallengeScene.LegendaryConfirmation).Action);
        Assert.AreEqual(ManualChallengeAction.Stop, Next(flow, ManualChallengeScene.World).Action);
        Assert.AreEqual(0, flow.CompletedCount);
        Assert.AreEqual(0, flow.BattleNumber);

    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public void UsesContractFirstWhenBothResourcesAllowIt(bool allowMarks)
    {
        var flow = StartBattle(allowMarks);
        Assert.AreEqual(Confirm, Next(flow, ManualChallengeScene.CaptureConfirmation).Button);
        Assert.IsNull(flow.StopReason);
    }

    [TestMethod]
    public void SwitchesToLegendaryMarkOnlyAfterConfirmedShortage()
    {
        var flow = Navigate(true);
        Assert.AreEqual(Confirm, Next(flow, ManualChallengeScene.InsufficientContractMark).Button);
        Next(flow, ManualChallengeScene.Preparation);
        Next(flow, ManualChallengeScene.Battle);
        Assert.AreEqual(Mark, Next(flow, ManualChallengeScene.CaptureConfirmation).Button);
        Assert.AreEqual(Mark, Next(flow, ManualChallengeScene.CaptureConfirmation).Button);
        Assert.AreEqual(Confirm, Next(flow, ManualChallengeScene.CaptureConfirmation, selected: true).Button);
        Assert.AreEqual(ManualChallengeAction.Escape, Next(flow, ManualChallengeScene.LegendaryRewards).Action);
        Assert.AreEqual(0, flow.CompletedCount);
        Assert.AreEqual(ManualChallengeAction.None, Next(flow, ManualChallengeScene.Result).Action);
    }

    [TestMethod]
    public void SelectedMarkDoesNotBypassPreference()
    {
        var flow = StartBattle(false);
        Assert.AreEqual(Contract, Next(flow, ManualChallengeScene.CaptureConfirmation, selected: true).Button);
    }

    [TestMethod]
    public void CapturedResultCountsOnceAndRepeatsUntilConfiguredTotal()
    {
        var flow = StartBattle(true);
        for (var i = 1; i <= 3; i++)
        {
            if (i == 1) Assert.AreEqual(Confirm, Next(flow, ManualChallengeScene.CaptureConfirmation).Button);
            else
            {
                Assert.AreEqual(Mark, Next(flow, ManualChallengeScene.CaptureConfirmation).Button);
                Assert.AreEqual(Confirm, Next(flow, ManualChallengeScene.CaptureConfirmation, selected: true).Button);
            }
            Assert.AreEqual(ManualChallengeAction.Escape, Next(flow, ManualChallengeScene.LegendaryRewards).Action);
            Assert.AreEqual(ManualChallengeAction.Capture, Next(flow, ManualChallengeScene.Capture).Action);
            Assert.AreEqual(i == 3 ? Exit : Confirm, Next(flow, ManualChallengeScene.Result).Button);
            Next(flow, ManualChallengeScene.Result);
            Assert.AreEqual(i, flow.CompletedCount);
            if (i == 3) break;
            Assert.AreEqual(Confirm, Next(flow, ManualChallengeScene.InsufficientContractMark).Button);
            Assert.AreEqual(Confirm, Next(flow, ManualChallengeScene.Preparation).Button);
            Next(flow, ManualChallengeScene.Battle);
        }
        Assert.AreEqual(3, flow.BattleNumber);
        Assert.AreEqual(ManualChallengeAction.Complete, Next(flow, ManualChallengeScene.World).Action);
    }

    [TestMethod]
    public void PreviousShortageDoesNotCarryIntoNextBattleWithoutNewPrompt()
    {
        var flow = Navigate(true);
        Next(flow, ManualChallengeScene.InsufficientContractMark);
        Next(flow, ManualChallengeScene.Preparation);
        Next(flow, ManualChallengeScene.Battle);
        Next(flow, ManualChallengeScene.Capture);
        Next(flow, ManualChallengeScene.Result);
        Next(flow, ManualChallengeScene.Preparation);
        Next(flow, ManualChallengeScene.Battle);
        Assert.AreEqual(Contract, Next(flow, ManualChallengeScene.CaptureConfirmation, selected: true).Button);
        Assert.AreEqual(Confirm, Next(flow, ManualChallengeScene.CaptureConfirmation).Button);
    }

    private static ManualChallengeFlow Navigate(bool allowMarks)
    {
        var flow = new ManualChallengeFlow(null, 3, "圣羽翼王", allowMarks);
        Next(flow, ManualChallengeScene.LegendaryList, rows: [new("圣羽翼王", Confirm)]);
        Next(flow, ManualChallengeScene.Map, text: "圣羽翼王");
        return flow;
    }

    private static ManualChallengeFlow StartBattle(bool allowMarks)
    {
        var flow = Navigate(allowMarks);
        Next(flow, ManualChallengeScene.Preparation);
        Next(flow, ManualChallengeScene.Battle);
        return flow;
    }

    private static ManualChallengeDecision Next(ManualChallengeFlow flow, ManualChallengeScene scene,
        bool selected = false, string text = "",
        IReadOnlyList<ManualChallengeRow>? rows = null)
        => flow.Next(new(scene, rows ?? [], Confirm, text, ExitButton: Exit,
            Marks: new(selected, Contract, Mark)), 1152);
}
