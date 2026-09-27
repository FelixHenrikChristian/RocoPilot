using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace RocoPilot.Views.Windows.AutoBattleConfigPages;

public sealed partial class AutoBattleNormalConfigPage : Page
{
    private readonly AutoBattleConfigWindow _owner;

    internal AutoBattleConfigEditor Editor
    {
        get;
    }

    internal AutoBattleNormalConfigPage(
        AutoBattleConfigEditor editor,
        AutoBattleConfigWindow owner)
    {
        Editor = editor;
        _owner = owner;
        InitializeComponent();
    }

    private void AppendSkillButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string skillKey })
        {
            Editor.AppendNormalSkill(skillKey);
        }
    }

    private void ClearReleaseSequenceButton_Click(object sender, RoutedEventArgs e)
    {
        Editor.ClearNormalReleaseSequence();
    }

    private void MoveStepEarlier_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: AutoBattleReleaseEditorItem item })
        {
            Editor.MoveNormalReleaseItemEarlier(item);
        }
    }

    private void MoveStepLater_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: AutoBattleReleaseEditorItem item })
        {
            Editor.MoveNormalReleaseItemLater(item);
        }
    }

    private void DeleteStep_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: AutoBattleReleaseEditorItem item })
        {
            Editor.RemoveNormalReleaseItem(item);
        }
    }

    private void InsertPresetButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: AutoBattlePresetEditorItem preset })
        {
            return;
        }

        if (!Editor.TryInsertSharedPresetIntoNormal(preset, out var error))
        {
            _owner.ShowMessage(error.Title, error.Message);
        }
    }

}
