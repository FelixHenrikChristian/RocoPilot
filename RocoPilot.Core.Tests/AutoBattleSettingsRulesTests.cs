using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json;
using RocoPilot.Core.Battle;

namespace RocoPilot.Core.Tests;

[TestClass]
public sealed class AutoBattleSettingsRulesTests
{
    [TestMethod]
    public void LegendarySequenceResolvesSharedPresetsWithoutChangingFlowerSequence()
    {
        var preset = new AutoBattleTurnSequencePreset { Name = "公共", Sequence = "4, X" };
        var settings = new AutoBattleSettings
        {
            TurnSequencePresets = [preset],
            FlowerSeedReleaseSequence = [AutoBattleReleaseStep.CreateSkill("2")],
            LegendaryReleaseSequence = [AutoBattleReleaseStep.CreateCustom("旧名称", "1", preset.Id)]
        };
        var normalized = AutoBattleSettingsRules.Normalize(settings);
        Assert.AreEqual("4, X", normalized.LegendaryReleaseSequence.Single().Sequence);
        Assert.AreEqual("2", normalized.FlowerSeedReleaseSequence.Single().SkillKey);
        Assert.AreEqual("1", settings.LegendaryReleaseSequence.Single().Sequence);
        normalized.LegendaryReleaseSequence.Clear();
        Assert.AreEqual(1, settings.LegendaryReleaseSequence.Count);
        // 扁平序列由选中配置生成：清空后重新规范化会从配置恢复，而不是回退默认序列。
        var restored = AutoBattleSettingsRules.Normalize(normalized);
        Assert.AreEqual(1, restored.LegendaryReleaseSequence.Count);
        Assert.AreEqual("4, X", restored.LegendaryReleaseSequence.Single().Sequence);

        var oldSettings = JsonConvert.DeserializeObject<AutoBattleSettings>("{}")!;
        Assert.AreEqual(5, AutoBattleSettingsRules.Normalize(oldSettings).LegendaryReleaseSequence.Count);
    }

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

    [TestMethod]
    public void LegacyFlatSequencesMigrateToPerTypeDefaultConfigs()
    {
        var legacy = JsonConvert.DeserializeObject<AutoBattleSettings>("{\"RoundOrder\":\"4, 2\"}")!;
        var normalized = AutoBattleSettingsRules.Normalize(legacy);

        // 没有任何配置时：每种战斗类型各生成一个「默认配置」，继承各自旧扁平序列。
        foreach (var (configs, typeName) in new[]
                 {
                     (normalized.NormalReleaseConfigs, "普通战斗"),
                     (normalized.FlowerSeedReleaseConfigs, "花种战斗"),
                     (normalized.LegendaryReleaseConfigs, "传说精灵战斗")
                 })
        {
            Assert.AreEqual(1, configs.Count, typeName);
            Assert.AreEqual("默认配置", configs[0].Name, typeName);
        }

        CollectionAssert.AreEqual(
            new[] { "4", "2" },
            normalized.NormalReleaseConfigs[0].Steps.Select(step => step.SkillKey).ToArray());
        Assert.AreEqual(normalized.NormalReleaseConfigs[0].Id, normalized.SelectedNormalReleaseConfigId);
        CollectionAssert.AreEqual(
            new[] { "4", "2" },
            normalized.ReleaseSequence.Select(step => step.SkillKey).ToArray());
        CollectionAssert.AreEqual(
            new[] { "1", "2", "3", "4", "X" },
            normalized.FlowerSeedReleaseSequence.Select(step => step.SkillKey).ToArray());
    }

    [TestMethod]
    public void PerTypeConfigsAreUsedAsIs()
    {
        var normal = new AutoBattleReleaseConfig { Name = "主力", Steps = [AutoBattleReleaseStep.CreateSkill("1")] };
        var flower = new AutoBattleReleaseConfig { Name = "花种配置", Steps = [AutoBattleReleaseStep.CreateSkill("2")] };
        var source = new AutoBattleSettings
        {
            NormalReleaseConfigs = [normal],
            FlowerSeedReleaseConfigs = [flower],
            SelectedFlowerSeedReleaseConfigId = flower.Id
        };

        var normalized = AutoBattleSettingsRules.Normalize(source);
        // 各类型独立管理，配置原样使用，互不迁移。
        Assert.AreEqual("主力", normalized.NormalReleaseConfigs.Single().Name);
        Assert.AreEqual("花种配置", normalized.FlowerSeedReleaseConfigs.Single().Name);
        Assert.AreEqual(1, normalized.LegendaryReleaseConfigs.Count);
        Assert.AreEqual(flower.Id, normalized.SelectedFlowerSeedReleaseConfigId);
        CollectionAssert.AreEqual(
            new[] { "2" },
            normalized.FlowerSeedReleaseSequence.Select(step => step.SkillKey).ToArray());
    }

