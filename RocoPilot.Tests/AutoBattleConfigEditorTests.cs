using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json;

using RocoPilot.Models.Runtime;
using RocoPilot.Services;
using RocoPilot.Views.Windows.AutoBattleConfigPages;

namespace RocoPilot.Tests;

[TestClass]
public sealed class AutoBattleConfigEditorTests
{
    [TestMethod]
    public void ReordersMixedStepsAndUpdatesMoveAvailability()
    {
        var settings = AutoBattleSettings.CreateDefault();
        settings.TurnSequencePresets =
        [
            new AutoBattleTurnSequencePreset { Name = "连招", Sequence = "1, Space" }
        ];
        var editor = new AutoBattleConfigEditor(settings, new KeyboardInputService());
        editor.NormalRelease.Items.Clear();
        editor.NormalRelease.AppendSkill("1");
        var skill = editor.NormalRelease.Items.Single();
        Assert.IsFalse(skill.CanMoveEarlier);
        Assert.IsFalse(skill.CanMoveLater);

        Assert.IsTrue(editor.TryInsertSharedPreset(editor.SharedPresetItems.Single(), editor.NormalRelease, out _));
        var preset = editor.NormalRelease.Items[1];
        editor.NormalRelease.AppendSkill("4");
        var last = editor.NormalRelease.Items[2];
        Assert.IsTrue(skill.CanMoveLater);
        Assert.IsFalse(last.CanMoveLater);

        editor.NormalRelease.MoveEarlier(preset);
        Assert.AreSame(preset, editor.NormalRelease.Items[0]);
        Assert.IsFalse(preset.CanMoveEarlier);
        Assert.AreEqual(1, preset.Position);
        editor.NormalRelease.MoveEarlier(preset);
        Assert.AreSame(preset, editor.NormalRelease.Items[0]);

        editor.NormalRelease.MoveLater(skill);
        Assert.AreSame(skill, editor.NormalRelease.Items[2]);
        Assert.IsFalse(skill.CanMoveLater);
        editor.NormalRelease.Items.Remove(skill);
        Assert.IsFalse(last.CanMoveLater);
        Assert.AreEqual(2, last.Position);

        Assert.IsTrue(editor.TryBuildSettings(settings, out var result, out _));
        Assert.AreEqual(2, result.ReleaseSequence.Count);
        Assert.IsTrue(result.ReleaseSequence[0].IsCustom);
        Assert.AreEqual("1, Space", result.ReleaseSequence[0].Sequence);
        Assert.AreEqual("4", result.ReleaseSequence[1].SkillKey);
    }

    [TestMethod]
    public void SharesPresetAcrossPages()
    {
        var settings = AutoBattleSettings.CreateDefault();
        settings.TurnSequencePresets =
        [
            new AutoBattleTurnSequencePreset
            {
                Name = "通用序列",
                Sequence = "1, X, 2, X, 3, X"
            }
        ];

        var editor = new AutoBattleConfigEditor(settings, new KeyboardInputService());
        var sharedPreset = editor.SharedPresetItems.Single();

        Assert.IsTrue(editor.TryInsertSharedPreset(sharedPreset, editor.NormalRelease, out _));
        Assert.IsTrue(editor.TryBuildSettings(settings, out var result, out _));

        var insertedStep = result.ReleaseSequence[^1];
        Assert.IsTrue(insertedStep.IsCustom);
        Assert.AreEqual("通用序列", insertedStep.Name);
        Assert.AreEqual("1, X, 2, X, 3, X", insertedStep.Sequence);
        Assert.AreEqual(1, result.TurnSequencePresets.Count);
    }

