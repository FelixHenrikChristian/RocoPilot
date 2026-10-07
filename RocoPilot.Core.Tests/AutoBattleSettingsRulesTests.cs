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
    public void ResolvesLinkedPresetAfterSerializationAndUsesLatestSequenceAtRuntime()
    {
        var preset = new AutoBattleTurnSequencePreset { Name = "连招", Sequence = "1" };
        var source = new AutoBattleSettings
        {
            TurnSequencePresets = [preset],
            ReleaseSequence = [AutoBattleReleaseStep.CreateCustom(preset.Name, preset.Sequence, preset.Id)]
        };
        var reloaded = JsonConvert.DeserializeObject<AutoBattleSettings>(JsonConvert.SerializeObject(source))!;
        reloaded.TurnSequencePresets[0].Name = "新连招";
        reloaded.TurnSequencePresets[0].Sequence = "4, X";
        Assert.AreEqual("4, X", AutoBattleSettingsRules.BuildReleaseSequence(reloaded, reloaded.ReleaseSequence[0]));
        var normalized = AutoBattleSettingsRules.Normalize(reloaded);
        Assert.AreEqual("新连招", normalized.ReleaseSequence[0].Name);
        Assert.AreEqual("4, X", normalized.ReleaseSequence[0].Sequence);
        Assert.AreEqual(preset.Id, normalized.ReleaseSequence[0].PresetId);
        Assert.AreEqual(JsonConvert.SerializeObject(normalized), JsonConvert.SerializeObject(AutoBattleSettingsRules.Normalize(normalized)));
        Assert.AreEqual("连招", source.ReleaseSequence[0].Name);
    }

    [TestMethod]
    public void MigratesOnlyUnambiguousLegacyCopies()
    {
        var source = new AutoBattleSettings
        {
            TurnSequencePresets =
            [
                new() { Name = "唯一", Sequence = "1" },
                new() { Name = "重复", Sequence = "2" },
                new() { Name = "重复", Sequence = "2" }
            ],
            ReleaseSequence =
            [
                AutoBattleReleaseStep.CreateCustom("唯一", "1"),
                AutoBattleReleaseStep.CreateCustom("重复", "2"),
                AutoBattleReleaseStep.CreateCustom("独立", "3")
            ]
        };
        var normalized = AutoBattleSettingsRules.Normalize(source);
        Assert.AreEqual(normalized.TurnSequencePresets[0].Id, normalized.ReleaseSequence[0].PresetId);
        Assert.AreEqual(string.Empty, normalized.ReleaseSequence[1].PresetId);
        Assert.AreEqual(string.Empty, normalized.ReleaseSequence[2].PresetId);
    }

    [TestMethod]
    public void MissingPresetFallsBackToSavedSequenceWithoutMatchingAnotherPreset()
    {
        var source = new AutoBattleSettings
        {
            TurnSequencePresets = [new() { Name = "连招", Sequence = "1" }],
            ReleaseSequence = [AutoBattleReleaseStep.CreateCustom("连招", "1", "removed")]
        };
        Assert.AreEqual("1", AutoBattleSettingsRules.BuildReleaseSequence(source, source.ReleaseSequence[0]));
        var normalized = AutoBattleSettingsRules.Normalize(source);
        Assert.AreEqual("1", normalized.ReleaseSequence[0].Sequence);
        Assert.AreEqual("removed", normalized.ReleaseSequence[0].PresetId);
        Assert.AreEqual(JsonConvert.SerializeObject(normalized), JsonConvert.SerializeObject(AutoBattleSettingsRules.Normalize(normalized)));
    }

    [TestMethod]
    public void RepairsMissingAndDuplicatePresetIds()
    {
        var source = new AutoBattleSettings
        {
            TurnSequencePresets =
            [
                new() { Id = "", Name = "一", Sequence = "1" },
                new() { Id = "same", Name = "二", Sequence = "2" },
                new() { Id = "same", Name = "三", Sequence = "3" }
            ]
        };
        var normalized = AutoBattleSettingsRules.Normalize(source);
        Assert.AreEqual(3, normalized.TurnSequencePresets.Select(preset => preset.Id).Distinct().Count());
        Assert.IsTrue(normalized.TurnSequencePresets.All(preset => !string.IsNullOrWhiteSpace(preset.Id)));
        Assert.AreEqual(JsonConvert.SerializeObject(normalized), JsonConvert.SerializeObject(AutoBattleSettingsRules.Normalize(normalized)));
    }

    [TestMethod]
    public void ExpandsLegacyTurnTemplateAndKeepsCustomSequenceLiteral()
    {
        var settings = new AutoBattleSettings { TurnSequence = "Space, {SKILL}, X" };
        Assert.AreEqual("Space, 4, X", AutoBattleSettingsRules.BuildReleaseSequence(settings, AutoBattleReleaseStep.CreateSkill("4")));
        Assert.AreEqual("1, 2", AutoBattleSettingsRules.BuildReleaseSequence(settings, AutoBattleReleaseStep.CreateCustom("组合", "1, 2")));
    }

    [TestMethod]
    public void FlowerSeedSequenceDefaultsIndependentlyOfLegacyNormalOrder()
    {
        var legacy = JsonConvert.DeserializeObject<AutoBattleSettings>("{\"RoundOrder\":\"4\"}")!;
        var normalized = AutoBattleSettingsRules.Normalize(legacy);
        Assert.AreEqual("4", normalized.ReleaseSequence.Single().SkillKey);
        CollectionAssert.AreEqual(new[] { "1", "2", "3", "4", "X" },
            normalized.FlowerSeedReleaseSequence.Select(step => step.SkillKey).ToArray());

        normalized.FlowerSeedReleaseSequence[0].SkillKey = "3";
        Assert.AreEqual("1", legacy.FlowerSeedReleaseSequence[0].SkillKey);
    }

    [TestMethod]
    public void FlowerSeedSequenceSurvivesSerializationAndResolvesSharedPreset()
    {
        var preset = new AutoBattleTurnSequencePreset { Name = "连招", Sequence = "1, Space" };
        var source = new AutoBattleSettings
        {
            TurnSequencePresets = [preset],
            ReleaseSequence = [AutoBattleReleaseStep.CreateSkill("4")],
            FlowerSeedReleaseSequence = [AutoBattleReleaseStep.CreateCustom(preset.Name, preset.Sequence, preset.Id)]
        };
        var reloaded = JsonConvert.DeserializeObject<AutoBattleSettings>(JsonConvert.SerializeObject(source))!;
        Assert.AreEqual(1, reloaded.FlowerSeedReleaseSequence.Count);
        reloaded.TurnSequencePresets[0].Name = "新连招";
        reloaded.TurnSequencePresets[0].Sequence = "3, X";
        var normalized = AutoBattleSettingsRules.Normalize(reloaded);
        Assert.AreEqual("4", normalized.ReleaseSequence.Single().SkillKey);
        Assert.AreEqual("新连招", normalized.FlowerSeedReleaseSequence.Single().Name);
        Assert.AreEqual("3, X", AutoBattleSettingsRules.BuildReleaseSequence(normalized, normalized.FlowerSeedReleaseSequence[0]));
        Assert.AreEqual("1, Space", source.FlowerSeedReleaseSequence[0].Sequence);
        Assert.AreEqual(JsonConvert.SerializeObject(normalized), JsonConvert.SerializeObject(AutoBattleSettingsRules.Normalize(normalized)));
    }
}
