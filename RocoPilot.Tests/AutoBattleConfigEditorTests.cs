using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json;

using RocoPilot.Models.Runtime;
using RocoPilot.Models.Spirits;
using RocoPilot.Services;
using RocoPilot.Views.Windows.AutoBattleConfigPages;

namespace RocoPilot.Tests;

[TestClass]
public sealed class AutoBattleConfigEditorTests
{
    [TestMethod]
    public void RejectsOrdinaryBattleWithOnlySkippedSteps()
    {
        var settings = AutoBattleSettings.CreateDefault();
        var editor = new AutoBattleConfigEditor(settings, new KeyboardInputService());
        var sequence = editor.NormalConfigs.SelectedSequence;
        sequence.Items.Clear();
        sequence.AppendSkill("S");
        Assert.IsFalse(editor.TryBuildSettings(settings, out _, out var error));
        Assert.AreEqual(AutoBattleConfigSection.Normal, error.Section);
        StringAssert.Contains(error.Message, "至少保留一个可执行");
    }

    [TestMethod]
    public void ReordersMixedStepsAndSavesTheirNewPositions()
    {
        var settings = AutoBattleSettings.CreateDefault();
        settings.TurnSequencePresets =
        [
            new AutoBattleTurnSequencePreset { Name = "连招", Sequence = "1, Space" }
        ];
        var editor = new AutoBattleConfigEditor(settings, new KeyboardInputService());
        // GridView 原生重排依次移除、插入项目，而不是发送集合 Move 通知。
        var sequence = editor.NormalConfigs.SelectedSequence;
        sequence.Items.Clear();
        sequence.AppendSkill("1");
        var skill = sequence.Items.Single();

        Assert.IsTrue(editor.TryInsertSharedPreset(editor.SharedPresetItems.Single(), sequence, out _));
        var preset = sequence.Items[1];
        sequence.AppendSkill("4");
        var last = sequence.Items[2];

        sequence.Items.RemoveAt(1);
        sequence.Items.Insert(0, preset);
        Assert.AreSame(preset, sequence.Items[0]);
        Assert.AreEqual(1, preset.Position);

        sequence.Items.RemoveAt(1);
        sequence.Items.Insert(2, skill);
        Assert.AreSame(skill, sequence.Items[2]);
        CollectionAssert.AreEqual(new[] { 1, 2, 3 }, sequence.Items.Select(item => item.Position).ToArray());
        StringAssert.StartsWith(sequence.Summary, "连招 → 4 → 1");
        sequence.Items.Remove(skill);
        Assert.AreEqual(2, last.Position);

        Assert.IsTrue(editor.TryBuildSettings(settings, out var result, out _));
        Assert.AreEqual(2, result.ReleaseSequence.Count);
        Assert.IsTrue(result.ReleaseSequence[0].IsCustom);
        Assert.AreEqual("1, Space", result.ReleaseSequence[0].Sequence);
        Assert.AreEqual("4", result.ReleaseSequence[1].SkillKey);
    }

    [TestMethod]
    public void ClickReorderMovesSourceStepToTargetPosition()
    {
        // 提权时拖拽不可用，改由点击选中 + 点击目标完成移动；这里直接验证移动与序号刷新。
        var sequence = new AutoBattleReleaseSequenceEditor(supportsSkipTurn: false);
        sequence.AppendSkill("1");
        sequence.AppendSkill("3");
        sequence.AppendSkill("4");
        var first = sequence.Items[0];
        var last = sequence.Items[2];

        Assert.IsTrue(AutoBattleReleaseSequenceEditor.TryMove(sequence.Items, first, last));

        CollectionAssert.AreEqual(new[] { "3", "4", "1" }, sequence.Items.Select(item => item.SkillKey).ToArray());
        CollectionAssert.AreEqual(new[] { 1, 2, 3 }, sequence.Items.Select(item => item.Position).ToArray());
        Assert.AreSame(last, sequence.Items[1]);
        Assert.IsTrue(first.IsMoveSource == false);
    }