    [TestMethod]
    public void UsesLatestSharedPresetValuesAcrossPages()
    {
        var settings = AutoBattleSettings.CreateDefault();
        settings.TurnSequencePresets =
        [
            new AutoBattleTurnSequencePreset
            {
                Name = "旧名称",
                Sequence = "1"
            }
        ];

        var editor = new AutoBattleConfigEditor(settings, new KeyboardInputService());
        var sharedPreset = editor.SharedPresetItems.Single();
        sharedPreset.Name = "更新后的序列";
        sharedPreset.Sequence = "4, 3, 2";

        Assert.IsTrue(editor.TryInsertSharedPreset(sharedPreset, editor.NormalRelease, out _));
        Assert.IsTrue(editor.TryBuildSettings(settings, out var result, out _));
        Assert.AreEqual("更新后的序列", result.TurnSequencePresets.Single().Name);
        Assert.AreEqual("4, 3, 2", result.TurnSequencePresets.Single().Sequence);
        Assert.AreEqual("更新后的序列", result.ReleaseSequence[^1].Name);
        Assert.AreEqual("4, 3, 2", result.ReleaseSequence[^1].Sequence);
    }

    [TestMethod]
    public void UpdatesEveryInsertedReferenceAndNotifiesDisplayBindings()
    {
        var settings = AutoBattleSettings.CreateDefault();
        settings.TurnSequencePresets = [new() { Name = "连招", Sequence = "1, Space" }];
        var editor = new AutoBattleConfigEditor(settings, new KeyboardInputService());
        editor.NormalRelease.Items.Clear();
        var preset = editor.SharedPresetItems.Single();
        Assert.IsTrue(editor.TryInsertSharedPreset(preset, editor.NormalRelease, out _));
        Assert.IsTrue(editor.TryInsertSharedPreset(preset, editor.NormalRelease, out _));
        var changes = new List<string?>();
        editor.NormalRelease.Items[0].PropertyChanged += (_, e) => changes.Add(e.PropertyName);

        preset.Name = "新连招";
        preset.Sequence = "4, X";

        Assert.IsTrue(editor.NormalRelease.Items.All(item => item.Name == "新连招" && item.Sequence == "4, X"));
        StringAssert.Contains(editor.NormalRelease.Summary, "新连招");
        CollectionAssert.Contains(changes, "StepTitle");
        CollectionAssert.Contains(changes, "DetailText");
        Assert.IsTrue(editor.TryBuildSettings(settings, out var result, out _));
        Assert.IsTrue(result.ReleaseSequence.All(step => step.PresetId == result.TurnSequencePresets.Single().Id));
    }

    [TestMethod]
    public void KeepsAssociationAfterSavingReopeningAndRenamingSameNamedPresets()
    {
        var settings = AutoBattleSettings.CreateDefault();
        settings.TurnSequencePresets =
        [
            new() { Name = "连招", Sequence = "1" },
            new() { Name = "连招", Sequence = "2" }
        ];
        var editor = new AutoBattleConfigEditor(settings, new KeyboardInputService());
        Assert.IsTrue(editor.TryInsertSharedPreset(editor.SharedPresetItems[1], editor.NormalRelease, out _));
        editor.NormalRelease.MoveEarlier(editor.NormalRelease.Items[^1]);
        Assert.IsTrue(editor.TryBuildSettings(settings, out var saved, out _));
        var reloaded = JsonConvert.DeserializeObject<AutoBattleSettings>(JsonConvert.SerializeObject(saved))!;
        var reopened = new AutoBattleConfigEditor(reloaded, new KeyboardInputService());
        reopened.SharedPresetItems[0].Sequence = "3";
        reopened.SharedPresetItems[1].Name = "改名";
        reopened.SharedPresetItems[1].Sequence = "4, Space";

        var linked = reopened.NormalRelease.Items.Single(item => item.IsCustom);
        Assert.AreEqual("改名", linked.Name);
        Assert.AreEqual("4, Space", linked.Sequence);
        Assert.IsTrue(reopened.TryBuildSettings(reloaded, out var result, out _));
        Assert.AreEqual("4, Space", AutoBattleSettingsRules.BuildReleaseSequence(result, result.ReleaseSequence.Single(step => step.IsCustom)));
    }

