using System.Diagnostics;

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using RocoPilot.Controls;
using RocoPilot.Helpers;

using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;

namespace RocoPilot.Views.Windows.AutoBattleConfigPages;

public sealed partial class AutoBattleReleaseConfigPage : Page
{
    private readonly AutoBattleConfigWindow _owner;

    internal AutoBattleConfigEditor Editor
    {
        get;
    }

    internal AutoBattleReleaseConfigSet Set { get; }

    internal string BattleTypeName { get; }

    internal string Description { get; }

    /// <summary>普通战斗没有出招队列，不能跳过回合；跳过步骤按钮仅在花种/传说战斗显示。</summary>
    internal Visibility SkipTurnButtonVisibility { get; }

    internal AutoBattleReleaseConfigPage(
        AutoBattleConfigEditor editor,
        AutoBattleConfigWindow owner,
        AutoBattleConfigSection section)
    {
        Editor = editor;
        _owner = owner;
        (Set, BattleTypeName) = section switch
        {
            AutoBattleConfigSection.FlowerSeed => (editor.FlowerSeedConfigs, "花种战斗"),
            AutoBattleConfigSection.Legendary => (editor.LegendaryConfigs, "传说精灵战斗"),
            _ => (editor.NormalConfigs, "普通战斗")
        };
        Description = section is AutoBattleConfigSection.FlowerSeed or AutoBattleConfigSection.Legendary
            ? "轮到自己操作时按顺序释放技能，每场挑战从第一步开始，可插入公共执行序列。"
            : "按顺序循环执行每个技能回合的动作。";
        SkipTurnButtonVisibility = section is AutoBattleConfigSection.FlowerSeed or AutoBattleConfigSection.Legendary
            ? Visibility.Visible
            : Visibility.Collapsed;
        InitializeComponent();
    }

