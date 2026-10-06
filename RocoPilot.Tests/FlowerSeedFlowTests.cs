using Microsoft.VisualStudio.TestTools.UnitTesting;
using RocoPilot.Models.ImageMatching;
using RocoPilot.Models.Runtime;
using RocoPilot.Services.IndependentTasks;

namespace RocoPilot.Tests;

[TestClass]
public sealed class FlowerSeedFlowTests
{
    private static readonly ImageMatchResult Button = new(true, .99, 1740, 200, 80, 40, "button");
    private static FlowerSeedScreen Page(params string[] names)
        => new(FlowerSeedScene.FlowerList,
            names.Select((name, index) => new FlowerSeedRow(name, Button with { Y = 200 + index * 230 })).ToArray());

    [TestMethod]
    public void ScanUsesCanonicalNamesOnceAndRequiresExecutedScrollsToConfirmBothEdges()
    {
        var flow = new FlowerSeedFlow(null);
        var top = PositionedPage(100, "小皮球", "友爱星飞");
        for (var i = 0; i < 5; i++) Assert.AreEqual(FlowerSeedAction.ScrollUp, flow.Next(top, 1152).Action);
        Assert.AreEqual(0, flow.Options.Count);
        Assert.AreEqual(FlowerSeedAction.ScrollUp, ExecuteScroll(flow, top).Action);
        Assert.AreEqual(FlowerSeedAction.ScrollDown, ExecuteScroll(flow, top).Action);
        CollectionAssert.AreEqual(new[] { "小皮球", "友爱星飞" }, flow.Options.Select(option => option.Name).ToArray());

        var next = PositionedPage(244, "友爱星飞", "星云旅者");
        flow.RecordScroll(top, 1152);
        Assert.AreEqual(FlowerSeedAction.ScrollDown, flow.Next(next, 1152).Action);
        for (var i = 0; i < 5; i++) Assert.AreEqual(FlowerSeedAction.ScrollDown, flow.Next(next, 1152).Action);
        Assert.AreEqual(FlowerSeedAction.ScrollDown, ExecuteScroll(flow, next).Action);
        Assert.AreEqual(FlowerSeedAction.Complete, ExecuteScroll(flow, next).Action);
        CollectionAssert.AreEqual(new[] { "小皮球", "友爱星飞", "星云旅者" }, flow.Options.Select(option => option.Name).ToArray());
        CollectionAssert.AreEqual(new[] { 1, 2, 3 }, flow.Options.Select(option => option.Number).ToArray());
        Assert.AreEqual(0, new FlowerSeedFlow(null).Options.Count);
    }

    [TestMethod]
    public void UnmatchedOcrAndEmptyNamesRemainOptionsAndDoNotBlockScrolling()
    {
        var flow = new FlowerSeedFlow(null);
        var top = Page("小皮球", "", "");
        top = top with { Rows = top.Rows.Select((row, index) => index == 1 ? row with { RawName = "电球羊羊" } : row).ToArray() };
        Assert.AreEqual(FlowerSeedAction.ScrollUp, flow.Next(top, 1152).Action);
        Assert.AreEqual(FlowerSeedAction.ScrollUp, ExecuteScroll(flow, top).Action);
        Assert.AreEqual(FlowerSeedAction.ScrollDown, ExecuteScroll(flow, top).Action);
        Assert.HasCount(3, flow.Options);
        CollectionAssert.AreEqual(new[] { 1, 2, 3 }, flow.Options.Select(option => option.Number).ToArray());
        Assert.AreEqual("小皮球", flow.Options[0].Name);
        Assert.AreEqual("电球羊羊", flow.Options[1].Name);
        Assert.AreEqual(FlowerSeedAction.ScrollDown, ExecuteScroll(flow, top).Action);
        Assert.AreEqual(FlowerSeedAction.Complete, ExecuteScroll(flow, top).Action);
        Assert.HasCount(3, flow.Options);
        Assert.AreEqual(3, flow.Options[2].Number);
    }

    [TestMethod]
    public void MissingNamesDoNotInvalidateAnUnchangedMatchedListBoundary()
    {
        var flow = new FlowerSeedFlow(null);
        flow.RecordScroll(Page("", "友爱星飞"), 1152);
        Assert.AreEqual(FlowerSeedAction.ScrollUp, flow.Next(Page("小皮球", ""), 1152).Action);
        flow.RecordScroll(Page("小皮球", ""), 1152);
        var top = Page("", "友爱星飞");
        Assert.AreEqual(FlowerSeedAction.ScrollDown, flow.Next(top, 1152).Action);
        Assert.HasCount(2, flow.Options);
        Assert.AreEqual("友爱星飞", flow.Options[1].Name);
    }

