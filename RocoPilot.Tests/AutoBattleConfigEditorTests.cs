using Microsoft.VisualStudio.TestTools.UnitTesting;

using RocoPilot.Models.Runtime;
using RocoPilot.Services;
using RocoPilot.Views.Windows.AutoBattleConfigPages;

namespace RocoPilot.Tests;

[TestClass]
public sealed class AutoBattleConfigEditorTests
{
    [TestMethod]
    public void SharesPresetBetweenNormalAndBossPages()
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
        Assert.IsTrue(editor.TryInsertSharedPresetIntoBossRelease(sharedPreset, out _));
        Assert.IsTrue(editor.TryBuildSettings(settings, out var result, out _));

        var insertedStep = result.ReleaseSequence[^1];
        Assert.IsTrue(insertedStep.IsCustom);
        Assert.AreEqual("通用序列", insertedStep.Name);
        Assert.AreEqual("1, X, 2, X, 3, X", insertedStep.Sequence);
        var insertedBossStep = result.BossReleaseSequence[^1];
        Assert.IsTrue(insertedBossStep.IsCustom);
        Assert.AreEqual("通用序列", insertedBossStep.Name);
        Assert.AreEqual("1, X, 2, X, 3, X", insertedBossStep.Sequence);
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
        Assert.IsTrue(editor.TryInsertSharedPresetIntoBossRelease(sharedPreset, out _));
        Assert.IsTrue(editor.TryBuildSettings(settings, out var result, out _));
        Assert.AreEqual("更新后的序列", result.TurnSequencePresets.Single().Name);
        Assert.AreEqual("4, 3, 2", result.TurnSequencePresets.Single().Sequence);
        Assert.AreEqual("更新后的序列", result.ReleaseSequence[^1].Name);
        Assert.AreEqual("4, 3, 2", result.ReleaseSequence[^1].Sequence);
        Assert.AreEqual("更新后的序列", result.BossReleaseSequence[^1].Name);
        Assert.AreEqual("4, 3, 2", result.BossReleaseSequence[^1].Sequence);
    }
}
