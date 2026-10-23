using Microsoft.VisualStudio.TestTools.UnitTesting;
using RocoPilot.Models.ImageMatching;
using RocoPilot.Models.Runtime;
using RocoPilot.Services.IndependentTasks;

namespace RocoPilot.Tests;

[TestClass]
public sealed class FlowerSeedFlowTests
{
    private static readonly ImageMatchResult Button = new(true, .99, 1740, 200, 80, 40, "button");
    private static ManualChallengeScreen Page(params string[] names)
        => new(ManualChallengeScene.FlowerList,
            names.Select((name, index) => new ManualChallengeRow(name, Button with { Y = 200 + index * 230 })).ToArray());

    [TestMethod]
    public void ScanWithZeroRunCountStillNavigatesAndCompletesTheList()
    {
        var flow = new ManualChallengeFlow(null, 0);
        Assert.AreEqual(ManualChallengeAction.OpenManual, flow.Next(new(ManualChallengeScene.World, []), 1152).Action);
        var top = Page("友爱星飞");
        Assert.AreEqual(ManualChallengeAction.ScrollUp, flow.Next(top, 1152).Action);
        Assert.AreEqual(ManualChallengeAction.ScrollUp, ExecuteScroll(flow, top).Action);
        Assert.AreEqual(ManualChallengeAction.ScrollDown, ExecuteScroll(flow, top).Action);
        Assert.AreEqual(ManualChallengeAction.ScrollDown, ExecuteScroll(flow, top).Action);
        Assert.AreEqual(ManualChallengeAction.Complete, ExecuteScroll(flow, top).Action);
        Assert.HasCount(1, flow.Options);
    }

    [TestMethod]
    public void ScanUsesCanonicalNamesOnceAndRequiresExecutedScrollsToConfirmBothEdges()
    {
        var flow = new ManualChallengeFlow(null);
        var top = PositionedPage(100, "小皮球", "友爱星飞");
        for (var i = 0; i < 5; i++) Assert.AreEqual(ManualChallengeAction.ScrollUp, flow.Next(top, 1152).Action);
        Assert.AreEqual(0, flow.Options.Count);
        Assert.AreEqual(ManualChallengeAction.ScrollUp, ExecuteScroll(flow, top).Action);
        Assert.AreEqual(ManualChallengeAction.ScrollDown, ExecuteScroll(flow, top).Action);
        CollectionAssert.AreEqual(new[] { "小皮球", "友爱星飞" }, flow.Options.Select(option => option.Name).ToArray());

        var next = PositionedPage(244, "友爱星飞", "星云旅者");
        flow.RecordScroll(top, 1152);
        Assert.AreEqual(ManualChallengeAction.ScrollDown, flow.Next(next, 1152).Action);
        for (var i = 0; i < 5; i++) Assert.AreEqual(ManualChallengeAction.ScrollDown, flow.Next(next, 1152).Action);
        Assert.AreEqual(ManualChallengeAction.ScrollDown, ExecuteScroll(flow, next).Action);
        Assert.AreEqual(ManualChallengeAction.Complete, ExecuteScroll(flow, next).Action);
        CollectionAssert.AreEqual(new[] { "小皮球", "友爱星飞", "星云旅者" }, flow.Options.Select(option => option.Name).ToArray());
        CollectionAssert.AreEqual(new[] { 1, 2, 3 }, flow.Options.Select(option => option.Number).ToArray());
        Assert.AreEqual(0, new ManualChallengeFlow(null).Options.Count);
    }

    [TestMethod]
    public void UnmatchedOcrAndEmptyNamesRemainOptionsAndDoNotBlockScrolling()
    {
        var flow = new ManualChallengeFlow(null);
        var top = Page("小皮球", "", "");
        top = top with { Rows = top.Rows.Select((row, index) => index == 1 ? row with { RawName = "电球羊羊" } : row).ToArray() };
        Assert.AreEqual(ManualChallengeAction.ScrollUp, flow.Next(top, 1152).Action);
        Assert.AreEqual(ManualChallengeAction.ScrollUp, ExecuteScroll(flow, top).Action);
        Assert.AreEqual(ManualChallengeAction.ScrollDown, ExecuteScroll(flow, top).Action);
        Assert.HasCount(3, flow.Options);
        CollectionAssert.AreEqual(new[] { 1, 2, 3 }, flow.Options.Select(option => option.Number).ToArray());
        Assert.AreEqual("小皮球", flow.Options[0].Name);
        Assert.AreEqual("电球羊羊", flow.Options[1].Name);
        Assert.AreEqual(ManualChallengeAction.ScrollDown, ExecuteScroll(flow, top).Action);
        Assert.AreEqual(ManualChallengeAction.Complete, ExecuteScroll(flow, top).Action);
        Assert.HasCount(3, flow.Options);
        Assert.AreEqual(3, flow.Options[2].Number);
    }

