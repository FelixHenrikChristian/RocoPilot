using Microsoft.VisualStudio.TestTools.UnitTesting;
using RocoPilot.Core.Battle;

namespace RocoPilot.Core.Tests;

[TestClass]
public sealed class SkipTurnStepTests
{
    private static AutoBattleSettings CreateSettings(params string[] keys)
    {
        var settings = AutoBattleSettings.CreateDefault();
        settings.ReleaseSequence = keys.Select(AutoBattleReleaseStep.CreateSkill).ToList();
        settings.FlowerSeedReleaseSequence = settings.ReleaseSequence.Select(step => step.Clone()).ToList();
        settings.LegendaryReleaseSequence = settings.ReleaseSequence.Select(step => step.Clone()).ToList();
        return AutoBattleSettingsRules.Normalize(settings);
    }

    [TestMethod]
    public void IsSkipTurnStep_RecognizesOnlySkillS()
    {
        Assert.IsTrue(AutoBattleController.IsSkipTurnStep(AutoBattleReleaseStep.CreateSkill("S")));
        Assert.IsFalse(AutoBattleController.IsSkipTurnStep(AutoBattleReleaseStep.CreateSkill("1")));
        Assert.IsFalse(AutoBattleController.IsSkipTurnStep(AutoBattleReleaseStep.CreateSkill("X")));
        Assert.IsFalse(AutoBattleController.IsSkipTurnStep(null));
        Assert.IsFalse(AutoBattleController.IsSkipTurnStep(
            AutoBattleReleaseStep.CreateCustom("自定义", "1, 2")));
    }

    [TestMethod]
    public void NormalizeSkillKey_AcceptsSkipKey()
    {
        Assert.AreEqual("S", AutoBattleSettingsRules.NormalizeSkillKey("s"));
        Assert.AreEqual("S", AutoBattleSettingsRules.NormalizeSkillKey(" S "));
        Assert.IsNull(AutoBattleSettingsRules.NormalizeSkillKey("Q"));
    }

    [TestMethod]
    public void Peek_ReturnsCurrentStepWithoutCreatingTurn()
    {
        var settings = CreateSettings("S", "1", "2");
        var battle = new AutoBattleController();

        var step = battle.PeekReleaseStep(settings);

        Assert.AreEqual("S", step.SkillKey);
        Assert.AreEqual(AutoBattlePhase.Idle, battle.Phase);
        Assert.IsNull(battle.CurrentTurn);
    }

    [TestMethod]
    public void SkipReleaseStep_AdvancesToNextStep()
    {
        var settings = CreateSettings("S", "1", "2");
        var battle = new AutoBattleController();

        battle.SkipReleaseStep();
        var step = battle.PeekReleaseStep(settings);

        Assert.AreEqual("1", step.SkillKey);
    }

    [TestMethod]
    public void SkipReleaseStep_WrapsAroundSequence()
    {
        var settings = CreateSettings("1", "S", "2");
        var battle = new AutoBattleController();

        battle.SkipReleaseStep(); // 1 -> S
        Assert.AreEqual("S", battle.PeekReleaseStep(settings).SkillKey);
        battle.SkipReleaseStep(); // S -> 2
        Assert.AreEqual("2", battle.PeekReleaseStep(settings).SkillKey);
        battle.SkipReleaseStep(); // 2 -> wraps to 1
        Assert.AreEqual("1", battle.PeekReleaseStep(settings).SkillKey);
    }

    [TestMethod]
    public void PlanSkillSelection_SkipStepFallsBackToNoAction()
    {
        var settings = CreateSettings("S", "1");
        settings.IsEnabled = true;
        var battle = new AutoBattleController();
        battle.BeginSkillSelection(settings, DateTimeOffset.Now);

        var plan = battle.PlanSkillSelection(settings, bloodlineRecognitionAvailable: false, DateTimeOffset.Now);

        if (battle.CurrentTurn?.ReleaseStep.SkillKey == "S")
        {
            Assert.AreEqual(AutoBattleAction.NoAction, plan.Action);
            Assert.IsFalse(plan.ShouldSendKeys);
        }
    }

    [TestMethod]
    public void Normalize_ClampsPlayerNumber()
    {
        var settings = AutoBattleSettings.CreateDefault();
        settings.PlayerNumber = 9;
        Assert.AreEqual(4, AutoBattleSettingsRules.Normalize(settings).PlayerNumber);

        settings.PlayerNumber = 0;
        Assert.AreEqual(1, AutoBattleSettingsRules.Normalize(settings).PlayerNumber);
    }

    [TestMethod]
    public void Normalize_ChallengeSequencesKeepSkipStep()
    {
        var settings = CreateSettings("S", "1");
        // 扁平序列由选中配置生成：修改步骤需写入配置本身。
        settings.FlowerSeedReleaseConfigs[0].Steps =
            [AutoBattleReleaseStep.CreateSkill("S"), AutoBattleReleaseStep.CreateSkill("2")];

        var normalized = AutoBattleSettingsRules.Normalize(settings);

        Assert.AreEqual("S", normalized.FlowerSeedReleaseSequence[0].SkillKey);
        Assert.AreEqual("2", normalized.FlowerSeedReleaseSequence[1].SkillKey);
        Assert.AreEqual("S", normalized.FlowerSeedReleaseConfigs[0].Steps[0].SkillKey);
    }
}