    private void AppendSkillButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string skillKey })
        {
            Set.SelectedSequence.AppendSkill(skillKey);
        }
    }

    private void ClearReleaseSequenceButton_Click(object sender, RoutedEventArgs e)
    {
        Set.SelectedSequence.Items.Clear();
    }

    private void DeleteStep_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: AutoBattleReleaseEditorItem item })
        {
            Set.SelectedSequence.Items.Remove(item);
        }
    }

    // GridView 鼠标拖拽在 WinUI3 不触发（已知问题），改用 ListView + 拖放事件手动交换位置。
    private AutoBattleReleaseEditorItem? _dragStepItem;

    private void StepsGrid_DragItemsStarting(object sender, DragItemsStartingEventArgs e)
    {
        _dragStepItem = e.Items.FirstOrDefault() as AutoBattleReleaseEditorItem;
        Debug.WriteLine($"[战斗配置] 拖拽开始：{_dragStepItem?.StepTitle ?? "<null>"}");
    }

    private void StepsGrid_DragOver(object sender, DragEventArgs e)
    {
        if (_dragStepItem is not null)
        {
            e.AcceptedOperation = DataPackageOperation.Move;
            e.Handled = true;
        }
    }

    private void StepsGrid_Drop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        if (_dragStepItem is not { } item)
        {
            return;
        }

        _dragStepItem = null;
        var sequence = Set.SelectedSequence;
        var oldIndex = sequence.Items.IndexOf(item);
        var targetIndex = FindStepDropIndex(e.GetPosition(StepsGrid));
        StepsGrid.SelectedItem = null;
        if (oldIndex < 0 || targetIndex < 0 || targetIndex == oldIndex)
        {
            return;
        }

        sequence.Items.Move(oldIndex, targetIndex);
        Debug.WriteLine($"[战斗配置] 拖拽排序：{item.StepTitle} #{oldIndex + 1} -> #{targetIndex + 1}");
    }

    /// <summary>返回指针所在步骤卡片的索引；落在空白区域返回 -1。</summary>
    private int FindStepDropIndex(Point position)
    {
        for (var index = 0; index < StepsGrid.Items.Count; index++)
        {
            if (StepsGrid.ContainerFromIndex(index) is not ListViewItem container)
            {
                continue;
            }

            var bounds = container
                .TransformToVisual(StepsGrid)
                .TransformBounds(new Rect(0, 0, container.ActualWidth, container.ActualHeight));
            if (bounds.Contains(position))
            {
                return index;
            }
        }

        return -1;
    }

    private void InsertPresetButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: AutoBattlePresetEditorItem preset })
        {
            return;
        }

        if (!Editor.TryInsertSharedPreset(preset, Set.SelectedSequence, out var error))
        {
            _owner.ShowMessage(error.Title, error.Message);
        }
    }

    private void NewConfigButton_Click(object sender, RoutedEventArgs e)
    {
        var item = Set.AddConfig();
        Debug.WriteLine($"[战斗配置] 新建配置：{BattleTypeName} / {item.Name}（共 {Set.Items.Count} 个）");
    }

    private async void RenameConfigButton_Click(object sender, RoutedEventArgs e)
    {
        if (Set.Selected is not { } selected)
        {
            return;
        }

        var textBox = new TextBox
        {
            Header = "配置名称",
            Text = selected.Name,
            PlaceholderText = "配置x"
        };
        var dialog = new AppContentDialog
        {
            Title = $"重命名{BattleTypeName}配置",
            Content = textBox,
            PrimaryButtonText = "确定",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = _owner.Content.XamlRoot
        };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary
            && !string.IsNullOrWhiteSpace(textBox.Text))
        {
            selected.Name = textBox.Text.Trim();
            Debug.WriteLine($"[战斗配置] 重命名配置：{BattleTypeName} -> {selected.Name}");
        }
    }

    private async void DeleteConfigButton_Click(object sender, RoutedEventArgs e)
    {
        if (Set.Selected is not { } selected || !Set.CanRemove)
        {
            return;
        }

        var dialog = new AppContentDialog
        {
            Title = "删除配置",
            Content = new TextBlock
            {
                Text = $"确定删除{BattleTypeName}的配置「{selected.Name}」？该配置的步骤将一并删除。",
                TextWrapping = TextWrapping.Wrap
            },
            PrimaryButtonText = "删除",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = _owner.Content.XamlRoot
        };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            Set.RemoveConfig(selected);
            Debug.WriteLine($"[战斗配置] 删除配置：{BattleTypeName} / {selected.Name}（剩余 {Set.Items.Count} 个）");
        }
    }

    private void ImportConfigButton_Click(object sender, RoutedEventArgs e)
    {
        // 应用提权运行时 WindowsAppSDK 的 FileOpenPicker 会抛 COMException，改用 Win32 对话框。
        var path = Win32FileDialog.PickOpenFile(
            WinRT.Interop.WindowNative.GetWindowHandle(_owner),
            "战斗配置",
            AutoBattleReleaseConfigTransfer.FileExtension);
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        string json;
        try
        {
            json = File.ReadAllText(path);
        }
        catch (Exception ex)
        {
            _owner.ShowMessage("读取文件失败", ex.Message);
            return;
        }

        if (!AutoBattleReleaseConfigTransfer.TryImport(
                json,
                Editor.KeyboardInputService,
                out var config,
                out var error))
        {
            _owner.ShowMessage("导入失败", error);
            return;
        }

        var item = Editor.AddImportedConfig(Set, config);
        Debug.WriteLine(
            $"[战斗配置] 导入配置：{BattleTypeName} / {item.Name}，{config.Steps.Count} 步，来源 {path}");
        _owner.ShowMessage(
            "导入成功",
            $"已导入配置「{item.Name}」（{config.Steps.Count} 步）到{BattleTypeName}，保存后生效。",
            InfoBarSeverity.Success);
    }

    private void ExportConfigButton_Click(object sender, RoutedEventArgs e)
    {
        if (Set.Selected is not { } selected)
        {
            return;
        }

        var path = Win32FileDialog.PickSaveFile(
            WinRT.Interop.WindowNative.GetWindowHandle(_owner),
            "战斗配置",
            AutoBattleReleaseConfigTransfer.FileExtension,
            SanitizeFileName(string.IsNullOrWhiteSpace(selected.Name)
                ? AutoBattleSettingsRules.CreateDefaultConfigName(null)
                : selected.Name));
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        try
        {
            File.WriteAllText(path, AutoBattleReleaseConfigTransfer.Export(selected));
            Debug.WriteLine($"[战斗配置] 导出配置：{BattleTypeName} / {selected.Name} -> {path}");
            _owner.ShowMessage("导出成功", $"已导出到 {path}", InfoBarSeverity.Success);
        }
        catch (Exception ex)
        {
            _owner.ShowMessage("导出失败", ex.Message);
        }
    }

    private static string SanitizeFileName(string name)
    {
        return string.Join("_", name.Split(
            [.. Path.GetInvalidFileNameChars()],
            StringSplitOptions.RemoveEmptyEntries)).Trim();
    }
}
