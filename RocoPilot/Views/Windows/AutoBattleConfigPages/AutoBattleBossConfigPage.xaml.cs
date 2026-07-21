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

        ShowReleaseItemMenu(
            element,
            canMoveEarlier: Editor.CanMoveBossReleaseItemEarlier(item),
            canMoveLater: Editor.CanMoveBossReleaseItemLater(item),
            moveEarlier: () => Editor.MoveBossReleaseItemEarlier(item),
            moveLater: () => Editor.MoveBossReleaseItemLater(item),
            delete: () => Editor.RemoveBossReleaseItem(item));
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

        ShowReleaseItemMenu(
            element,
            canMoveEarlier: Editor.CanMoveBossComboItemEarlier(item),
            canMoveLater: Editor.CanMoveBossComboItemLater(item),
            moveEarlier: () => Editor.MoveBossComboItemEarlier(item),
            moveLater: () => Editor.MoveBossComboItemLater(item),
            delete: () => Editor.RemoveBossComboItem(item));
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

    private static void ShowReleaseItemMenu(
        FrameworkElement element,
        bool canMoveEarlier,
        bool canMoveLater,
        Action moveEarlier,
        Action moveLater,
        Action delete)
    {
        var moveEarlierItem = new MenuFlyoutItem
        {
            Text = "前移",
            IsEnabled = canMoveEarlier
        };
        moveEarlierItem.Click += (_, _) => moveEarlier();

        var moveLaterItem = new MenuFlyoutItem
        {
            Text = "后移",
            IsEnabled = canMoveLater
        };
        moveLaterItem.Click += (_, _) => moveLater();

        var deleteItem = new MenuFlyoutItem
        {
            Text = "删除"
        };
        deleteItem.Click += (_, _) => delete();

        var flyout = new MenuFlyout();
        flyout.Items.Add(moveEarlierItem);
        flyout.Items.Add(moveLaterItem);
        flyout.Items.Add(new MenuFlyoutSeparator());
        flyout.Items.Add(deleteItem);
        flyout.ShowAt(element);
    }
}
