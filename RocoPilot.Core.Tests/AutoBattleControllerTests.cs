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

    private static AutoBattleSettings Settings() => AutoBattleSettingsRules.Normalize(new AutoBattleSettings
    {
        IsEnabled = true,
        EncounterRelievedAction = AutoBattleEncounterRelievedAction.Capture
    });
}