    [TestMethod]
    public void RemovingPresetKeepsInsertedContentAndDoesNotRelinkToSameNamedReplacement()
    {
        var settings = AutoBattleSettings.CreateDefault();
        settings.TurnSequencePresets = [new() { Name = "连招", Sequence = "1, Space" }];
        var editor = new AutoBattleConfigEditor(settings, new KeyboardInputService());
        var preset = editor.SharedPresetItems.Single();
        Assert.IsTrue(editor.TryInsertSharedPreset(preset, editor.NormalRelease, out _));
        preset.Sequence = "4, X";
        editor.RemoveSharedPreset(preset);
        preset.Sequence = "2";
        editor.SharedPresetItems.Add(new() { Name = "连招", Sequence = "4, X" });

        Assert.IsTrue(editor.TryBuildSettings(settings, out var result, out _));
        Assert.AreEqual(1, result.TurnSequencePresets.Count);
        Assert.AreEqual("4, X", result.ReleaseSequence[^1].Sequence);
        Assert.AreEqual(preset.Id, result.ReleaseSequence[^1].PresetId);
        var reopened = new AutoBattleConfigEditor(result, new KeyboardInputService());
        reopened.SharedPresetItems.Single().Sequence = "3";
        Assert.AreEqual("4, X", reopened.NormalRelease.Items[^1].Sequence);
    }

    [TestMethod]
    public void RejectsInvalidChangesToLinkedSequence()
    {
        var settings = AutoBattleSettings.CreateDefault();
        settings.TurnSequencePresets = [new() { Name = "连招", Sequence = "1" }];
        var editor = new AutoBattleConfigEditor(settings, new KeyboardInputService());
        var preset = editor.SharedPresetItems.Single();
        Assert.IsTrue(editor.TryInsertSharedPreset(preset, editor.NormalRelease, out _));
        preset.Sequence = "not-a-key";

        Assert.IsFalse(editor.TryBuildSettings(settings, out _, out var error));
        Assert.AreEqual(AutoBattleConfigSection.SharedSequences, error.Section);
        Assert.AreEqual("1", settings.TurnSequencePresets.Single().Sequence);
    }

    [TestMethod]
    public void BuildsDefaultBloodlineCaptureFilterSettings()
    {
        var settings = AutoBattleSettings.CreateDefault();
        var editor = new AutoBattleConfigEditor(settings, new KeyboardInputService());

        Assert.IsTrue(editor.TryBuildSettings(settings, out var result, out _));
        Assert.IsNotNull(result.BloodlineCaptureFilter);
        Assert.IsTrue(result.BloodlineCaptureFilter.IsEnabled);
        Assert.IsTrue(result.BloodlineCaptureFilter.CaptureQiYi);
        Assert.IsFalse(result.BloodlineCaptureFilter.CaptureHunXue);
        Assert.IsTrue(result.BloodlineCaptureFilter.CaptureWuRan);
        Assert.IsFalse(result.BloodlineCaptureFilter.CaptureNormal);
        Assert.IsTrue(result.BloodlineCaptureFilter.CaptureUnrecognized);
    }

    [TestMethod]
    public void PersistsEditedBloodlineCaptureFilterSettings()
    {
        var settings = AutoBattleSettings.CreateDefault();
        var editor = new AutoBattleConfigEditor(settings, new KeyboardInputService())
        {
            BloodlineCaptureFilterEnabled = true,
            CaptureBloodlineQiYi = false,
            CaptureBloodlineHunXue = true,
            CaptureBloodlineWuRan = false,
            CaptureBloodlineNormal = true,
            CaptureBloodlineUnrecognized = false
        };

        Assert.IsTrue(editor.TryBuildSettings(settings, out var result, out _));
        Assert.IsTrue(result.BloodlineCaptureFilter.IsEnabled);
        Assert.IsFalse(result.BloodlineCaptureFilter.CaptureQiYi);
        Assert.IsTrue(result.BloodlineCaptureFilter.CaptureHunXue);
        Assert.IsFalse(result.BloodlineCaptureFilter.CaptureWuRan);
        Assert.IsTrue(result.BloodlineCaptureFilter.CaptureNormal);
        Assert.IsFalse(result.BloodlineCaptureFilter.CaptureUnrecognized);
    }

