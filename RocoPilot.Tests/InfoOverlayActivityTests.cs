using Microsoft.VisualStudio.TestTools.UnitTesting;
using RocoPilot.Models.Overlay;

namespace RocoPilot.Tests;

[TestClass]
public sealed class InfoOverlayActivityTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 4, 12, 0, 0, TimeSpan.FromHours(8));

    [TestMethod]
    public void LateOcrAndInputCannotOverwriteANewBattleOrTurn()
    {
        var tracker = new InfoOverlayActivityTracker();
        tracker.ResetBattle(10);
        tracker.BeginTurn(1);
        tracker.ResetBattle(11);
        tracker.BeginTurn(2);
        var current = tracker.RecognizeSpirit(11, 2, "栗鼠", Now);
        Assert.IsNull(tracker.RecognizeSpirit(10, 1, "旧精灵", Now));
        Assert.IsNull(tracker.Publish(11, 1, "old-input", InfoOverlayActivityKind.Skill, "旧动作", "", Now, true));
        Assert.AreSame(current, tracker.Current);
    }

    [TestMethod]
    public void LongSequenceStaysExpandedUntilCompletionAndRetriesDoNotExtendExpiry()
    {
        var tracker = new InfoOverlayActivityTracker();
        tracker.ResetBattle(1);
        tracker.BeginTurn(3);
        var started = tracker.Publish(1, 3, "input", InfoOverlayActivityKind.Skill, "执行公共序列", "1, Space", Now, false);
        Assert.IsNotNull(Present(started, Now.AddSeconds(30)));
        var completed = tracker.Publish(1, 3, "input", InfoOverlayActivityKind.Skill, "按键已发送", "等待界面变化", Now.AddSeconds(30), true);
        Assert.AreEqual(started!.Id, completed!.Id);
        Assert.IsNull(tracker.Publish(1, 3, "input", InfoOverlayActivityKind.Skill, "重试", "", Now.AddSeconds(33), false));
        Assert.IsNotNull(Present(tracker.Current, Now.AddSeconds(33)));
        Assert.IsNull(Present(tracker.Current, Now.AddSeconds(34)));
        Assert.IsNull(tracker.Publish(1, 3, "input", InfoOverlayActivityKind.Skill, "重试", "", Now.AddSeconds(40), true));
        Assert.IsNull(Present(tracker.Current, Now.AddSeconds(40)));
    }

    [TestMethod]
    public void BackgroundNameAndRecordUpdatesDoNotInterruptAnOngoingSequence()
    {
        var tracker = new InfoOverlayActivityTracker();
        tracker.ResetBattle(1);
        tracker.BeginTurn(2);
        var action = tracker.Publish(1, 2, "input", InfoOverlayActivityKind.Skill, "执行公共序列", "", Now, false);
        var recognized = tracker.RecognizeSpirit(1, null, "栗鼠", Now.AddSeconds(1));
        Assert.AreEqual(action!.Id, recognized!.Id);
        Assert.AreEqual("栗鼠", recognized.CreatureName);
        Assert.AreSame(recognized, tracker.Record("栗鼠", 8, Now, Now.AddSeconds(2)));
        var completed = tracker.Publish(1, 2, "input", InfoOverlayActivityKind.Skill, "已发送", "", Now.AddSeconds(3), true);
        Assert.AreEqual("栗鼠", completed!.CreatureName);
    }

    [TestMethod]
    public void SavedRecordGetsFullDisplayDurationAndSurvivesWorldFrameResets()
    {
        var tracker = new InfoOverlayActivityTracker();
        var publishedAt = Now.AddSeconds(20);
        var record = tracker.Record("栗鼠", 8, Now, publishedAt);
        tracker.ResetBattle(10);
        tracker.ResetBattle(11);
        Assert.AreSame(record, tracker.Current);
        Assert.IsNotNull(Present(record, publishedAt.AddSeconds(3)));
        Assert.IsNull(Present(record, publishedAt.AddSeconds(4)));
        StringAssert.Contains(record.Description, Now.ToLocalTime().ToString("HH:mm:ss"));
        tracker.Clear();
        Assert.IsNull(tracker.Current);
    }

    [TestMethod]
    public void ShinyProtectionIsPersistentWithoutStatisticsAndOverridesOrdinaryActivity()
    {
        var tracker = new InfoOverlayActivityTracker();
        var snapshot = new InfoOverlaySnapshot("战斗中", [], Now,
            Activity: tracker.Record("栗鼠", 8, Now, Now), IsShinyProtectionActive: true);
        var warning = InfoOverlayIslandPresentation.Resolve(snapshot, null, Now.AddHours(1));
        Assert.IsTrue(warning!.IsWarning);
        Assert.AreEqual("异色保护", warning.Category);
        Assert.AreEqual("异色保护", snapshot.MainStatusText);
        Assert.AreEqual("自动操作已暂停", warning.Title);
        Assert.IsNull(InfoOverlayIslandPresentation.Resolve(snapshot with { IsShinyProtectionActive = false }, null, Now.AddHours(1)));
    }

    [TestMethod]
    public void ShinyProtectionKeepsKnownCreatureWithoutPendingStatisticsRecord()
    {
        var snapshot = new InfoOverlaySnapshot("战斗中", [], Now, IsShinyProtectionActive: true,
            Scene: InfoOverlayScene.Battle, IsAutoBattleEnabled: true, BattleCreatureName: "栗鼠");
        var warning = InfoOverlayIslandPresentation.Resolve(snapshot, null, Now.AddHours(1))!;
        Assert.IsTrue(warning.IsWarning);
        Assert.AreEqual("栗鼠", warning.CreatureName);
        StringAssert.Contains(warning.Description, "精灵：栗鼠");
        Assert.AreEqual("自动操作已暂停", warning.Title);
        Assert.AreEqual("异色保护", snapshot.MainStatusText);
    }

    [TestMethod]
    public void AccountNoticeAndPendingShinyRemainVisibleUntilCleared()
    {
        var snapshot = new InfoOverlaySnapshot("战斗中", [], Now,
            PendingShinyCapture: new("栗鼠", "S4", Now));
        var notice = new InfoOverlayNotice("请确认统计账号", "账号尚未识别");
        Assert.AreEqual(notice.Title, InfoOverlayIslandPresentation.Resolve(snapshot, notice, Now.AddHours(1))!.Title);
        Assert.AreEqual("发现异色精灵", InfoOverlayIslandPresentation.Resolve(snapshot, null, Now.AddHours(1))!.Title);
        StringAssert.Contains(InfoOverlayIslandPresentation.Resolve(snapshot with { IsShinyProtectionActive = true }, null, Now)!.Description,
            "请在统计页面确认");
        Assert.IsNull(InfoOverlayIslandPresentation.Resolve(snapshot with { PendingShinyCapture = null }, null, Now.AddHours(1)));
    }

    private static InfoOverlayIslandPresentation? Present(InfoOverlayActivity? activity, DateTimeOffset at)
        => InfoOverlayIslandPresentation.Resolve(new("技能选择", [], Now, Activity: activity), null, at);

    [TestMethod]
    [DataRow(InfoOverlayScene.World, true, "大世界")]
    [DataRow(InfoOverlayScene.Battle, true, "自动战斗中")]
    [DataRow(InfoOverlayScene.Battle, false, "战斗中")]
    [DataRow(InfoOverlayScene.Suspended, true, "任务已挂起")]
    public void MainTitleReflectsSceneAndActualAutomationSetting(InfoOverlayScene scene, bool enabled, string expected)
    {
        var snapshot = new InfoOverlaySnapshot("战斗中 - 技能选择", [], Now, Scene: scene, IsAutoBattleEnabled: enabled);
        Assert.AreEqual(expected, snapshot.MainStatusText);
    }

    [TestMethod]
    public void ExpiredActivityFallsBackToTheCurrentBattleFlowWithoutChangingMainTitle()
    {
        var tracker = new InfoOverlayActivityTracker();
        tracker.ResetBattle(1); tracker.BeginTurn(1);
        var activity = tracker.Publish(1, 1, "input", InfoOverlayActivityKind.Skill, "使用技能 3", "", Now, true);
        var snapshot = new InfoOverlaySnapshot("战斗中 - 技能选择", [], Now, Activity: activity,
            Scene: InfoOverlayScene.Battle, IsAutoBattleEnabled: true);
        Assert.AreEqual("使用技能 3", InfoOverlayIslandPresentation.Resolve(snapshot, null, Now)!.Title);
        Assert.AreEqual("技能选择", InfoOverlayIslandPresentation.Resolve(snapshot, null, Now.AddSeconds(4))!.Title);
        Assert.AreEqual("自动战斗中", snapshot.MainStatusText);
        Assert.IsNull(InfoOverlayIslandPresentation.Resolve(snapshot with { Scene = InfoOverlayScene.World, StatusText = "大世界" }, null, Now.AddSeconds(4)));
    }

    [TestMethod]
    public void SkillDescriptionKeepsCreatureNameWithoutInternalExecutionDetails()
    {
        var tracker = new InfoOverlayActivityTracker();
        tracker.ResetBattle(1); tracker.BeginTurn(1);
        tracker.RecognizeSpirit(1, 1, "栗鼠", Now);
        var activity = tracker.Publish(1, 1, "input", InfoOverlayActivityKind.Skill, "使用技能 3", "", Now, false);
        var presentation = Present(activity, Now)!;
        Assert.AreEqual("栗鼠", presentation.CreatureName);
        Assert.AreEqual("使用技能 3", presentation.Title);
        Assert.AreEqual("精灵：栗鼠", presentation.Description);
        Assert.AreEqual("技能选择", presentation.Category);
    }

    [TestMethod]
    public void BattleFlowKeepsCreatureNameAfterActivityExpiresAndClearsOnNewBattle()
    {
        var tracker = new InfoOverlayActivityTracker();
        tracker.ResetBattle(1); tracker.BeginTurn(1);
        var recognized = tracker.RecognizeSpirit(1, 1, "栗鼠", Now);
        var snapshot = new InfoOverlaySnapshot("战斗中 - 技能选择", [], Now, Activity: recognized,
            Scene: InfoOverlayScene.Battle, BattleCreatureName: tracker.CreatureName);
        var flow = InfoOverlayIslandPresentation.Resolve(snapshot, null, Now.AddSeconds(5))!;
        Assert.AreEqual("技能选择", flow.Title);
        Assert.AreEqual("精灵：栗鼠", flow.Description);
        Assert.AreEqual("栗鼠", flow.CreatureName);
        tracker.ResetBattle(2);
        Assert.AreEqual("", tracker.CreatureName);
        Assert.IsNull(InfoOverlayIslandPresentation.Resolve(snapshot with { Scene = InfoOverlayScene.World, StatusText = "大世界" }, null, Now.AddSeconds(5)));
    }
}