    [TestMethod]
    public void MatchingRowPositionsMatterEvenWhenNamesRemainTheSame()
    {
        var flow = new FlowerSeedFlow(null);
        var top = Page("小皮球", "友爱星飞");
        flow.RecordScroll(top, 1152);
        var shifted = Shift(top, -50);
        Assert.AreEqual(FlowerSeedAction.ScrollUp, flow.Next(shifted, 1152).Action);
        Assert.AreEqual(FlowerSeedAction.ScrollUp, ExecuteScroll(flow, shifted).Action);
    }

    [TestMethod]
    public void ScanKeepsGlobalRowNumbersAndSuccessfulMatchesAcrossScrolledFrames()
    {
        var flow = new FlowerSeedFlow(null);
        var top = PositionedPage(100, "友爱星飞", "小皮球", "星云旅者");
        Assert.AreEqual(FlowerSeedAction.ScrollUp, flow.Next(top, 1152).Action);
        Assert.IsTrue(top.Rows.All(row => row.Number == 0));
        Assert.AreEqual(FlowerSeedAction.ScrollUp, ExecuteScroll(flow, top).Action);
        Assert.AreEqual(FlowerSeedAction.ScrollDown, ExecuteScroll(flow, top).Action);
        CollectionAssert.AreEqual(new[] { 1, 2, 3 }, top.Rows.Select(row => row.Number).ToArray());
        Assert.IsTrue(top.Rows.All(row => row.HasMatched));

        flow.RecordScroll(top, 1152);
        var next = PositionedPage(244, "", "星云旅者", "");
        Assert.AreEqual(FlowerSeedAction.ScrollDown, flow.Next(next, 1152).Action);
        CollectionAssert.AreEqual(new[] { 2, 3, 4 }, next.Rows.Select(row => row.Number).ToArray());
        CollectionAssert.AreEqual(new[] { true, true, false }, next.Rows.Select(row => row.HasMatched).ToArray());

        Assert.AreEqual(FlowerSeedAction.None, flow.Next(new(FlowerSeedScene.Unknown, []), 1152).Action);
        flow.RecordScroll(next, 1152);
        var last = PositionedPage(388, "", "泥吼牙", "怖哭菇");
        Assert.AreEqual(FlowerSeedAction.ScrollDown, flow.Next(last, 1152).Action);
        CollectionAssert.AreEqual(new[] { 3, 4, 5 }, last.Rows.Select(row => row.Number).ToArray());
        Assert.IsTrue(last.Rows.All(row => row.HasMatched));
        CollectionAssert.AreEqual(new[] { "友爱星飞", "小皮球", "星云旅者", "泥吼牙", "怖哭菇" },
            flow.Options.Select(option => option.Name).ToArray());

        var newFlow = new FlowerSeedFlow(null);
        var newTop = PositionedPage(100, "怖哭菇");
        newFlow.Next(newTop, 1152);
        ExecuteScroll(newFlow, newTop);
        ExecuteScroll(newFlow, newTop);
        Assert.AreEqual(1, newTop.Rows[0].Number);
    }

    [TestMethod]
    public void DuplicateCatalogNamesHaveSeparateRowNumbersAndMatchHistories()
    {
        var flow = new FlowerSeedFlow(null);
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
        var flow = new FlowerSeedFlow(null);
        var top = PositionedPage(300, "怖哭菇");
        flow.Next(top, 1152);
        ExecuteScroll(flow, top);
        ExecuteScroll(flow, top);
        Assert.AreEqual(1, top.Rows[0].Number);
        Assert.IsTrue(top.Rows[0].HasMatched);

        flow.RecordScroll(top, 1152);
        var missing = PositionedPage(300, "");
        Assert.AreEqual(FlowerSeedAction.ScrollDown, flow.Next(missing, 1152).Action);
        Assert.AreEqual(1, missing.Rows[0].Number);
        Assert.IsTrue(missing.Rows[0].HasMatched);
        Assert.AreEqual(FlowerSeedAction.Complete, ExecuteScroll(flow, missing).Action);
        Assert.AreEqual(1, missing.Rows[0].Number);
    }