    [TestMethod]
    public void MissingNamesDoNotInvalidateAnUnchangedMatchedListBoundary()
    {
        var flow = new ManualChallengeFlow(null);
        flow.RecordScroll(Page("", "友爱星飞"), 1152);
        Assert.AreEqual(ManualChallengeAction.ScrollUp, flow.Next(Page("小皮球", ""), 1152).Action);
        flow.RecordScroll(Page("小皮球", ""), 1152);
        var top = Page("", "友爱星飞");
        Assert.AreEqual(ManualChallengeAction.ScrollDown, flow.Next(top, 1152).Action);
        Assert.HasCount(2, flow.Options);
        Assert.AreEqual("友爱星飞", flow.Options[1].Name);
    }

    [TestMethod]
    public void MatchingRowPositionsMatterEvenWhenNamesRemainTheSame()
    {
        var flow = new ManualChallengeFlow(null);
        var top = Page("小皮球", "友爱星飞");
        flow.RecordScroll(top, 1152);
        var shifted = Shift(top, -50);
        Assert.AreEqual(ManualChallengeAction.ScrollUp, flow.Next(shifted, 1152).Action);
        Assert.AreEqual(ManualChallengeAction.ScrollUp, ExecuteScroll(flow, shifted).Action);
    }

    [TestMethod]
    public void ScanKeepsGlobalRowNumbersAndSuccessfulMatchesAcrossScrolledFrames()
    {
        var flow = new ManualChallengeFlow(null);
        var top = PositionedPage(100, "友爱星飞", "小皮球", "星云旅者");
        Assert.AreEqual(ManualChallengeAction.ScrollUp, flow.Next(top, 1152).Action);
        Assert.IsTrue(top.Rows.All(row => row.Number == 0));
        Assert.AreEqual(ManualChallengeAction.ScrollUp, ExecuteScroll(flow, top).Action);
        Assert.AreEqual(ManualChallengeAction.ScrollDown, ExecuteScroll(flow, top).Action);
        CollectionAssert.AreEqual(new[] { 1, 2, 3 }, top.Rows.Select(row => row.Number).ToArray());
        Assert.IsTrue(top.Rows.All(row => row.HasMatched));

        flow.RecordScroll(top, 1152);
        var next = PositionedPage(244, "", "星云旅者", "");
        Assert.AreEqual(ManualChallengeAction.ScrollDown, flow.Next(next, 1152).Action);
        CollectionAssert.AreEqual(new[] { 2, 3, 4 }, next.Rows.Select(row => row.Number).ToArray());
        CollectionAssert.AreEqual(new[] { true, true, false }, next.Rows.Select(row => row.HasMatched).ToArray());

        Assert.AreEqual(ManualChallengeAction.None, flow.Next(new(ManualChallengeScene.Unknown, []), 1152).Action);
        flow.RecordScroll(next, 1152);
        var last = PositionedPage(388, "", "泥吼牙", "怖哭菇");
        Assert.AreEqual(ManualChallengeAction.ScrollDown, flow.Next(last, 1152).Action);
        CollectionAssert.AreEqual(new[] { 3, 4, 5 }, last.Rows.Select(row => row.Number).ToArray());
        Assert.IsTrue(last.Rows.All(row => row.HasMatched));
        CollectionAssert.AreEqual(new[] { "友爱星飞", "小皮球", "星云旅者", "泥吼牙", "怖哭菇" },
            flow.Options.Select(option => option.Name).ToArray());

        var newFlow = new ManualChallengeFlow(null);
        var newTop = PositionedPage(100, "怖哭菇");
        newFlow.Next(newTop, 1152);
        ExecuteScroll(newFlow, newTop);
        ExecuteScroll(newFlow, newTop);
        Assert.AreEqual(1, newTop.Rows[0].Number);
    }

