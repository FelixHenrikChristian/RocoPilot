using Microsoft.VisualStudio.TestTools.UnitTesting;

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
        editor.ClearNormalReleaseSequence();
        editor.AppendNormalSkill("1");
        var skill = editor.NormalReleaseItems.Single();
        Assert.IsFalse(skill.CanMoveEarlier);
        Assert.IsFalse(skill.CanMoveLater);

        Assert.IsTrue(editor.TryInsertSharedPresetIntoNormal(editor.SharedPresetItems.Single(), out _));
        var preset = editor.NormalReleaseItems[1];
        editor.AppendNormalSkill("4");
        var last = editor.NormalReleaseItems[2];
        Assert.IsTrue(skill.CanMoveLater);
        Assert.IsFalse(last.CanMoveLater);

        editor.MoveNormalReleaseItemEarlier(preset);
        Assert.AreSame(preset, editor.NormalReleaseItems[0]);
        Assert.IsFalse(preset.CanMoveEarlier);
        Assert.AreEqual(1, preset.Position);
        editor.MoveNormalReleaseItemEarlier(preset);
        Assert.AreSame(preset, editor.NormalReleaseItems[0]);

        editor.MoveNormalReleaseItemLater(skill);
        Assert.AreSame(skill, editor.NormalReleaseItems[2]);
        Assert.IsFalse(skill.CanMoveLater);
        editor.RemoveNormalReleaseItem(skill);
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

        Assert.IsTrue(editor.TryInsertSharedPresetIntoNormal(sharedPreset, out _));
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

        Assert.IsTrue(editor.TryInsertSharedPresetIntoNormal(sharedPreset, out _));
        Assert.IsTrue(editor.TryBuildSettings(settings, out var result, out _));
        Assert.AreEqual("更新后的序列", result.TurnSequencePresets.Single().Name);
        Assert.AreEqual("4, 3, 2", result.TurnSequencePresets.Single().Sequence);
        Assert.AreEqual("更新后的序列", result.ReleaseSequence[^1].Name);
        Assert.AreEqual("4, 3, 2", result.ReleaseSequence[^1].Sequence);
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
}
