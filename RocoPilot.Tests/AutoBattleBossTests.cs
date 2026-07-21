using Microsoft.VisualStudio.TestTools.UnitTesting;

using RocoPilot.Models.Runtime;
using RocoPilot.Services;

namespace RocoPilot.Tests;

[TestClass]
public sealed class AutoBattleBossTests
{
    [TestMethod]
    public void RecognizesMeaningfulBossNameText()
    {
        Assert.IsTrue(BossBattleRecognition.HasRecognizedName("岩甲首领"));
        Assert.IsTrue(BossBattleRecognition.HasRecognizedName("  岩甲 首领  \r\n"));
        Assert.IsFalse(BossBattleRecognition.HasRecognizedName(null));
        Assert.IsFalse(BossBattleRecognition.HasRecognizedName("|"));
        Assert.IsFalse(BossBattleRecognition.HasRecognizedName("领"));
    }

    [TestMethod]
    public void MatchesNoisyBossComboPrompt()
    {
        Assert.IsTrue(BossBattleRecognition.IsComboPrompt(
            "连续释放技能攻击首领",
            out var exactSimilarity));
        Assert.AreEqual(1d, exactSimilarity);

        Assert.IsTrue(BossBattleRecognition.IsComboPrompt(
            "连续释放技熊攻去首领",
            out var noisySimilarity));
        Assert.IsGreaterThanOrEqualTo(
            BossBattleRecognition.ComboPromptMatchThreshold,
            noisySimilarity);
    }

    [TestMethod]
    public void RejectsShortOrUnrelatedBossComboText()
    {
        Assert.IsFalse(BossBattleRecognition.IsComboPrompt("首领", out _));
        Assert.IsFalse(BossBattleRecognition.IsComboPrompt("技能冷却中", out _));
    }

    [TestMethod]
    public void MatchesNoisyEnergyInsufficientTip()
    {
        Assert.IsTrue(BossBattleRecognition.IsEnergyInsufficient("能量不足", out _));
        Assert.IsTrue(BossBattleRecognition.IsEnergyInsufficient("量不足", out _));
        Assert.IsTrue(BossBattleRecognition.IsEnergyInsufficient("能量不促", out _));
        Assert.IsFalse(BossBattleRecognition.IsEnergyInsufficient("能量", out _));
        Assert.IsFalse(BossBattleRecognition.IsEnergyInsufficient("技能释放成功", out _));
    }

    [TestMethod]
    public void NormalizesExactlySixBossComboSkills()
    {
        Assert.IsTrue(BossBattleComboSequence.TryNormalize(
            "1 2;3\n1,4\t2",
            out var normalizedSequence));
        Assert.AreEqual("1, 2, 3, 1, 4, 2", normalizedSequence);
        Assert.AreEqual(
            "1, 2, 3, 1, 4, 2, Space",
            BossBattleComboSequence.BuildConfirmedSequence(normalizedSequence));
    }

    [TestMethod]
    public void AllowsEnergyRecoveryInBossComboSequence()
    {
        Assert.IsTrue(BossBattleComboSequence.TryNormalize(
            "1, X, 2, X, 3, X",
            out var normalizedSequence));
        Assert.AreEqual(AutoBattleSettings.DefaultBossComboSequence, normalizedSequence);
    }

    [TestMethod]
    [DataRow("1, 2, 3, 4, 1")]
    [DataRow("1, 2, 3, 4, 1, 2, 3")]
    [DataRow("1, 2, 3, 5, 1, 2")]
    [DataRow("")]
    public void RejectsInvalidBossComboSequence(string sequence)
    {
        Assert.IsFalse(BossBattleComboSequence.TryNormalize(sequence, out _));
    }

    [TestMethod]
    public void MigratesMissingBossReleaseSequenceFromNormalBattle()
    {
        var settings = AutoBattleSettings.CreateDefault();
        settings.ReleaseSequence =
        [
            AutoBattleReleaseStep.CreateSkill("4"),
            AutoBattleReleaseStep.CreateCustom("收尾", "2, Space")
        ];
        settings.BossReleaseSequence = [];

        var bossReleaseSequence = RuntimeTaskService.NormalizeAutoBattleBossReleaseSequence(settings);

        Assert.AreEqual(2, bossReleaseSequence.Count);
        Assert.AreEqual("4", bossReleaseSequence[0].SkillKey);
        Assert.IsTrue(bossReleaseSequence[1].IsCustom);
        Assert.AreEqual("收尾", bossReleaseSequence[1].Name);
    }

    [TestMethod]
    public void KeepsBossReleaseSequenceIndependentFromNormalBattle()
    {
        var settings = AutoBattleSettings.CreateDefault();
        settings.ReleaseSequence = [AutoBattleReleaseStep.CreateSkill("1")];
        settings.BossReleaseSequence =
        [
            AutoBattleReleaseStep.CreateSkill("3"),
            AutoBattleReleaseStep.CreateSkill("X")
        ];

        var bossReleaseSequence = RuntimeTaskService.NormalizeAutoBattleBossReleaseSequence(settings);

        CollectionAssert.AreEqual(
            new[] { "3", "X" },
            bossReleaseSequence.Select(step => step.SkillKey).ToArray());
    }
}