    [TestMethod]
    public void DuplicateCatalogNamesHaveSeparateRowNumbersAndMatchHistories()
    {
        var flow = new ManualChallengeFlow(null);
        var top = PositionedPage(100, "小皮球", "小皮球", "");
        flow.Next(top, 1152);
        ExecuteScroll(flow, top);
        ExecuteScroll(flow, top);

        CollectionAssert.AreEqual(new[] { 1, 2, 3 }, top.Rows.Select(row => row.Number).ToArray());
        CollectionAssert.AreEqual(new[] { true, true, false }, top.Rows.Select(row => row.HasMatched).ToArray());
        Assert.HasCount(3, flow.Options);
        CollectionAssert.AreEqual(new[] { "小皮球", "小皮球" }, flow.Options.Take(2).Select(option => option.Name).ToArray());

        flow.RecordScroll(top, 1152);
        var next = PositionedPage(244, "", "", "小皮球");
        flow.Next(next, 1152);
        CollectionAssert.AreEqual(new[] { 2, 3, 4 }, next.Rows.Select(row => row.Number).ToArray());
        CollectionAssert.AreEqual(new[] { true, false, true }, next.Rows.Select(row => row.HasMatched).ToArray());
        Assert.HasCount(4, flow.Options);
        CollectionAssert.AreEqual(new[] { 1, 2, 3, 4 }, flow.Options.Select(option => option.Number).ToArray());
        Assert.AreEqual("小皮球", flow.Options[3].Name);
    }

    [TestMethod]
    public void SingleVisibleRowRetainsItsNumberAndMatchOnAnUnchangedBoundary()
    {
        var flow = new ManualChallengeFlow(null);
        var top = PositionedPage(300, "怖哭菇");
        flow.Next(top, 1152);
        ExecuteScroll(flow, top);
        ExecuteScroll(flow, top);
        Assert.AreEqual(1, top.Rows[0].Number);
        Assert.IsTrue(top.Rows[0].HasMatched);

        flow.RecordScroll(top, 1152);
        var missing = PositionedPage(300, "");
        Assert.AreEqual(ManualChallengeAction.ScrollDown, flow.Next(missing, 1152).Action);
        Assert.AreEqual(1, missing.Rows[0].Number);
        Assert.IsTrue(missing.Rows[0].HasMatched);
        Assert.AreEqual(ManualChallengeAction.Complete, ExecuteScroll(flow, missing).Action);
        Assert.AreEqual(1, missing.Rows[0].Number);
    }

    [TestMethod]
    public void ChallengeOnlyApproachesAfterMatchingTheTargetMapAndStartsOnMatchedBattle()
    {
        var flow = new ManualChallengeFlow(new FlowerSeedOption(1, "友爱星飞"));
        Assert.AreEqual(ManualChallengeAction.OpenManual, flow.Next(new(ManualChallengeScene.World, []), 1152).Action);
        Assert.AreEqual(ManualChallengeAction.None, flow.Next(new(ManualChallengeScene.Battle, []), 1152).Action);
        var top = Page("友爱星飞");
        Assert.AreEqual(ManualChallengeAction.ScrollUp, flow.Next(top, 1152).Action);
        Assert.AreEqual(ManualChallengeAction.ScrollUp, ExecuteScroll(flow, top).Action);
        Assert.AreEqual(ManualChallengeAction.Click, ExecuteScroll(flow, top).Action);
        Assert.AreEqual(ManualChallengeAction.None, flow.Next(new(ManualChallengeScene.Unknown, []), 1152).Action);
        Assert.AreEqual(ManualChallengeAction.Click, flow.Next(new(ManualChallengeScene.Map, [], Button, "友爱星飞"), 1152).Action);
        Assert.AreEqual(ManualChallengeAction.Approach, flow.Next(new(ManualChallengeScene.World, []), 1152).Action);
        Assert.AreEqual(ManualChallengeAction.SelectChallenge, flow.Next(new(ManualChallengeScene.Interaction, [], Text: "你好"), 1152).Action);
        Assert.AreEqual(ManualChallengeAction.Interact, flow.Next(new(ManualChallengeScene.Interaction, [], Text: "挑战！"), 1152).Action);
        Assert.AreEqual(ManualChallengeAction.Click, flow.Next(new(ManualChallengeScene.Preparation, [], Button), 1152).Action);
        Assert.AreEqual(ManualChallengeAction.None, flow.Next(new(ManualChallengeScene.World, []), 1152).Action);
        Assert.AreEqual(ManualChallengeAction.None, flow.Next(new(ManualChallengeScene.Unknown, []), 1152).Action);
        Assert.AreEqual(ManualChallengeAction.Battle, flow.Next(new(ManualChallengeScene.Battle, []), 1152).Action);
        Assert.IsTrue(flow.IsBattleActive);
        Assert.AreEqual(1, flow.BattleNumber);
        Assert.AreEqual(0, flow.CompletedCount);
    }