    [TestMethod]
    public void ChallengeOnlyApproachesAfterMatchingTheTargetMapAndOnlyFinishesOnMatchedBattle()
    {
        var flow = new FlowerSeedFlow(new FlowerSeedOption(1, "友爱星飞"));
        Assert.AreEqual(FlowerSeedAction.OpenManual, flow.Next(new(FlowerSeedScene.World, []), 1152).Action);
        Assert.AreEqual(FlowerSeedAction.None, flow.Next(new(FlowerSeedScene.Battle, []), 1152).Action);
        var top = Page("友爱星飞");
        Assert.AreEqual(FlowerSeedAction.ScrollUp, flow.Next(top, 1152).Action);
        Assert.AreEqual(FlowerSeedAction.ScrollUp, ExecuteScroll(flow, top).Action);
        Assert.AreEqual(FlowerSeedAction.Click, ExecuteScroll(flow, top).Action);
        Assert.AreEqual(FlowerSeedAction.None, flow.Next(new(FlowerSeedScene.Unknown, []), 1152).Action);
        Assert.AreEqual(FlowerSeedAction.Click, flow.Next(new(FlowerSeedScene.Map, [], Button, "友爱星飞"), 1152).Action);
        Assert.AreEqual(FlowerSeedAction.Approach, flow.Next(new(FlowerSeedScene.World, []), 1152).Action);
        Assert.AreEqual(FlowerSeedAction.SelectChallenge, flow.Next(new(FlowerSeedScene.Interaction, [], Text: "你好"), 1152).Action);
        Assert.AreEqual(FlowerSeedAction.Interact, flow.Next(new(FlowerSeedScene.Interaction, [], Text: "挑战！"), 1152).Action);
        Assert.AreEqual(FlowerSeedAction.Click, flow.Next(new(FlowerSeedScene.Preparation, [], Button), 1152).Action);
        Assert.AreEqual(FlowerSeedAction.None, flow.Next(new(FlowerSeedScene.World, []), 1152).Action);
        Assert.AreEqual(FlowerSeedAction.None, flow.Next(new(FlowerSeedScene.Unknown, []), 1152).Action);
        Assert.AreEqual(FlowerSeedAction.Complete, flow.Next(new(FlowerSeedScene.Battle, []), 1152).Action);
    }

    [TestMethod]
    public void InitialFlowerInteractionOpensTheHandbookAndNeverStartsAnUnselectedChallenge()
    {
        var flow = new FlowerSeedFlow(new FlowerSeedOption(1, "友爱星飞"));
        Assert.AreEqual(FlowerSeedAction.OpenManual, flow.Next(new(FlowerSeedScene.Interaction, [], Text: "挑战"), 1152).Action);
        Assert.AreEqual(FlowerSeedAction.None, flow.Next(new(FlowerSeedScene.Confirmation, [], Button), 1152).Action);
        Assert.AreEqual(FlowerSeedAction.None, flow.Next(new(FlowerSeedScene.Preparation, [], Button), 1152).Action);
    }

    [TestMethod]
    public void MissingMatchedButtonsCannotIssueClicksOrAdvanceToBattleCompletion()
    {
        var flow = new FlowerSeedFlow(new FlowerSeedOption(1, "友爱星飞"));
        Assert.AreEqual(FlowerSeedAction.None, flow.Next(new(FlowerSeedScene.Manual, []), 1152).Action);
        Assert.AreEqual(FlowerSeedAction.None, flow.Next(new(FlowerSeedScene.Map, [], Text: "友爱星飞"), 1152).Action);
        Assert.AreEqual(FlowerSeedAction.None, flow.Next(new(FlowerSeedScene.Confirmation, []), 1152).Action);
        Assert.AreEqual(FlowerSeedAction.None, flow.Next(new(FlowerSeedScene.Battle, []), 1152).Action);
    }

    [TestMethod]
    public void MapCannotSendTeleportBeforeTheTargetRowHasBeenMatched()
    {
        var flow = new FlowerSeedFlow(new FlowerSeedOption(1, "友爱星飞"));
        Assert.AreEqual(FlowerSeedAction.None, flow.Next(new(FlowerSeedScene.Map, [], Button, "小皮球"), 1152).Action);
    }

    [TestMethod]
    public void RepeatedReadsKeepOneOptionPerNumberAndLaterCatalogMatchImprovesOnlyThatEntry()
    {
        var flow = new FlowerSeedFlow(null);
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
        var flow = new FlowerSeedFlow(new FlowerSeedOption(2, "小皮球"));
        var top = Page("小皮球", "小皮球");
        Assert.AreEqual(FlowerSeedAction.ScrollUp, flow.Next(top, 1152).Action);
        Assert.AreEqual(FlowerSeedAction.ScrollUp, ExecuteScroll(flow, top).Action);

        var selected = ExecuteScroll(flow, top);

        Assert.AreEqual(FlowerSeedAction.Click, selected.Action);
        Assert.AreSame(top.Rows[1].Button, selected.Button);
        CollectionAssert.AreEqual(new[] { 1, 2 }, top.Rows.Select(row => row.Number).ToArray());
    }