    [TestMethod]
    public void ClickReorderIgnoresSameOrForeignItems()
    {
        var sequence = new AutoBattleReleaseSequenceEditor(supportsSkipTurn: true);
        sequence.AppendSkill("1");
        sequence.AppendSkill("3");
        var step = sequence.Items[0];

        Assert.IsFalse(AutoBattleReleaseSequenceEditor.TryMove(sequence.Items, step, step));
        Assert.IsFalse(AutoBattleReleaseSequenceEditor.TryMove(
            sequence.Items,
            step,
            AutoBattleReleaseEditorItem.CreateSkill("4")));
        CollectionAssert.AreEqual(new[] { "1", "3" }, sequence.Items.Select(item => item.SkillKey).ToArray());
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

        Assert.IsTrue(editor.TryInsertSharedPreset(sharedPreset, editor.NormalConfigs.SelectedSequence, out _));
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

        Assert.IsTrue(editor.TryInsertSharedPreset(sharedPreset, editor.NormalConfigs.SelectedSequence, out _));
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
        editor.NormalConfigs.SelectedSequence.Items.Clear();
        var preset = editor.SharedPresetItems.Single();
        Assert.IsTrue(editor.TryInsertSharedPreset(preset, editor.NormalConfigs.SelectedSequence, out _));
        Assert.IsTrue(editor.TryInsertSharedPreset(preset, editor.NormalConfigs.SelectedSequence, out _));
        var changes = new List<string?>();
        editor.NormalConfigs.SelectedSequence.Items[0].PropertyChanged += (_, e) => changes.Add(e.PropertyName);

        preset.Name = "新连招";
        preset.Sequence = "4, X";

        Assert.IsTrue(editor.NormalConfigs.SelectedSequence.Items.All(item => item.Name == "新连招" && item.Sequence == "4, X"));
        StringAssert.Contains(editor.NormalConfigs.SelectedSequence.Summary, "新连招");
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
        var sequence = editor.NormalConfigs.SelectedSequence;
        Assert.IsTrue(editor.TryInsertSharedPreset(editor.SharedPresetItems[1], sequence, out _));
        sequence.Items.Move(sequence.Items.Count - 1, sequence.Items.Count - 2);
        Assert.IsTrue(editor.TryBuildSettings(settings, out var saved, out _));
        var reloaded = JsonConvert.DeserializeObject<AutoBattleSettings>(JsonConvert.SerializeObject(saved))!;
        var reopened = new AutoBattleConfigEditor(reloaded, new KeyboardInputService());
        reopened.SharedPresetItems[0].Sequence = "3";
        reopened.SharedPresetItems[1].Name = "改名";
        reopened.SharedPresetItems[1].Sequence = "4, Space";

        var linked = reopened.NormalConfigs.SelectedSequence.Items.Single(item => item.IsCustom);
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
        Assert.IsTrue(editor.TryInsertSharedPreset(preset, editor.NormalConfigs.SelectedSequence, out _));
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
        Assert.AreEqual("4, X", reopened.NormalConfigs.SelectedSequence.Items[^1].Sequence);
    }

    [TestMethod]
    public void RejectsInvalidChangesToLinkedSequence()
    {
        var settings = AutoBattleSettings.CreateDefault();
        settings.TurnSequencePresets = [new() { Name = "连招", Sequence = "1" }];
        var editor = new AutoBattleConfigEditor(settings, new KeyboardInputService());
        var preset = editor.SharedPresetItems.Single();
        Assert.IsTrue(editor.TryInsertSharedPreset(preset, editor.NormalConfigs.SelectedSequence, out _));
        preset.Sequence = "not-a-key";

        Assert.IsFalse(editor.TryBuildSettings(settings, out _, out var error));
        Assert.AreEqual(AutoBattleConfigSection.SharedSequences, error.Section);
        Assert.AreEqual("1", settings.TurnSequencePresets.Single().Sequence);
    }

