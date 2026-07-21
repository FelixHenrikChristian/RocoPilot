using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

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

    private void ResetReleaseSequenceButton_Click(object sender, RoutedEventArgs e)
    {
        Editor.ResetNormalReleaseSequence();
    }

    private void ClearReleaseSequenceButton_Click(object sender, RoutedEventArgs e)
    {
        Editor.ClearNormalReleaseSequence();
    }

    private void ReleaseItem_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: AutoBattleReleaseEditorItem item } element)
        {
            return;
        }

        var deleteItem = new MenuFlyoutItem
        {
            Text = "删除"
        };
        deleteItem.Click += (_, _) => Editor.RemoveNormalReleaseItem(item);

        var flyout = new MenuFlyout();
        flyout.Items.Add(deleteItem);
        flyout.ShowAt(element);
        e.Handled = true;
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