    [TestMethod]
    public void InitialFlowerInteractionOpensTheHandbookAndNeverStartsAnUnselectedChallenge()
    {
        var flow = new ManualChallengeFlow(new FlowerSeedOption(1, "友爱星飞"));
        Assert.AreEqual(ManualChallengeAction.OpenManual, flow.Next(new(ManualChallengeScene.Interaction, [], Text: "挑战"), 1152).Action);
        Assert.AreEqual(ManualChallengeAction.None, flow.Next(new(ManualChallengeScene.Confirmation, [], Button), 1152).Action);
        Assert.AreEqual(ManualChallengeAction.None, flow.Next(new(ManualChallengeScene.Preparation, [], Button), 1152).Action);
    }

    [TestMethod]
    public void MissingMatchedButtonsCannotIssueClicksOrStartBattle()
    {
        var flow = new ManualChallengeFlow(new FlowerSeedOption(1, "友爱星飞"));
        Assert.AreEqual(ManualChallengeAction.None, flow.Next(new(ManualChallengeScene.Manual, []), 1152).Action);
        Assert.AreEqual(ManualChallengeAction.None, flow.Next(new(ManualChallengeScene.Map, [], Text: "友爱星飞"), 1152).Action);
        Assert.AreEqual(ManualChallengeAction.None, flow.Next(new(ManualChallengeScene.Confirmation, []), 1152).Action);
        Assert.AreEqual(ManualChallengeAction.None, flow.Next(new(ManualChallengeScene.Battle, []), 1152).Action);
    }

    [TestMethod]
    public void MapCannotSendTeleportBeforeTheTargetRowHasBeenMatched()
    {
        var flow = new ManualChallengeFlow(new FlowerSeedOption(1, "友爱星飞"));
        Assert.AreEqual(ManualChallengeAction.None, flow.Next(new(ManualChallengeScene.Map, [], Button, "小皮球"), 1152).Action);
    }

    [TestMethod]
    public void RepeatedReadsKeepOneOptionPerNumberAndLaterCatalogMatchImprovesOnlyThatEntry()
    {
        var flow = new ManualChallengeFlow(null);
        var raw = Page("", "") with
        {
            Rows = [new("", Button, "电球羊羊"), new("", Button with { Y = 430 })]
        };
        flow.Next(raw, 1152);
        ExecuteScroll(flow, raw);
        ExecuteScroll(flow, raw);
        Assert.HasCount(2, flow.Options);
        Assert.AreEqual("电球羊羊", flow.Options[0].Name);

        var matched = Page("电球咩咩", "小皮球");
        for (var i = 0; i < 3; i++) flow.Next(matched, 1152);
        Assert.HasCount(2, flow.Options);
        CollectionAssert.AreEqual(new[] { 1, 2 }, flow.Options.Select(option => option.Number).ToArray());
        CollectionAssert.AreEqual(new[] { "电球咩咩", "小皮球" }, flow.Options.Select(option => option.Name).ToArray());

        flow.Next(raw, 1152);
        CollectionAssert.AreEqual(new[] { "电球咩咩", "小皮球" }, flow.Options.Select(option => option.Name).ToArray());
        Assert.IsTrue(raw.Rows.All(row => row.HasMatched));
    }

    [TestMethod]
    public void ChallengeSelectsTheRequestedDuplicateNumberInsteadOfTheFirstMatchingName()
    {
        var flow = new ManualChallengeFlow(new FlowerSeedOption(2, "小皮球"));
        var top = Page("小皮球", "小皮球");
        Assert.AreEqual(ManualChallengeAction.ScrollUp, flow.Next(top, 1152).Action);
        Assert.AreEqual(ManualChallengeAction.ScrollUp, ExecuteScroll(flow, top).Action);

        var selected = ExecuteScroll(flow, top);

        Assert.AreEqual(ManualChallengeAction.Click, selected.Action);
        Assert.AreSame(top.Rows[1].Button, selected.Button);
        CollectionAssert.AreEqual(new[] { 1, 2 }, top.Rows.Select(row => row.Number).ToArray());
    }

