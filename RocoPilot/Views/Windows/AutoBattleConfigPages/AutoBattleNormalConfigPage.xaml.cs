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

        ShowReleaseItemMenu(
            element,
            canMoveEarlier: Editor.CanMoveNormalReleaseItemEarlier(item),
            canMoveLater: Editor.CanMoveNormalReleaseItemLater(item),
            moveEarlier: () => Editor.MoveNormalReleaseItemEarlier(item),
            moveLater: () => Editor.MoveNormalReleaseItemLater(item),
            delete: () => Editor.RemoveNormalReleaseItem(item));
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
