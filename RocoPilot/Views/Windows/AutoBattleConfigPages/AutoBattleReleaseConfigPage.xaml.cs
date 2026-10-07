using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace RocoPilot.Views.Windows.AutoBattleConfigPages;

public sealed partial class AutoBattleReleaseConfigPage : Page
{
    private readonly AutoBattleConfigWindow _owner;

    internal AutoBattleConfigEditor Editor
    {
        get;
    }

    internal AutoBattleReleaseSequenceEditor ReleaseEditor { get; }

    internal string BattleTypeName { get; }

    internal string Description { get; }

    internal AutoBattleReleaseConfigPage(
        AutoBattleConfigEditor editor,
        AutoBattleConfigWindow owner,
        AutoBattleConfigSection section)
    {
        Editor = editor;
        _owner = owner;
        ReleaseEditor = section == AutoBattleConfigSection.FlowerSeed ? editor.FlowerSeedRelease : editor.NormalRelease;
        BattleTypeName = section == AutoBattleConfigSection.FlowerSeed ? "花种战斗" : "普通战斗";
        Description = section == AutoBattleConfigSection.FlowerSeed
            ? "轮到自己操作时按顺序释放技能，每场挑战从第一步开始；命定与稀兽花种共用此配置。"
            : "按顺序循环执行每个技能回合的动作。";
        InitializeComponent();
    }

    private void AppendSkillButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string skillKey })
        {
            ReleaseEditor.AppendSkill(skillKey);
        }
    }

    private void ClearReleaseSequenceButton_Click(object sender, RoutedEventArgs e)
    {
        ReleaseEditor.Items.Clear();
    }

    private void MoveStepEarlier_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: AutoBattleReleaseEditorItem item })
        {
            ReleaseEditor.MoveEarlier(item);
        }
    }

    private void MoveStepLater_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: AutoBattleReleaseEditorItem item })
        {
            ReleaseEditor.MoveLater(item);
        }
    }

    private void DeleteStep_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: AutoBattleReleaseEditorItem item })
        {
            ReleaseEditor.Items.Remove(item);
        }
    }

    private void InsertPresetButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: AutoBattlePresetEditorItem preset })
        {
            return;
        }

        if (!Editor.TryInsertSharedPreset(preset, ReleaseEditor, out var error))
        {
            _owner.ShowMessage(error.Title, error.Message);
        }
    }

}