    [TestMethod]
    public void TargetNumberIsReachedAfterScrollingEvenWithAnUnmatchedOrEmptyOcrName()
    {
        var flow = new ManualChallengeFlow(new FlowerSeedOption(4, "电球羊羊"));
        var top = PositionedPage(100, "小皮球", "友爱星飞", "怖哭菇");
        flow.Next(top, 1152);
        ExecuteScroll(flow, top);
        Assert.AreEqual(ManualChallengeAction.ScrollDown, ExecuteScroll(flow, top).Action);

        flow.RecordScroll(top, 1152);
        var next = PositionedPage(244, "友爱星飞", "怖哭菇", "");
        var selected = flow.Next(next, 1152);

        Assert.AreEqual(4, next.Rows[2].Number);
        Assert.AreEqual(ManualChallengeAction.Click, selected.Action);
        Assert.AreSame(next.Rows[2].Button, selected.Button);
        Assert.AreEqual(ManualChallengeAction.Click, flow.Next(new(ManualChallengeScene.Map, [], Button, "电球咩咩"), 1152).Action);
        Assert.AreEqual(ManualChallengeAction.Click, flow.Next(new(ManualChallengeScene.Map, [], Button), 1152).Action);
    }

    [TestMethod]
    public void TargetNumberOutsideTheCurrentListFailsRatherThanSelectingAnotherDuplicate()
    {
        var flow = new ManualChallengeFlow(new FlowerSeedOption(3, "小皮球"));
        var top = Page("小皮球", "小皮球");
        flow.Next(top, 1152);
        ExecuteScroll(flow, top);
        Assert.AreEqual(ManualChallengeAction.ScrollDown, ExecuteScroll(flow, top).Action);
        Assert.AreEqual(ManualChallengeAction.ScrollDown, ExecuteScroll(flow, top).Action);
        Assert.ThrowsExactly<InvalidOperationException>(() => ExecuteScroll(flow, top));
    }

    [TestMethod]
    [DataRow(84)]
    [DataRow(168)]
    public void FullTwentyFourRowScanKeepsEveryEntryInOrderAcrossPartialScrolls(int step)
    {
        const int lastOffset = 19 * 228;
        var flow = new ManualChallengeFlow(null);
        var screen = AtOffset(0);
        flow.Next(screen, 1152);
        ExecuteScroll(flow, screen);
        ExecuteScroll(flow, screen);

        for (var offset = step; offset < lastOffset + step; offset += step)
        {
            flow.RecordScroll(screen, 1152);
            screen = AtOffset(Math.Min(offset, lastOffset));
            Assert.AreEqual(ManualChallengeAction.ScrollDown, flow.Next(screen, 1152).Action);
            foreach (var row in screen.Rows)
                Assert.AreEqual((row.Button.Y + Button.Height / 2 + Math.Min(offset, lastOffset) - 100) / 228 + 1,
                    row.Number, $"滚动至 {offset} 时行号错误");
        }

        Assert.AreEqual(ManualChallengeAction.ScrollDown, ExecuteScroll(flow, screen).Action);
        Assert.AreEqual(ManualChallengeAction.Complete, ExecuteScroll(flow, screen).Action);
        Assert.HasCount(24, flow.Options);
        CollectionAssert.AreEqual(Enumerable.Range(1, 24).ToArray(), flow.Options.Select(option => option.Number).ToArray());
        Assert.AreEqual("小皮球", flow.Options[4].Name);
        Assert.AreEqual("小皮球", flow.Options[5].Name);
        Assert.AreEqual("电球羊羊", flow.Options[10].Name);
        Assert.AreEqual(12, flow.Options[11].Number);

        static ManualChallengeScreen AtOffset(int offset) => new(ManualChallengeScene.FlowerList,
            Enumerable.Range(0, 24)
                .Where(index => 100 + index * 228 - offset is >= 100 and <= 1012)
                .Select(index => new ManualChallengeRow(index switch
                {
                    4 or 5 => "小皮球",
                    10 or 11 => "",
                    _ => $"花种{index + 1}"
                }, Button with { Y = 100 + index * 228 - offset - Button.Height / 2 }, index == 10 ? "电球羊羊" : ""))
                .ToArray());
    }