    [TestMethod]
    public void EditsFlowerSeedSequenceWithoutChangingNormalSequenceOrCurrentInputSettings()
    {
        var source = AutoBattleSettings.CreateDefault();
        source.ReleaseSequence = [AutoBattleReleaseStep.CreateSkill("4")];
        var editor = new AutoBattleConfigEditor(source, new KeyboardInputService());
        editor.FlowerSeedRelease.Items.Clear();
        editor.FlowerSeedRelease.AppendSkill("3");
        editor.FlowerSeedRelease.AppendSkill("X");
        editor.FlowerSeedRelease.MoveEarlier(editor.FlowerSeedRelease.Items[1]);
        var current = source.Clone();
        current.KeyboardHoldDurationMs = 250;
        current.IsEnabled = true;

        Assert.IsTrue(editor.TryBuildSettings(current, out var result, out _));
        Assert.AreEqual("4", result.ReleaseSequence.Single().SkillKey);
        CollectionAssert.AreEqual(new[] { "X", "3" }, result.FlowerSeedReleaseSequence.Select(step => step.SkillKey).ToArray());
        Assert.AreEqual(250, result.KeyboardHoldDurationMs);
        Assert.IsTrue(result.IsEnabled);
        Assert.AreEqual(5, source.FlowerSeedReleaseSequence.Count);
    }

    [TestMethod]
    public void SharedPresetUpdatesReferencesInBothBattleTypesAfterReopening()
    {
        var settings = AutoBattleSettings.CreateDefault();
        settings.TurnSequencePresets = [new() { Name = "连招", Sequence = "1, Space" }];
        var editor = new AutoBattleConfigEditor(settings, new KeyboardInputService());
        editor.NormalRelease.Items.Clear();
        editor.FlowerSeedRelease.Items.Clear();
        var preset = editor.SharedPresetItems.Single();
        Assert.IsTrue(editor.TryInsertSharedPreset(preset, editor.NormalRelease, out _));
        Assert.IsTrue(editor.TryInsertSharedPreset(preset, editor.FlowerSeedRelease, out _));
        Assert.IsTrue(editor.TryBuildSettings(settings, out var saved, out _));
        var reloaded = JsonConvert.DeserializeObject<AutoBattleSettings>(JsonConvert.SerializeObject(saved))!;
        var reopened = new AutoBattleConfigEditor(reloaded, new KeyboardInputService());
        reopened.SharedPresetItems.Single().Name = "新连招";
        reopened.SharedPresetItems.Single().Sequence = "3, X";

        StringAssert.Contains(reopened.NormalRelease.Summary, "新连招");
        StringAssert.Contains(reopened.FlowerSeedRelease.Summary, "新连招");
        Assert.AreEqual("3, X", reopened.FlowerSeedRelease.Items.Single().Sequence);
        Assert.IsTrue(reopened.TryBuildSettings(reloaded, out var result, out _));
        Assert.AreEqual(preset.Id, result.FlowerSeedReleaseSequence.Single().PresetId);
        Assert.AreEqual("3, X", result.ReleaseSequence.Single().Sequence);
        Assert.AreEqual("3, X", result.FlowerSeedReleaseSequence.Single().Sequence);
    }

    [TestMethod]
    public void RejectsEmptyFlowerSeedSequenceInFlowerSeedSection()
    {
        var settings = AutoBattleSettings.CreateDefault();
        var editor = new AutoBattleConfigEditor(settings, new KeyboardInputService());
        editor.FlowerSeedRelease.Items.Clear();

        Assert.IsFalse(editor.TryBuildSettings(settings, out _, out var error));
        Assert.AreEqual(AutoBattleConfigSection.FlowerSeed, error.Section);
        Assert.AreEqual(5, settings.FlowerSeedReleaseSequence.Count);
    }
}
