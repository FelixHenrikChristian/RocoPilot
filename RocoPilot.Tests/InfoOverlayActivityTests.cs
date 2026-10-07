using Microsoft.VisualStudio.TestTools.UnitTesting;
using RocoPilot.Models.Overlay;
using RocoPilot.Models.Runtime;

namespace RocoPilot.Tests;

[TestClass]
public sealed class InfoOverlayActivityTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 4, 12, 0, 0, TimeSpan.FromHours(8));

    [TestMethod]
    public void IndependentTaskDisplaysItsStepOperationAndRecognitionWhileRealtimeIsSuspended()
    {
        var progress = new IndependentTaskProgress("稀兽花种列表", "向下滚动，扫描下一页", "小皮球、星云旅者、琳琅");
        var snapshot = new InfoOverlaySnapshot("已挂起", [], Now, Scene: InfoOverlayScene.Suspended,
            IndependentTaskName: "扫描花种", IndependentTaskProgress: progress);

        Assert.AreEqual("扫描花种", snapshot.MainStatusText);
        var presentation = InfoOverlayIslandPresentation.Resolve(snapshot, null, Now.AddHours(1))!;
        Assert.AreEqual(progress.Stage, presentation.Category);
        Assert.AreEqual(progress.Operation, presentation.Title);
        Assert.AreEqual(progress.Recognition, presentation.Description);
        Assert.AreEqual("", presentation.CreatureName);
    }

    [TestMethod]
    public void IndependentTaskUsesItsRecognizedCreatureInsteadOfThePreviousRealtimeBattle()
    {
        var progress = new IndependentTaskProgress("战斗", "技能 1", "成功 0/6 次", "伊贝粉粉");
        var snapshot = new InfoOverlaySnapshot("已挂起", [], Now, Scene: InfoOverlayScene.Suspended,
            BattleCreatureName: "栗鼠", IndependentTaskName: "花种挑战", IndependentTaskProgress: progress);

        var presentation = InfoOverlayIslandPresentation.Resolve(snapshot, null, Now)!;
        Assert.AreEqual("伊贝粉粉", presentation.CreatureName);
        Assert.AreEqual("技能 1", presentation.Title);
        Assert.AreEqual("成功 0/6 次", presentation.Description);
    }

    [TestMethod]
    [DataRow(AutoBattleAction.Skill, "1", "1", "技能 1")]
    [DataRow(AutoBattleAction.Skill, "2", "2", "技能 2")]
    [DataRow(AutoBattleAction.Skill, "3", "3", "技能 3")]
    [DataRow(AutoBattleAction.Skill, "4", "4", "技能 4")]
    [DataRow(AutoBattleAction.Skill, "X", "X", "回能")]
    [DataRow(AutoBattleAction.EnergyRecovery, "X", null, "回能")]
    [DataRow(AutoBattleAction.Capture, "W", null, "捕捉")]
    [DataRow(AutoBattleAction.Skill, "自定义连招", null, "公共序列")]
    [DataRow(AutoBattleAction.Skill, "1", null, "公共序列")]
    public void BattleInputTitleDescribesTheActionWithoutRepeatingItsSequence(
        AutoBattleAction action, string displayKey, string? fallbackSequence, string expected)
    {
        var plan = new AutoBattlePlan(action, "1, Space", "释放技能 1（1, Space），已发送", displayKey, fallbackSequence);
        Assert.AreEqual(expected, InfoOverlayIslandPresentation.BattleInputTitle(plan));
    }

    [TestMethod]
    public void ClearingIndependentTaskRestoresTheRealtimeSceneAndDetails()
    {
        var snapshot = new InfoOverlaySnapshot("大世界", [], Now, Scene: InfoOverlayScene.World,
            IndependentTaskName: "稀兽花种挑战", IndependentTaskProgress: new("挑战准备", "点击开始"));
        Assert.IsNotNull(InfoOverlayIslandPresentation.Resolve(snapshot, null, Now));

        var resumed = snapshot with { IndependentTaskName = "", IndependentTaskProgress = null };
        Assert.AreEqual("大世界", resumed.MainStatusText);
        Assert.IsNull(InfoOverlayIslandPresentation.Resolve(resumed, null, Now));
    }

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
    public void CompletedTaskResultSurvivesResumeAndBattleResetsUntilItsFourSecondExpiry()
    {
        var tracker = new InfoOverlayActivityTracker();
        var completed = tracker.CompleteTask("扫描完成，共识别 12 个花种", "当前花种选项已更新", Now);
        tracker.ResetBattle(10);
        tracker.BeginTurn(1);
        tracker.ResetBattle(11);
        Assert.AreSame(completed, tracker.Current);
        Assert.IsFalse(completed.IsBattleBound);

        var resumed = new InfoOverlaySnapshot("大世界", [], Now, Activity: tracker.Current, Scene: InfoOverlayScene.World);
        var presentation = InfoOverlayIslandPresentation.Resolve(resumed, null, Now.AddSeconds(3))!;
        Assert.AreEqual("大世界", resumed.MainStatusText);
        Assert.AreEqual("任务完成", presentation.Category);
        Assert.AreEqual(completed.Title, presentation.Title);
        Assert.AreEqual(completed.Description, presentation.Description);
        Assert.IsNull(InfoOverlayIslandPresentation.Resolve(resumed, null, Now.AddSeconds(4)));
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
    public void AccountNoticeStillExpandsButPendingShinyDoesNotKeepDetailsOpen()
    {
        var snapshot = new InfoOverlaySnapshot("战斗中", [], Now,
            PendingShinyCapture: new("栗鼠", "S4", Now));
        var notice = new InfoOverlayNotice("请确认统计账号", "账号尚未识别");
        Assert.AreEqual(notice.Title, InfoOverlayIslandPresentation.Resolve(snapshot, notice, Now.AddHours(1))!.Title);
        Assert.IsNull(InfoOverlayIslandPresentation.Resolve(snapshot, null, Now.AddHours(1)));
        StringAssert.Contains(InfoOverlayIslandPresentation.Resolve(snapshot with { IsShinyProtectionActive = true }, null, Now)!.Description,
            "请在统计页面确认");
        Assert.IsNull(InfoOverlayIslandPresentation.Resolve(snapshot with { PendingShinyCapture = null }, null, Now.AddHours(1)));
    }

    [TestMethod]
    public void LeavingProtectedBattleKeepsPendingRecordWhileWorldDetailsCollapse()
    {
        var pending = new InfoOverlayPendingShinyCapture("栗鼠", "S4", Now, TotalCount: 2);
        var snapshot = new InfoOverlaySnapshot("战斗中", [], Now, PendingShinyCapture: pending,
            IsShinyProtectionActive: true, Scene: InfoOverlayScene.Battle, IsAutoBattleEnabled: true);
        Assert.IsTrue(InfoOverlayIslandPresentation.Resolve(snapshot, null, Now)!.IsWarning);

        var world = snapshot with { IsShinyProtectionActive = false, Scene = InfoOverlayScene.World, StatusText = "大世界" };
        Assert.AreEqual("大世界", world.MainStatusText);
        Assert.AreSame(pending, world.PendingShinyCapture);
        Assert.AreEqual(2, world.PendingShinyCapture!.TotalCount);
        Assert.IsNull(InfoOverlayIslandPresentation.Resolve(world, null, Now.AddHours(1)));
    }

    [TestMethod]
    public void UnconfirmedShinyDoesNotMaskNextCreatureSkillOrError()
    {
        var tracker = new InfoOverlayActivityTracker();
        tracker.ResetBattle(2); tracker.BeginTurn(1);
        var recognized = tracker.RecognizeSpirit(2, 1, "刺轮砣", Now);
        var snapshot = new InfoOverlaySnapshot("战斗中 - 技能选择", [], Now,
            PendingShinyCapture: new("栗鼠", "S4", Now.AddMinutes(-1)), Activity: recognized,
            Scene: InfoOverlayScene.Battle, IsAutoBattleEnabled: true, BattleCreatureName: "刺轮砣");
        Assert.AreEqual("刺轮砣", InfoOverlayIslandPresentation.Resolve(snapshot, null, Now)!.CreatureName);

        var skill = tracker.Publish(2, 1, "input", InfoOverlayActivityKind.Skill, "使用技能 3", "", Now, false);
        var running = InfoOverlayIslandPresentation.Resolve(snapshot with { Activity = skill }, null, Now.AddSeconds(30))!;
        Assert.AreEqual("使用技能 3", running.Title);
        Assert.AreEqual("精灵：刺轮砣", running.Description);
        Assert.IsFalse(running.IsWarning);
        var withoutPending = InfoOverlayIslandPresentation.Resolve(snapshot with { Activity = skill, PendingShinyCapture = null }, null, Now.AddSeconds(30));
        Assert.AreEqual(running, withoutPending);

        var failed = tracker.Publish(2, 1, "input:error", InfoOverlayActivityKind.Error, "操作未完成", "键盘设备不可用", Now, true);
        Assert.IsTrue(InfoOverlayIslandPresentation.Resolve(snapshot with { Activity = failed }, null, Now)!.IsError);
        Assert.IsTrue(InfoOverlayIslandPresentation.Resolve(snapshot with { Activity = skill, IsShinyProtectionActive = true }, null, Now)!.IsWarning);
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