    [TestMethod]
    public void OptionalMedalStartAndUnknownAnimationsKeepTheCurrentChallenge()
    {
        var flow = ReadyChallenge();
        Assert.AreEqual(ManualChallengeAction.None, flow.Next(new(ManualChallengeScene.MedalConfirmation, []), 1152).Action);
        Assert.AreEqual(ManualChallengeAction.Click, flow.Next(new(ManualChallengeScene.MedalConfirmation, [], Button), 1152).Action);
        Assert.AreEqual(ManualChallengeAction.None, flow.Next(new(ManualChallengeScene.Unknown, []), 1152).Action);
        StartBattle(flow);
        Assert.AreEqual(ManualChallengeAction.Battle, flow.Next(new(ManualChallengeScene.Battle, []), 1152).Action);
        Assert.AreEqual(1, flow.BattleNumber);
        Assert.AreEqual(ManualChallengeAction.None, flow.Next(new(ManualChallengeScene.Unknown, []), 1152).Action);
        Assert.IsTrue(flow.IsBattleActive);
        Assert.AreEqual(ManualChallengeAction.Capture, flow.Next(new(ManualChallengeScene.Capture, []), 1152).Action);
        Assert.AreEqual(0, flow.CompletedCount);
        Assert.AreEqual(0, flow.ConsecutiveFailures);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void CaptureTransitionsDoNotCountAsFailureOrBlockRewards(bool interactionVisible)
    {
        var flow = ReadyChallenge(2);
        FailBattle(flow);
        FailBattle(flow);
        StartBattle(flow);
        Assert.AreEqual(ManualChallengeAction.Capture, flow.Next(new(ManualChallengeScene.Capture, []), 1152).Action);
        var transition = interactionVisible ? ManualChallengeScene.Interaction : ManualChallengeScene.World;
        for (var i = 0; i < 3; i++)
            Assert.AreEqual(ManualChallengeAction.None, flow.Next(new(transition, [], Text: "挑战"), 1152).Action);
        Assert.AreEqual(ManualChallengeAction.None, flow.Next(new(ManualChallengeScene.Unknown, []), 1152).Action);
        Assert.AreEqual(2, flow.ConsecutiveFailures);
        Assert.AreEqual(0, flow.CompletedCount);
        Assert.AreEqual(ManualChallengeAction.Click, flow.Next(new(ManualChallengeScene.Rewards, [], Button), 1152).Action);
        Assert.AreEqual(ManualChallengeAction.None, flow.Next(new(transition, [], Text: "挑战"), 1152).Action);
        var result = new ManualChallengeScreen(ManualChallengeScene.Result, [], Button, ExitButton: Button);
        Assert.AreEqual(ManualChallengeAction.Click, flow.Next(result, 1152).Action);
        Assert.AreEqual(ManualChallengeAction.Click, flow.Next(result, 1152).Action);
        Assert.AreEqual(1, flow.CompletedCount);
        Assert.AreEqual(0, flow.ConsecutiveFailures);
        StartBattle(flow);
        Assert.AreEqual(4, flow.BattleNumber);
    }

    [TestMethod]
    public void DefaultChallengeRepeatsSixSuccessfulSettlementsThenExitsAndConfirmsWorld()
    {
        var flow = ReadyChallenge();
        var exit = Button with { X = 777, TemplatePath = "exit" };
        var result = new ManualChallengeScreen(ManualChallengeScene.Result, [], Button, ExitButton: exit);
        for (var number = 1; number <= 6; number++)
        {
            StartBattle(flow);
            Assert.AreEqual(number, flow.BattleNumber);
            Assert.AreEqual(ManualChallengeAction.Click, flow.Next(new(ManualChallengeScene.Rewards, [], Button), 1152).Action);
            Assert.AreEqual(number - 1, flow.CompletedCount);
            var decision = flow.Next(result, 1152);
            Assert.AreEqual(ManualChallengeAction.Click, decision.Action);
            Assert.AreSame(number == 6 ? exit : Button, decision.Button);
            Assert.AreEqual(number, flow.CompletedCount);
            Assert.IsFalse(flow.IsBattleActive);
            for (var i = 0; i < 3; i++)
            {
                Assert.AreEqual(ManualChallengeAction.Click, flow.Next(result, 1152).Action);
                Assert.AreEqual(number, flow.CompletedCount);
            }
            Assert.AreEqual(ManualChallengeAction.None, flow.Next(new(ManualChallengeScene.Rewards, [], Button), 1152).Action);
        }
        Assert.AreEqual(ManualChallengeAction.None, flow.Next(new(ManualChallengeScene.Battle, []), 1152).Action);
        Assert.AreEqual(ManualChallengeAction.None, flow.Next(new(ManualChallengeScene.Unknown, []), 1152).Action);
        Assert.AreEqual(ManualChallengeAction.Complete, flow.Next(new(ManualChallengeScene.World, []), 1152).Action);
        Assert.AreEqual(ManualChallengeAction.Complete, flow.Next(new(ManualChallengeScene.Interaction, [], Text: "挑战"), 1152).Action);
    }

    [TestMethod]
    public void ResultWithoutCurrentRewardsCannotCountOrIssueRetryAndMissingButtonsWait()
    {
        var flow = ReadyChallenge(1);
        StartBattle(flow);
        Assert.AreEqual(ManualChallengeAction.None, flow.Next(new(ManualChallengeScene.Result, [], Button, ExitButton: Button), 1152).Action);
        Assert.AreEqual(ManualChallengeAction.None, flow.Next(new(ManualChallengeScene.Rewards, []), 1152).Action);
        Assert.AreEqual(ManualChallengeAction.None, flow.Next(new(ManualChallengeScene.Result, [], Button, ExitButton: Button), 1152).Action);
        Assert.AreEqual(0, flow.CompletedCount);
        Assert.AreEqual(ManualChallengeAction.Click, flow.Next(new(ManualChallengeScene.Rewards, [], Button), 1152).Action);
        Assert.AreEqual(ManualChallengeAction.None, flow.Next(new(ManualChallengeScene.Result, [], Button), 1152).Action);
        Assert.AreEqual(1, flow.CompletedCount);
        Assert.AreEqual(ManualChallengeAction.Click, flow.Next(new(ManualChallengeScene.Result, [], ExitButton: Button), 1152).Action);
        Assert.AreEqual(1, flow.CompletedCount);
    }

    [TestMethod]
    public void FailureRetriesAtTheCurrentFlowerAndRepeatedWorldFramesOnlyCountOnce()
    {
        var flow = ReadyChallenge();
        StartBattle(flow);
        Assert.AreEqual(ManualChallengeAction.Approach, flow.Next(new(ManualChallengeScene.World, []), 1152).Action);
        for (var i = 0; i < 3; i++)
            Assert.AreEqual(ManualChallengeAction.Approach, flow.Next(new(ManualChallengeScene.World, []), 1152).Action);
        Assert.AreEqual(1, flow.ConsecutiveFailures);
        Assert.AreEqual(0, flow.CompletedCount);
        Assert.IsFalse(flow.IsBattleActive);
        Assert.AreEqual(ManualChallengeAction.Interact, flow.Next(new(ManualChallengeScene.Interaction, [], Text: "挑战"), 1152).Action);
        Assert.AreEqual(ManualChallengeAction.Click, flow.Next(new(ManualChallengeScene.Confirmation, [], Button), 1152).Action);
        Assert.AreEqual(ManualChallengeAction.Click, flow.Next(new(ManualChallengeScene.Preparation, [], Button), 1152).Action);
        StartBattle(flow);
        Assert.AreEqual(2, flow.BattleNumber);
        Assert.AreEqual(ManualChallengeAction.Interact, flow.Next(new(ManualChallengeScene.Interaction, [], Text: "挑战"), 1152).Action);
        Assert.AreEqual(2, flow.ConsecutiveFailures);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void ReturningToTheFlowerAfterSuccessfulResultDoesNotCountAsFailure(bool interactionVisible)
    {
        var flow = ReadyChallenge(2);
        StartBattle(flow);
        flow.Next(new(ManualChallengeScene.Rewards, [], Button), 1152);
        flow.Next(new(ManualChallengeScene.Result, [], Button, ExitButton: Button), 1152);
        var scene = interactionVisible ? ManualChallengeScene.Interaction : ManualChallengeScene.World;
        Assert.AreEqual(interactionVisible ? ManualChallengeAction.Interact : ManualChallengeAction.Approach,
            flow.Next(new(scene, [], Text: "挑战"), 1152).Action);
        Assert.AreEqual(1, flow.CompletedCount);
        Assert.AreEqual(0, flow.ConsecutiveFailures);
    }

    [TestMethod]
    public void ThreeConsecutiveFailuresStopButSuccessfulSettlementClearsFailures()
    {
        var flow = ReadyChallenge(2);
        FailBattle(flow);
        FailBattle(flow);
        Assert.AreEqual(2, flow.ConsecutiveFailures);
        StartBattle(flow);
        flow.Next(new(ManualChallengeScene.Rewards, [], Button), 1152);
        flow.Next(new(ManualChallengeScene.Result, [], Button, ExitButton: Button), 1152);
        Assert.AreEqual(0, flow.ConsecutiveFailures);
        Assert.AreEqual(1, flow.CompletedCount);
        FailBattle(flow);
        FailBattle(flow);
        StartBattle(flow);
        var failure = Assert.ThrowsExactly<InvalidOperationException>(() =>
            flow.Next(new(ManualChallengeScene.World, []), 1152));
        StringAssert.Contains(failure.Message, "连续失败 3 次");
        Assert.AreEqual(3, flow.ConsecutiveFailures);
        Assert.AreEqual(1, flow.CompletedCount);
    }

    [TestMethod]
    public void MissingStartButtonAndOutOfOrderCaptureCannotStartAChallenge()
    {
        var flow = ReadyChallenge(startPreparation: false);
        Assert.AreEqual(ManualChallengeAction.None, flow.Next(new(ManualChallengeScene.Preparation, []), 1152).Action);
        Assert.AreEqual(ManualChallengeAction.None, flow.Next(new(ManualChallengeScene.Battle, []), 1152).Action);
        Assert.AreEqual(ManualChallengeAction.None, flow.Next(new(ManualChallengeScene.Capture, []), 1152).Action);
        Assert.AreEqual(ManualChallengeAction.None, flow.Next(new(ManualChallengeScene.Rewards, [], Button), 1152).Action);
        Assert.AreEqual(ManualChallengeAction.None, flow.Next(new(ManualChallengeScene.Result, [], Button), 1152).Action);
        Assert.AreEqual(0, flow.BattleNumber);
        Assert.AreEqual(0, flow.CompletedCount);
    }

    private static ManualChallengeFlow ReadyChallenge(int runCount = 6, bool startPreparation = true)
    {
        var flow = new ManualChallengeFlow(new FlowerSeedOption(1, "友爱星飞"), runCount);
        var top = Page("友爱星飞");
        flow.Next(top, 1152);
        ExecuteScroll(flow, top);
        ExecuteScroll(flow, top);
        flow.Next(new(ManualChallengeScene.Map, [], Button), 1152);
        if (startPreparation) flow.Next(new(ManualChallengeScene.Preparation, [], Button), 1152);
        return flow;
    }

    private static void StartBattle(ManualChallengeFlow flow)
        => Assert.AreEqual(ManualChallengeAction.Battle, flow.Next(new(ManualChallengeScene.Battle, []), 1152).Action);

    private static void FailBattle(ManualChallengeFlow flow)
    {
        StartBattle(flow);
        Assert.AreEqual(ManualChallengeAction.Approach, flow.Next(new(ManualChallengeScene.World, []), 1152).Action);
        Assert.AreEqual(ManualChallengeAction.Click, flow.Next(new(ManualChallengeScene.Preparation, [], Button), 1152).Action);
    }

    private static ManualChallengeDecision ExecuteScroll(ManualChallengeFlow flow, ManualChallengeScreen screen)
    {
        flow.RecordScroll(screen, 1152);
        return flow.Next(screen, 1152);
    }

    private static ManualChallengeScreen Shift(ManualChallengeScreen screen, int pixels)
        => screen with { Rows = screen.Rows.Select(row => row with { Button = row.Button with { Y = row.Button.Y + pixels } }).ToArray() };

    private static ManualChallengeScreen PositionedPage(int firstCenterY, params string[] names)
        => new(ManualChallengeScene.FlowerList,
            names.Select((name, index) => new ManualChallengeRow(name,
                Button with { Y = firstCenterY + index * 228 - Button.Height / 2 })).ToArray());
}
