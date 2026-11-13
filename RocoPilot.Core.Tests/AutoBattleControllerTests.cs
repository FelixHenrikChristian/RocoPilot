using Microsoft.VisualStudio.TestTools.UnitTesting;
using RocoPilot.Core.Battle;

namespace RocoPilot.Core.Tests;

[TestClass]
public sealed class AutoBattleControllerTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 11, 0, 0, 0, TimeSpan.Zero);

    [TestMethod]
    [DataRow(0)]
    [DataRow(500)]
    [DataRow(2000)]
    public void FirstActionOnlyWaitsForConfiguredActionDelay(int actionDelay)
    {
        var settings = Settings();
        settings.SkillSelectionActionDelayMs = actionDelay;
        var battle = new AutoBattleController();
        battle.BeginSkillSelection(settings, Now);
        Assert.IsFalse(battle.CanAct(settings, Now.AddMilliseconds(actionDelay - 1)));
        Assert.IsTrue(battle.CanAct(settings, Now.AddMilliseconds(actionDelay)));
    }

    [TestMethod]
    public void AdvancesSkillExactlyOnceWhenSelectionEnds()
    {
        var settings = Settings();
        var battle = new AutoBattleController();
        var first = battle.BeginSkillSelection(settings, Now);
        battle.RecordAction(first.Id, AutoBattleAction.Skill, Now);
        battle.CompleteSkillSelection();
        battle.CompleteSkillSelection();
        var next = battle.BeginSkillSelection(settings, Now.AddSeconds(1));
        Assert.AreEqual("2", next.ReleaseStep.SkillKey);
        Assert.AreEqual(2, next.Number);
    }

    [TestMethod]
    public void SelectionInterruptedBeforeSendingKeepsTheCurrentReleaseStep()
    {
        var settings = Settings();
        var battle = new AutoBattleController();
        var turn = battle.BeginSkillSelection(settings, Now);
        battle.CompleteSkillSelection();
        Assert.IsFalse(battle.RecordAction(turn.Id, AutoBattleAction.Skill, Now));
        Assert.AreEqual("1", battle.BeginSkillSelection(settings, Now.AddSeconds(1)).ReleaseStep.SkillKey);
        Assert.AreEqual(0, battle.RoundIndex);
    }

    [TestMethod]
    [DataRow(750)]
    [DataRow(4000)]
    [DataRow(8000)]
    public void FailedSkillRecoversEnergyAndKeepsOriginalSkillForNextTurn(int retryDelay)
    {
        var settings = Settings();
        settings.SkillSelectionRetryDelayMs = retryDelay;
        var battle = new AutoBattleController();
        var turn = battle.BeginSkillSelection(settings, Now);
        battle.RecordAction(turn.Id, AutoBattleAction.Skill, Now);
        Assert.IsFalse(battle.ShouldRecoverAfterSkillFailure(settings, Now.AddMilliseconds(500)));
        Assert.IsFalse(battle.ShouldRecoverAfterSkillFailure(settings, Now.AddMilliseconds(retryDelay - 1)));
        Assert.IsTrue(battle.ShouldRecoverAfterSkillFailure(settings, Now.AddMilliseconds(retryDelay)));
        battle.RecordAction(turn.Id, AutoBattleAction.EnergyRecovery, Now.AddMilliseconds(retryDelay));
        Assert.IsFalse(battle.ShouldRecoverAfterSkillFailure(settings, Now.AddMilliseconds(retryDelay + 500)));
        Assert.IsFalse(battle.CanAct(settings, Now.AddMilliseconds(retryDelay * 2 - 1)));
        Assert.IsTrue(battle.CanAct(settings, Now.AddMilliseconds(retryDelay * 2)));
        battle.CompleteSkillSelection();
        Assert.AreEqual("1", battle.BeginSkillSelection(settings, Now.AddMilliseconds(retryDelay * 2 + 1)).ReleaseStep.SkillKey);
    }

    [TestMethod]
    [DataRow(AutoBattleAction.None)]
    [DataRow(AutoBattleAction.NoAction)]
    [DataRow(AutoBattleAction.Capture)]
    [DataRow(AutoBattleAction.EnergyRecovery)]
    public void NonSkillActionsDoNotAdvanceSequence(AutoBattleAction action)
    {
        var settings = Settings();
        var battle = new AutoBattleController();
        var turn = battle.BeginSkillSelection(settings, Now);
        battle.RecordAction(turn.Id, action, Now);
        battle.CompleteSkillSelection();
        Assert.AreEqual("1", battle.BeginSkillSelection(settings, Now).ReleaseStep.SkillKey);
    }

    [TestMethod]
    public void PetSwitchDoesNotConsumeAReleaseStep()
    {
        var settings = Settings();
        var battle = new AutoBattleController();
        var step = battle.BeginPetSwitching(settings);
        Assert.AreEqual(AutoBattlePhase.PetSwitching, battle.Phase);
        Assert.AreEqual("1", step.SkillKey);
        battle.ObservePetSwitching(false);
        var turn = battle.BeginSkillSelection(settings, Now);
        Assert.AreEqual("1", turn.ReleaseStep.SkillKey);
        Assert.AreEqual(2, turn.Number);
    }

    [TestMethod]
    public void SequenceWrapsAfterLastSkill()
    {
        var settings = Settings();
        settings.ReleaseSequence = [AutoBattleReleaseStep.CreateSkill("4")];
        var battle = new AutoBattleController();
        var first = battle.BeginSkillSelection(settings, Now);
        battle.RecordAction(first.Id, AutoBattleAction.Skill, Now);
        battle.CompleteSkillSelection();
        Assert.AreEqual("4", battle.BeginSkillSelection(settings, Now).ReleaseStep.SkillKey);
    }

    [TestMethod]
    public void ShinyProtectionRejectsLateInputCompletionAndClearsOnBattleEnd()
    {
        var settings = Settings();
        var battle = new AutoBattleController();
        var turn = battle.BeginSkillSelection(settings, Now);
        Assert.IsTrue(battle.ObserveShiny(battle.BattleId));
        Assert.IsFalse(battle.ObserveShiny(battle.BattleId)); // Repeated frames must not notify again.
        Assert.IsFalse(battle.RecordAction(turn.Id, AutoBattleAction.Skill, Now));
        Assert.IsFalse(battle.CanAct(settings, Now.AddSeconds(10)));
        battle.ResetBattle();
        Assert.IsFalse(battle.IsSuspendedForShiny);
        Assert.AreEqual("1", battle.BeginSkillSelection(settings, Now).ReleaseStep.SkillKey);
        Assert.IsTrue(battle.ObserveShiny(battle.BattleId)); // A new encounter can notify again.
    }

    [TestMethod]
    public void OldBattleAndTurnResultsCannotChangeNewBattle()
    {
        var settings = Settings();
        var battle = new AutoBattleController();
        var oldBattle = battle.BattleId;
        var oldTurn = battle.BeginSkillSelection(settings, Now);
        battle.ResetBattle();
        var next = battle.BeginSkillSelection(settings, Now);
        Assert.AreNotEqual(oldTurn.Id, next.Id);
        Assert.IsFalse(battle.RecordAction(oldTurn.Id, AutoBattleAction.Skill, Now));
        Assert.IsFalse(battle.ObserveShiny(oldBattle));
        battle.ObserveBloodline(oldBattle, EncounterBloodlineKind.Normal);
        battle.ObserveEncounterRelieved(settings);
        Assert.AreEqual(AutoBattleAction.None, battle.PlanSkillSelection(settings, true, Now).Action);
    }

    [TestMethod]
    public void BloodlineWaitTimesOutAtFourSecondsAndLocksTheDecision()
    {
        var settings = Settings();
        settings.BloodlineCaptureFilter.CaptureUnrecognized = false;
        var battle = new AutoBattleController();
        battle.BeginSkillSelection(settings, Now);
        battle.ObserveEncounterRelieved(settings);
        Assert.AreEqual(AutoBattleAction.None, battle.PlanSkillSelection(settings, true, Now).Action);
        Assert.AreEqual(AutoBattleAction.None, battle.PlanSkillSelection(settings, true, Now.AddMilliseconds(3999)).Action);
        Assert.AreEqual(AutoBattleAction.Skill, battle.PlanSkillSelection(settings, true, Now.AddSeconds(4)).Action);
        battle.ObserveBloodline(battle.BattleId, EncounterBloodlineKind.QiYi);
        Assert.AreEqual(AutoBattleAction.Skill, battle.PlanSkillSelection(settings, true, Now.AddSeconds(5)).Action);
    }

    [TestMethod]
    [DataRow(EncounterBloodlineKind.QiYi, AutoBattleAction.Capture)]
    [DataRow(EncounterBloodlineKind.HunXue, AutoBattleAction.Skill)]
    [DataRow(EncounterBloodlineKind.WuRan, AutoBattleAction.Capture)]
    [DataRow(EncounterBloodlineKind.Normal, AutoBattleAction.Skill)]
    public void BloodlineFilterSelectsCaptureOrOriginalSkill(EncounterBloodlineKind kind, AutoBattleAction expected)
    {
        var settings = Settings();
        var battle = new AutoBattleController();
        battle.BeginSkillSelection(settings, Now);
        battle.ObserveEncounterRelieved(settings);
        battle.ObserveBloodline(battle.BattleId, kind);
        Assert.AreEqual(expected, battle.PlanSkillSelection(settings, true, Now).Action);
    }

    [TestMethod]
    public void SeasonWithoutBloodlineRecognitionUsesUnknownFilterImmediately()
    {
        var settings = Settings();
        var battle = new AutoBattleController();
        battle.BeginSkillSelection(settings, Now);
        battle.ObserveEncounterRelieved(settings);
        Assert.AreEqual(AutoBattleAction.Capture, battle.PlanSkillSelection(settings, false, Now).Action);
    }

    [TestMethod]
    public void DisabledBattleCannotActOrRecover()
    {
        var settings = Settings();
        var battle = new AutoBattleController();
        var turn = battle.BeginSkillSelection(settings, Now);
        battle.RecordAction(turn.Id, AutoBattleAction.Skill, Now);
        settings.IsEnabled = false;
        Assert.IsFalse(battle.CanAct(settings, Now.AddSeconds(10)));
        Assert.IsFalse(battle.ShouldRecoverAfterSkillFailure(settings, Now.AddSeconds(10)));
    }

    [TestMethod]
    public void SpiritCaptureListWaitsForEnemyNameThenCapturesOnlyListedSpirits()
    {
        var settings = Settings();
        settings.EncounterCapturePolicy.IsEnabled = true;
        settings.EncounterCapturePolicy.OnlyListedSpirits = true;
        settings.EncounterCapturePolicy.Rules.Add(new EncounterCaptureRuleSettings { SpiritName = "吸泥鸥" });
        var battle = new AutoBattleController();
        battle.BeginSkillSelection(settings, Now);
        battle.ObserveEncounterRelieved(settings);
        // 敌名未识别：先等待，超时后默认捕捉。
        Assert.AreEqual(AutoBattleAction.None, battle.PlanSkillSelection(settings, true, Now).Action);
        var unrecognized = battle.PlanSkillSelection(settings, true, Now.AddSeconds(4));
        Assert.AreEqual(AutoBattleAction.Capture, unrecognized.Action);
        Assert.IsTrue(unrecognized.Description.Contains("未识别", StringComparison.Ordinal));
        // 名单内精灵：捕捉。
        battle.ObserveEnemyName(battle.BattleId, "吸泥鸥");
        var capture = battle.PlanSkillSelection(settings, true, Now.AddSeconds(5));
        Assert.AreEqual(AutoBattleAction.Capture, capture.Action);
        Assert.IsTrue(capture.Description.Contains("吸泥鸥", StringComparison.Ordinal));
        // 名单外精灵：不捕捉。
        var other = new AutoBattleController();
        other.BeginSkillSelection(settings, Now);
        other.ObserveEncounterRelieved(settings);
        other.ObserveEnemyName(other.BattleId, "恶魔狼");
        var defeat = other.PlanSkillSelection(settings, true, Now.AddSeconds(1));
        Assert.AreEqual(AutoBattleAction.Skill, defeat.Action);
        Assert.IsTrue(defeat.Description.Contains("不在捕捉名单", StringComparison.Ordinal));
    }

    [TestMethod]
    public void DefeatRuleBeatsWhitelistModeAndBlocksCapture()
    {
        // 黑名单（击败）在白名单模式下同样生效：即使精灵在白名单中也不重复配置，
        // 黑名单命中即击败；白名单外的仍不捕捉。
        var settings = Settings();
        settings.EncounterCapturePolicy.IsEnabled = true;
        settings.EncounterCapturePolicy.OnlyListedSpirits = true;
        settings.EncounterCapturePolicy.Rules.Add(new EncounterCaptureRuleSettings { SpiritName = "吸泥鸥" });
        settings.EncounterCapturePolicy.Rules.Add(new EncounterCaptureRuleSettings
        {
            SpiritName = "花影羚羊",
            Action = EncounterCaptureRuleAction.Defeat
        });
        var battle = new AutoBattleController();
        battle.BeginSkillSelection(settings, Now);
        battle.ObserveEncounterRelieved(settings);
        battle.ObserveEnemyName(battle.BattleId, "花影羚羊");
        var defeat = battle.PlanSkillSelection(settings, true, Now);
        Assert.AreEqual(AutoBattleAction.Skill, defeat.Action);
        Assert.IsTrue(defeat.Description.Contains("直接击败", StringComparison.Ordinal));
    }

    [TestMethod]
    public void SpiritCaptureListWithoutWhitelistFallsBackToBloodlineFilter()
    {
        var settings = Settings();
        settings.EncounterCapturePolicy.IsEnabled = true;
        settings.EncounterCapturePolicy.OnlyListedSpirits = false;
        settings.EncounterCapturePolicy.Rules.Add(new EncounterCaptureRuleSettings { SpiritName = "吸泥鸥" });
        var battle = new AutoBattleController();
        battle.BeginSkillSelection(settings, Now);
        battle.ObserveEncounterRelieved(settings);
        battle.ObserveEnemyName(battle.BattleId, "恶魔狼");
        // 名单外且未开白名单：按血脉筛选（赛季无血脉识别时立即捕捉）。
        Assert.AreEqual(AutoBattleAction.Capture, battle.PlanSkillSelection(settings, false, Now).Action);
    }

    [TestMethod]
    public void DefeatRuleReleasesSkillInsteadOfCapturing()
    {
        var settings = Settings();
        settings.EncounterCapturePolicy.IsEnabled = true;
        settings.EncounterCapturePolicy.OnlyListedSpirits = false;
        settings.EncounterCapturePolicy.Rules.Add(new EncounterCaptureRuleSettings
        {
            SpiritName = "恶魔狼",
            Action = EncounterCaptureRuleAction.Defeat
        });
        var battle = new AutoBattleController();
        battle.BeginSkillSelection(settings, Now);
        battle.ObserveEncounterRelieved(settings);
        battle.ObserveEnemyName(battle.BattleId, "恶魔狼");
        var plan = battle.PlanSkillSelection(settings, true, Now);
        Assert.AreEqual(AutoBattleAction.Skill, plan.Action);
        Assert.IsTrue(plan.Description.Contains("直接击败", StringComparison.Ordinal));
    }

    [TestMethod]
    public void EnemyNameOnlyAppliesToCurrentBattleAfterReliefReset()
    {
        var settings = Settings();
        settings.EncounterCapturePolicy.IsEnabled = true;
        settings.EncounterCapturePolicy.OnlyListedSpirits = true;
        settings.EncounterCapturePolicy.Rules.Add(new EncounterCaptureRuleSettings { SpiritName = "吸泥鸥" });
        var battle = new AutoBattleController();
        battle.BeginSkillSelection(settings, Now);
        battle.ObserveEncounterRelieved(settings);
        battle.ObserveEnemyName(battle.BattleId, "吸泥鸥");
        Assert.AreEqual(AutoBattleAction.Capture, battle.PlanSkillSelection(settings, true, Now).Action);
        battle.ResetBattle();
        battle.BeginSkillSelection(settings, Now);
        battle.ObserveEncounterRelieved(settings);
        // 新战斗未识别敌名：回到等待。
        Assert.AreEqual(AutoBattleAction.None, battle.PlanSkillSelection(settings, true, Now).Action);
    }

    private static AutoBattleSettings Settings() => AutoBattleSettingsRules.Normalize(new AutoBattleSettings
    {
        IsEnabled = true,
        EncounterRelievedAction = AutoBattleEncounterRelievedAction.Capture
    });
}
