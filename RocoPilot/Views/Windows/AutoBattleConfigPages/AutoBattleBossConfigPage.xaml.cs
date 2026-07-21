using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace RocoPilot.Views.Windows.AutoBattleConfigPages;

public sealed partial class AutoBattleBossConfigPage : Page
{
    private readonly AutoBattleConfigWindow _owner;

    internal AutoBattleConfigEditor Editor
    {
        get;
    }

    internal AutoBattleBossConfigPage(
        AutoBattleConfigEditor editor,
        AutoBattleConfigWindow owner)
    {
        Editor = editor;
        _owner = owner;
        InitializeComponent();
    }

    private void AppendBossSkillButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string skillKey })
        {
            Editor.AppendBossSkill(skillKey);
        }
    }

    private void ResetBossReleaseSequenceButton_Click(object sender, RoutedEventArgs e)
    {
        Editor.ResetBossReleaseSequence();
    }

    private void ClearBossReleaseSequenceButton_Click(object sender, RoutedEventArgs e)
    {
        Editor.ClearBossReleaseSequence();
    }

    private void BossReleaseItem_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: AutoBattleReleaseEditorItem item } element)
        {
            return;
        }

        var deleteItem = new MenuFlyoutItem
        {
            Text = "删除"
        };
        deleteItem.Click += (_, _) => Editor.RemoveBossReleaseItem(item);

        var flyout = new MenuFlyout();
        flyout.Items.Add(deleteItem);
        flyout.ShowAt(element);
        e.Handled = true;
    }

    private void AppendBossComboSkillButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string skillKey })
        {
            Editor.AppendBossComboSkill(skillKey);
        }
    }

    private void ResetBossComboButton_Click(object sender, RoutedEventArgs e)
    {
        Editor.ResetBossComboSequence();
    }

    private void ClearBossComboButton_Click(object sender, RoutedEventArgs e)
    {
        Editor.ClearBossComboSequence();
    }

    private void BossComboItem_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: AutoBattleReleaseEditorItem item } element)
        {
            return;
        }

        var deleteItem = new MenuFlyoutItem
        {
            Text = "删除"
        };
        deleteItem.Click += (_, _) => Editor.RemoveBossComboItem(item);

        var flyout = new MenuFlyout();
        flyout.Items.Add(deleteItem);
        flyout.ShowAt(element);
        e.Handled = true;
    }

    private void InsertBossReleasePresetButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: AutoBattlePresetEditorItem preset })
        {
            return;
        }

        if (!Editor.TryInsertSharedPresetIntoBossRelease(preset, out var error))
        {
            _owner.ShowMessage(error.Title, error.Message);
        }
    }
}
