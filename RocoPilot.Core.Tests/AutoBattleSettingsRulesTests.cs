using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json;
using RocoPilot.Core.Battle;

namespace RocoPilot.Core.Tests;

[TestClass]
public sealed class AutoBattleSettingsRulesTests
{
    [TestMethod]
    public void MigratesLegacyRoundOrderWithoutOverwritingCustomSequence()
    {
        var legacy = JsonConvert.DeserializeObject<AutoBattleSettings>("{\"RoundOrder\":\"4; x 2\"}")!;
        var normalized = AutoBattleSettingsRules.Normalize(legacy);
        CollectionAssert.AreEqual(new[] { "4", "X", "2" }, normalized.ReleaseSequence.Select(step => step.SkillKey).ToArray());
        legacy.ReleaseSequence = [AutoBattleReleaseStep.CreateCustom("连招", "1, X, 2")];
        Assert.AreEqual("1, X, 2", AutoBattleSettingsRules.Normalize(legacy).ReleaseSequence.Single().Sequence);
    }

    [TestMethod]
    public void NormalizationIsIdempotentAndDoesNotMutateCaller()
    {
        var source = new AutoBattleSettings { RoundOrder = "4", KeyboardHoldDurationMs = 1 };
        var first = AutoBattleSettingsRules.Normalize(source);
        var second = AutoBattleSettingsRules.Normalize(first);
        Assert.AreEqual(JsonConvert.SerializeObject(first), JsonConvert.SerializeObject(second));
        Assert.AreEqual(1, source.KeyboardHoldDurationMs);
        Assert.AreEqual("1", source.ReleaseSequence[0].SkillKey);
    }

    [TestMethod]
    public void InvalidOrNullReleaseStepsFallBackToLegacyOrder()
    {
        var source = JsonConvert.DeserializeObject<AutoBattleSettings>(
            "{\"RoundOrder\":\"3\",\"ReleaseSequence\":[null,{\"SkillKey\":\"bad\"}],\"TurnSequencePresets\":[null]}")!;
        var normalized = AutoBattleSettingsRules.Normalize(source);
        Assert.AreEqual("3", normalized.ReleaseSequence.Single().SkillKey);
        Assert.AreEqual(0, normalized.TurnSequencePresets.Count);
    }

    [TestMethod]
    public void ExpandsLegacyTurnTemplateAndKeepsCustomSequenceLiteral()
    {
        var settings = new AutoBattleSettings { TurnSequence = "Space, {SKILL}, X" };
        Assert.AreEqual("Space, 4, X", AutoBattleSettingsRules.BuildReleaseSequence(settings, AutoBattleReleaseStep.CreateSkill("4")));
        Assert.AreEqual("1, 2", AutoBattleSettingsRules.BuildReleaseSequence(settings, AutoBattleReleaseStep.CreateCustom("组合", "1, 2")));
    }
}