    [TestMethod]
    public void SelectedConfigDrivesRuntimeSequence()
    {
        var first = new AutoBattleReleaseConfig { Name = "配置1", Steps = [AutoBattleReleaseStep.CreateSkill("1")] };
        var second = new AutoBattleReleaseConfig { Name = "配置2", Steps = [AutoBattleReleaseStep.CreateSkill("3"), AutoBattleReleaseStep.CreateSkill("X")] };
        var source = new AutoBattleSettings
        {
            NormalReleaseConfigs = [first, second],
            SelectedNormalReleaseConfigId = second.Id
        };

        var normalized = AutoBattleSettingsRules.Normalize(source);
        CollectionAssert.AreEqual(
            new[] { "3", "X" },
            normalized.ReleaseSequence.Select(step => step.SkillKey).ToArray());

        source.SelectedNormalReleaseConfigId = first.Id;
        normalized = AutoBattleSettingsRules.Normalize(source);
        CollectionAssert.AreEqual(
            new[] { "1" },
            normalized.ReleaseSequence.Select(step => step.SkillKey).ToArray());

        // 无效选中回退到第一个配置。
        source.SelectedNormalReleaseConfigId = "missing";
        normalized = AutoBattleSettingsRules.Normalize(source);
        Assert.AreEqual(first.Id, normalized.SelectedNormalReleaseConfigId);
    }

    [TestMethod]
    public void SkipStepsSurviveInConfigsForNormalBattleRuntimeToIgnore()
    {
        var source = new AutoBattleSettings
        {
            NormalReleaseConfigs =
            [
                new AutoBattleReleaseConfig
                {
                    Name = "跳过配置",
                    Steps = [AutoBattleReleaseStep.CreateSkill("S"), AutoBattleReleaseStep.CreateSkill("2")]
                }
            ]
        };

        var normalized = AutoBattleSettingsRules.Normalize(source);
        // 配置保留跳过步骤；普通战斗运行时由控制器忽略。
        CollectionAssert.AreEqual(
            new[] { "S", "2" },
            normalized.ReleaseSequence.Select(step => step.SkillKey).ToArray());
        Assert.AreEqual("S", normalized.NormalReleaseConfigs[0].Steps[0].SkillKey);
    }

    [TestMethod]
    public void EmptyConfigStepsFallBackToDefaultSequence()
    {
        var source = new AutoBattleSettings
        {
            FlowerSeedReleaseConfigs =
            [
                new AutoBattleReleaseConfig { Name = "空配置", Steps = [AutoBattleReleaseStep.CreateSkill("bad")] }
            ]
        };

        var normalized = AutoBattleSettingsRules.Normalize(source);
        CollectionAssert.AreEqual(
            new[] { "1", "2", "3", "4", "X" },
            normalized.FlowerSeedReleaseSequence.Select(step => step.SkillKey).ToArray());
        Assert.AreEqual(5, normalized.FlowerSeedReleaseConfigs[0].Steps.Count);
    }

    [TestMethod]
    public void CreateDefaultConfigNamePicksSmallestUnusedIndex()
    {
        Assert.AreEqual("配置1", AutoBattleSettingsRules.CreateDefaultConfigName(null));
        Assert.AreEqual("配置1", AutoBattleSettingsRules.CreateDefaultConfigName([]));
        Assert.AreEqual("配置2", AutoBattleSettingsRules.CreateDefaultConfigName(["配置1"]));
        Assert.AreEqual("配置2", AutoBattleSettingsRules.CreateDefaultConfigName(["配置1", "配置3"]));
        Assert.AreEqual("配置4", AutoBattleSettingsRules.CreateDefaultConfigName(["配置1", "配置2", "配置3"]));
    }

    [TestMethod]
    public void ConfigNormalizationIsIdempotent()
    {
        var source = new AutoBattleSettings
        {
            NormalReleaseConfigs =
            [
                new AutoBattleReleaseConfig { Name = "  主力  ", Steps = [AutoBattleReleaseStep.CreateSkill("4")] },
                new AutoBattleReleaseConfig { Name = "", Steps = [] }
            ]
        };

        var first = AutoBattleSettingsRules.Normalize(source);
        var second = AutoBattleSettingsRules.Normalize(first);
        Assert.AreEqual(JsonConvert.SerializeObject(first), JsonConvert.SerializeObject(second));
        Assert.AreEqual("主力", first.NormalReleaseConfigs[0].Name);
        Assert.AreEqual("配置1", first.NormalReleaseConfigs[1].Name);
        Assert.AreEqual(5, first.NormalReleaseConfigs[1].Steps.Count);
        Assert.AreEqual("主力", source.NormalReleaseConfigs[0].Name.Trim());
        Assert.AreEqual(string.Empty, source.NormalReleaseConfigs[1].Name);
    }
}