    [TestMethod]
    public void SelectingAnyCatalogFormPersistsWholeChainsSeparatelyAndRemovesThemTogether()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Configuration", "Spirits", "Sources", "biligame", "spirits.json");
        var catalog = JsonConvert.DeserializeObject<SpiritCatalogDocument>(File.ReadAllText(path))!.Spirits;
        var settings = JsonConvert.DeserializeObject<AutoBattleSettings>("{}")!;
        var editor = new AutoBattleConfigEditor(settings, new KeyboardInputService());
        Assert.AreEqual(SpiritCaptureListMode.Off, editor.SpiritCaptureFilter.Mode);
        editor.SpiritCaptureFilter.Mode = SpiritCaptureListMode.Blacklist;
        foreach (var name in new[] { "熔岩布丁", "果冻" })
            editor.AddSpiritChain(catalog.Single(item => item.Name == name), catalog);
        var jellyNames = new[] { "果冻", "抹茶布丁", "椰浆布丁", "熔岩布丁" };
        CollectionAssert.AreEquivalent(jellyNames, editor.SpiritCaptureFilter.Blacklist.Single().Names);
        editor.SpiritCaptureFilter.Mode = SpiritCaptureListMode.Whitelist;
        editor.AddSpiritChain(catalog.Single(item => item.Name == "雪绒鸟（夏天的样子）"), catalog);
        editor.SpiritCaptureFilter.Mode = SpiritCaptureListMode.Off;
        Assert.IsTrue(editor.TryBuildSettings(settings, out var edited, out _));
        var saved = JsonConvert.DeserializeObject<AutoBattleSettings>(JsonConvert.SerializeObject(edited))!;
        var reopened = new AutoBattleConfigEditor(saved, new KeyboardInputService());
        var filter = reopened.SpiritCaptureFilter;
        Assert.IsEmpty(filter.ActiveChains);
        filter.Mode = SpiritCaptureListMode.Whitelist;
        CollectionAssert.Contains(filter.ActiveChains.Single().Names, "霜翼领主（秋天的样子）");
        Assert.AreEqual(string.Empty, filter.GetExclusionReason("霜翼领主"));
        reopened.RemoveSpiritChain(filter.ActiveChains.Single());
        Assert.IsEmpty(filter.Whitelist);
        CollectionAssert.AreEquivalent(jellyNames, filter.Blacklist.Single().Names);
        Assert.HasCount(1, saved.SpiritCaptureFilter.Whitelist);
    }

    [TestMethod]
    public void ChainCardListsSavedFormsBaseFirstWithSameNamedVariantsMerged()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Configuration", "Spirits", "Sources", "biligame", "spirits.json");
        var catalog = JsonConvert.DeserializeObject<SpiritCatalogDocument>(File.ReadAllText(path))!.Spirits;
        var editor = new AutoBattleConfigEditor(AutoBattleSettings.CreateDefault(), new KeyboardInputService());
        editor.SpiritCaptureFilter.Mode = SpiritCaptureListMode.Whitelist;
        // 季节形态同名只显示一个；图鉴里圣光迪莫排在迪莫前面，卡片仍以初始形态开头。
        editor.AddSpiritChain(catalog.Single(item => item.Name == "雪绒鸟（夏天的样子）"), catalog);
        editor.AddSpiritChain(catalog.Single(item => item.Name == "圣火迪莫"), catalog);
        var bird = editor.SpiritCaptureFilter.Whitelist[0];
        var expected = new[] { "雪绒鸟", "冬羽雀", "岚鸟", "霜翼领主" };

        var forms = SpiritChainForms.Resolve(bird, catalog);
        CollectionAssert.AreEqual(expected, forms.Select(form => form.Name).ToArray());
        Assert.IsTrue(forms.All(form => form.AvatarPath.Length > 0));
        Assert.AreEqual("雪绒鸟", SpiritChainForms.Title(forms));
        // 第二行列出整条链，标题里的初始形态也在内。
        Assert.AreEqual("雪绒鸟、冬羽雀、岚鸟、霜翼领主", SpiritChainForms.Detail(forms));
        Assert.AreEqual("迪莫", SpiritChainForms.Title(SpiritChainForms.Resolve(editor.SpiritCaptureFilter.Whitelist[1], catalog)));

        // 图鉴还没加载完时先按名单里存的名字显示，同样不重复、没有头像。
        var offline = SpiritChainForms.Resolve(bird, null);
        CollectionAssert.AreEqual(expected, offline.Select(form => form.Name).ToArray());
        Assert.IsTrue(offline.All(form => form.AvatarPath.Length == 0));

        // 卡片只列名单里实际存着的名字：图鉴里同一条链后来多出的形态不会冒出来。
        bird.Names.RemoveAll(name => name.StartsWith("霜翼领主", StringComparison.Ordinal));
        CollectionAssert.AreEqual(expected[..3], SpiritChainForms.Resolve(bird, catalog).Select(form => form.Name).ToArray());
        Assert.AreEqual("无其他形态", SpiritChainForms.Detail(SpiritChainForms.Resolve(new() { Names = ["果冻"] }, catalog)));
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
        editor.FlowerSeedConfigs.SelectedSequence.Items.Clear();
        editor.FlowerSeedConfigs.SelectedSequence.AppendSkill("3");
        editor.FlowerSeedConfigs.SelectedSequence.AppendSkill("X");
        editor.FlowerSeedConfigs.SelectedSequence.Items.Move(1, 0);
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
    public void SharedPresetUpdatesReferencesInAllBattleTypesAfterReopening()
    {
        var settings = AutoBattleSettings.CreateDefault();
        settings.TurnSequencePresets = [new() { Name = "连招", Sequence = "1, Space" }];
        var editor = new AutoBattleConfigEditor(settings, new KeyboardInputService());
        editor.NormalConfigs.SelectedSequence.Items.Clear();
        editor.FlowerSeedConfigs.SelectedSequence.Items.Clear();
        editor.LegendaryConfigs.SelectedSequence.Items.Clear();
        var preset = editor.SharedPresetItems.Single();
        Assert.IsTrue(editor.TryInsertSharedPreset(preset, editor.NormalConfigs.SelectedSequence, out _));
        Assert.IsTrue(editor.TryInsertSharedPreset(preset, editor.FlowerSeedConfigs.SelectedSequence, out _));
        Assert.IsTrue(editor.TryInsertSharedPreset(preset, editor.LegendaryConfigs.SelectedSequence, out _));
        Assert.IsTrue(editor.TryBuildSettings(settings, out var saved, out _));
        var reloaded = JsonConvert.DeserializeObject<AutoBattleSettings>(JsonConvert.SerializeObject(saved))!;
        var reopened = new AutoBattleConfigEditor(reloaded, new KeyboardInputService());
        reopened.SharedPresetItems.Single().Name = "新连招";
        reopened.SharedPresetItems.Single().Sequence = "3, X";

        StringAssert.Contains(reopened.NormalConfigs.SelectedSequence.Summary, "新连招");
        StringAssert.Contains(reopened.FlowerSeedConfigs.SelectedSequence.Summary, "新连招");
        StringAssert.Contains(reopened.LegendaryConfigs.SelectedSequence.Summary, "新连招");
        Assert.AreEqual("3, X", reopened.FlowerSeedConfigs.SelectedSequence.Items.Single().Sequence);
        Assert.IsTrue(reopened.TryBuildSettings(reloaded, out var result, out _));
        Assert.AreEqual(preset.Id, result.FlowerSeedReleaseSequence.Single().PresetId);
        Assert.AreEqual("3, X", result.ReleaseSequence.Single().Sequence);
        Assert.AreEqual("3, X", result.FlowerSeedReleaseSequence.Single().Sequence);
        Assert.AreEqual("3, X", result.LegendaryReleaseSequence.Single().Sequence);
        Assert.AreEqual(preset.Id, result.LegendaryReleaseSequence.Single().PresetId);
    }

    [TestMethod]
    public void LegendarySequenceIsSavedAndReopenedIndependently()
    {
        var source = AutoBattleSettings.CreateDefault();
        source.ReleaseSequence = [AutoBattleReleaseStep.CreateSkill("1")];
        source.FlowerSeedReleaseSequence = [AutoBattleReleaseStep.CreateSkill("2")];
        var editor = new AutoBattleConfigEditor(source, new KeyboardInputService());
        editor.LegendaryConfigs.SelectedSequence.Items.Clear();
        editor.LegendaryConfigs.SelectedSequence.AppendSkill("4");
        Assert.IsTrue(editor.TryBuildSettings(source, out var result, out _));
        var reloaded = JsonConvert.DeserializeObject<AutoBattleSettings>(JsonConvert.SerializeObject(result))!;
        var reopened = new AutoBattleConfigEditor(reloaded, new KeyboardInputService());

        Assert.AreEqual("1", reopened.NormalConfigs.SelectedSequence.Items.Single().SkillKey);
        Assert.AreEqual("2", reopened.FlowerSeedConfigs.SelectedSequence.Items.Single().SkillKey);
        Assert.AreEqual("4", reopened.LegendaryConfigs.SelectedSequence.Items.Single().SkillKey);
        Assert.AreEqual(5, source.LegendaryReleaseSequence.Count);

        reopened.FlowerSeedConfigs.SelectedSequence.AppendSkill("3");
        Assert.IsTrue(reopened.TryBuildSettings(reloaded, out var updated, out _));
        Assert.AreEqual("4", updated.LegendaryReleaseSequence.Single().SkillKey);
        reopened.LegendaryConfigs.SelectedSequence.Items.Clear();
        Assert.IsFalse(reopened.TryBuildSettings(updated, out _, out var error));
        Assert.AreEqual(AutoBattleConfigSection.Legendary, error.Section);
    }

    [TestMethod]
    public void RejectsEmptyFlowerSeedSequenceInFlowerSeedSection()
    {
        var settings = AutoBattleSettings.CreateDefault();
        var editor = new AutoBattleConfigEditor(settings, new KeyboardInputService());
        editor.FlowerSeedConfigs.SelectedSequence.Items.Clear();

        Assert.IsFalse(editor.TryBuildSettings(settings, out _, out var error));
        Assert.AreEqual(AutoBattleConfigSection.FlowerSeed, error.Section);
        Assert.AreEqual(5, settings.FlowerSeedReleaseSequence.Count);
    }
}