    [TestMethod]
    public void TargetNumberIsReachedAfterScrollingEvenWithAnUnmatchedOrEmptyOcrName()
    {
        var flow = new FlowerSeedFlow(new FlowerSeedOption(4, "电球羊羊"));
        var top = PositionedPage(100, "小皮球", "友爱星飞", "怖哭菇");
        flow.Next(top, 1152);
        ExecuteScroll(flow, top);
        Assert.AreEqual(FlowerSeedAction.ScrollDown, ExecuteScroll(flow, top).Action);

        flow.RecordScroll(top, 1152);
        var next = PositionedPage(244, "友爱星飞", "怖哭菇", "");
        var selected = flow.Next(next, 1152);

        Assert.AreEqual(4, next.Rows[2].Number);
        Assert.AreEqual(FlowerSeedAction.Click, selected.Action);
        Assert.AreSame(next.Rows[2].Button, selected.Button);
        Assert.AreEqual(FlowerSeedAction.Click, flow.Next(new(FlowerSeedScene.Map, [], Button, "电球咩咩"), 1152).Action);
        Assert.AreEqual(FlowerSeedAction.Click, flow.Next(new(FlowerSeedScene.Map, [], Button), 1152).Action);
    }

    [TestMethod]
    public void TargetNumberOutsideTheCurrentListFailsRatherThanSelectingAnotherDuplicate()
    {
        var flow = new FlowerSeedFlow(new FlowerSeedOption(3, "小皮球"));
        var top = Page("小皮球", "小皮球");
        flow.Next(top, 1152);
        ExecuteScroll(flow, top);
        Assert.AreEqual(FlowerSeedAction.ScrollDown, ExecuteScroll(flow, top).Action);
        Assert.AreEqual(FlowerSeedAction.ScrollDown, ExecuteScroll(flow, top).Action);
        Assert.ThrowsExactly<InvalidOperationException>(() => ExecuteScroll(flow, top));
    }

    [TestMethod]
    public void FullTwentyFourRowScanKeepsEveryEntryInOrderAcrossPartialScrolls()
    {
        const int lastOffset = 19 * 228;
        var flow = new FlowerSeedFlow(null);
        var screen = AtOffset(0);
        flow.Next(screen, 1152);
        ExecuteScroll(flow, screen);
        ExecuteScroll(flow, screen);

        for (var offset = 84; offset < lastOffset + 84; offset += 84)
        {
            flow.RecordScroll(screen, 1152);
            screen = AtOffset(Math.Min(offset, lastOffset));
            Assert.AreEqual(FlowerSeedAction.ScrollDown, flow.Next(screen, 1152).Action);
        }

        Assert.AreEqual(FlowerSeedAction.ScrollDown, ExecuteScroll(flow, screen).Action);
        Assert.AreEqual(FlowerSeedAction.Complete, ExecuteScroll(flow, screen).Action);
        Assert.HasCount(24, flow.Options);
        CollectionAssert.AreEqual(Enumerable.Range(1, 24).ToArray(), flow.Options.Select(option => option.Number).ToArray());
        Assert.AreEqual("小皮球", flow.Options[4].Name);
        Assert.AreEqual("小皮球", flow.Options[5].Name);
        Assert.AreEqual("电球羊羊", flow.Options[10].Name);
        Assert.AreEqual(12, flow.Options[11].Number);

        static FlowerSeedScreen AtOffset(int offset) => new(FlowerSeedScene.FlowerList,
            Enumerable.Range(0, 24)
                .Where(index => 100 + index * 228 - offset is >= 100 and <= 1012)
                .Select(index => new FlowerSeedRow(index switch
                {
                    4 or 5 => "小皮球",
                    10 or 11 => "",
                    _ => $"花种{index + 1}"
                }, Button with { Y = 100 + index * 228 - offset - Button.Height / 2 }, index == 10 ? "电球羊羊" : ""))
                .ToArray());
    }

    private static FlowerSeedDecision ExecuteScroll(FlowerSeedFlow flow, FlowerSeedScreen screen)
    {
        flow.RecordScroll(screen, 1152);
        return flow.Next(screen, 1152);
    }

    private static FlowerSeedScreen Shift(FlowerSeedScreen screen, int pixels)
        => screen with { Rows = screen.Rows.Select(row => row with { Button = row.Button with { Y = row.Button.Y + pixels } }).ToArray() };

    private static FlowerSeedScreen PositionedPage(int firstCenterY, params string[] names)
        => new(FlowerSeedScene.FlowerList,
            names.Select((name, index) => new FlowerSeedRow(name,
                Button with { Y = firstCenterY + index * 228 - Button.Height / 2 })).ToArray());
}
