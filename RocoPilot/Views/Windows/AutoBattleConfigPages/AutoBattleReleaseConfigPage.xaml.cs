using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Windows.Storage.Pickers;
using RocoPilot.Controls;
using RocoPilot.Helpers;

using Serilog;

namespace RocoPilot.Views.Windows.AutoBattleConfigPages;

public sealed partial class AutoBattleReleaseConfigPage : Page
{
    private readonly AutoBattleConfigWindow _owner;

    /// <summary>提权进程中 WinUI 不支持拖放，改用点击卡片选中、再点击目标卡片完成移动。</summary>
    private readonly bool _clickReorderEnabled;

    private AutoBattleReleaseEditorItem? _moveSource;

    internal AutoBattleConfigEditor Editor
    {
        get;
    }

    internal AutoBattleReleaseConfigSet Set { get; }

    internal string BattleTypeName { get; }

    internal string Description { get; }

    /// <summary>普通战斗没有出招队列，不能跳过回合；跳过步骤按钮仅在花种/传说战斗显示。</summary>
    internal Visibility SkipTurnButtonVisibility { get; }

    /// <summary>区块说明：提权时提示拖拽不可用。</summary>
    internal string SectionDescription { get; }

    /// <summary>点击排序操作提示的可见性，仅提权时显示。</summary>
    internal Visibility ClickReorderHintVisibility { get; }

    internal AutoBattleReleaseConfigPage(
        AutoBattleConfigEditor editor,
        AutoBattleConfigWindow owner,
        AutoBattleConfigSection section)
    {
        Editor = editor;
        _owner = owner;
        _clickReorderEnabled = AppElevation.IsElevated;
        SectionDescription = _clickReorderEnabled
            ? "从左到右、逐行循环执行。当前以管理员权限运行，拖拽排序不可用。"
            : "从左到右、逐行循环执行。拖动步骤可调整释放顺序。";
        ClickReorderHintVisibility = _clickReorderEnabled ? Visibility.Visible : Visibility.Collapsed;
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

        if (_clickReorderEnabled)
        {
            // 提权进程里 WinUI 拖放整体失效，关掉原生拖拽，避免出现“禁止”光标却始终无法重排。
            StepsGrid.CanDragItems = false;
            StepsGrid.CanReorderItems = false;
            StepsGrid.AllowDrop = false;
        }
        else
        {
            // 普通权限下保留原生拖拽手感，点击不参与排序。
            StepsGrid.IsItemClickEnabled = false;
        }

        // 切换配置会换掉整个 Items 集合，旧的待移动选中状态必须清掉。
        Set.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(AutoBattleReleaseConfigSet.SelectedSequence))
            {
                ClearMoveSource();
            }
        };
    }

    /// <summary>提权时的替代交互：第一次点击选中待移动步骤，第二次点击目标步骤把源步骤移到目标位置。</summary>
    private void StepsGrid_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (!_clickReorderEnabled || e.ClickedItem is not AutoBattleReleaseEditorItem clicked)
        {
            return;
        }

        var sequence = Set.SelectedSequence;
        if (_moveSource is null || !sequence.Items.Contains(_moveSource))
        {
            SelectMoveSource(clicked);
            return;
        }

        if (ReferenceEquals(_moveSource, clicked))
        {
            ClearMoveSource();
            return;
        }

        var source = _moveSource;
        ClearMoveSource();
        if (AutoBattleReleaseSequenceEditor.TryMove(sequence.Items, source, clicked))
        {
            Debug.WriteLine(
                $"[战斗配置] 点击排序：{BattleTypeName} / {source.DisplayText} 移入第 {clicked.Position} 步");
        }
    }

    private void SelectMoveSource(AutoBattleReleaseEditorItem item)
    {
        ClearMoveSource();
        _moveSource = item;
        item.IsMoveSource = true;
    }

    private void ClearMoveSource()
    {
        if (_moveSource is { } source)
        {
            source.IsMoveSource = false;
            _moveSource = null;
        }
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
        ClearMoveSource();
        Set.SelectedSequence.Items.Clear();
    }

    private void DeleteStep_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: AutoBattleReleaseEditorItem item })
        {
            if (ReferenceEquals(_moveSource, item))
            {
                ClearMoveSource();
            }

            Set.SelectedSequence.Items.Remove(item);
        }
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
        // 编辑窗口里的增删改在保存前不生效，只留调试记录；导入导出涉及文件，记为给用户看的流水。
        Log.ForContext(typeof(AutoBattleReleaseConfigPage)).Debug(
            "战斗配置：新建 {BattleType} / {Name}（共 {Count} 个）", BattleTypeName, item.Name, Set.Items.Count);
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
            TitleGlyph = DialogGlyphs.Rename,
            Content = new Border
            {
                Style = (Style)Application.Current.Resources["DialogCardStyle"],
                Padding = new Thickness(16),
                MinWidth = 320,
                Child = textBox
            },
            PrimaryButtonText = "确定",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = _owner.Content.XamlRoot
        };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary
            && !string.IsNullOrWhiteSpace(textBox.Text))
        {
            selected.Name = textBox.Text.Trim();
            Log.ForContext(typeof(AutoBattleReleaseConfigPage)).Debug(
                "战斗配置：重命名 {BattleType} / {Name}", BattleTypeName, selected.Name);
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
            TitleGlyph = DialogGlyphs.Delete,
            Tone = AppDialogTone.Critical,
            Content = new TextBlock
            {
                Text = $"确定删除{BattleTypeName}的配置「{selected.Name}」？该配置的步骤将一并删除。",
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = 360
            },
            PrimaryButtonText = "删除",
            CloseButtonText = "取消",
            XamlRoot = _owner.Content.XamlRoot
        };
        dialog.UseDestructiveButtons();
        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            Set.RemoveConfig(selected);
            Log.ForContext(typeof(AutoBattleReleaseConfigPage)).Debug(
                "战斗配置：删除 {BattleType} / {Name}（剩余 {Count} 个）", BattleTypeName, selected.Name, Set.Items.Count);
        }
    }

    private async void ImportConfigButton_Click(object sender, RoutedEventArgs e)
    {
        string? path;
        try
        {
            var picker = new FileOpenPicker(_owner.AppWindow.Id)
            {
                Title = "导入战斗配置",
                FileTypeChoices =
                {
                    { "战斗配置", new List<string> { AutoBattleReleaseConfigTransfer.FileExtension } },
                    { "所有文件", new List<string> { "*" } }
                }
            };
            path = (await picker.PickSingleFileAsync())?.Path;
        }
        catch (Exception ex)
        {
            _owner.ShowMessage("打开导入窗口失败", ex.Message);
            return;
        }

        if (path is null)
        {
            return;
        }

        string json;
        try
        {
            json = await File.ReadAllTextAsync(path);
        }
        catch (Exception ex)
        {
            Log.ForContext(typeof(AutoBattleReleaseConfigPage)).Error(ex, "战斗配置：导入时读取文件失败：{Path}", path);
            _owner.ShowMessage("读取文件失败", ex.Message);
            return;
        }

        if (!AutoBattleReleaseConfigTransfer.TryImport(
                json,
                Editor.KeyboardInputService,
                out var config,
                out var error))
        {
            Log.ForContext(typeof(AutoBattleReleaseConfigPage)).Error(
                "战斗配置：导入失败，{Error}（{Path}）", error, path);
            _owner.ShowMessage("导入失败", error);
            return;
        }

        var item = Editor.AddImportedConfig(Set, config);
        Log.ForContext(typeof(AutoBattleReleaseConfigPage)).Information(
            "战斗配置：已导入「{Name}」（{StepCount} 步）到{BattleType}，保存后生效，来源 {Path}",
            item.Name, config.Steps.Count, BattleTypeName, path);
        _owner.ShowMessage(
            "导入成功",
            $"已导入配置「{item.Name}」（{config.Steps.Count} 步）到{BattleTypeName}，保存后生效。",
            InfoBarSeverity.Success);
    }

    private async void ExportConfigButton_Click(object sender, RoutedEventArgs e)
    {
        if (Set.Selected is not { } selected)
        {
            return;
        }

        string? path;
        try
        {
            var picker = new FileSavePicker(_owner.AppWindow.Id)
            {
                Title = "导出战斗配置",
                SuggestedFileName = SanitizeFileName(string.IsNullOrWhiteSpace(selected.Name)
                    ? AutoBattleSettingsRules.CreateDefaultConfigName(null)
                    : selected.Name),
                DefaultFileExtension = AutoBattleReleaseConfigTransfer.FileExtension,
                FileTypeChoices = { { "战斗配置", new List<string> { AutoBattleReleaseConfigTransfer.FileExtension } } }
            };
            path = (await picker.PickSaveFileAsync())?.Path;
        }
        catch (Exception ex)
        {
            _owner.ShowMessage("打开导出窗口失败", ex.Message);
            return;
        }

        if (path is null)
        {
            return;
        }

        try
        {
            await File.WriteAllTextAsync(path, AutoBattleReleaseConfigTransfer.Export(selected));
            Log.ForContext(typeof(AutoBattleReleaseConfigPage)).Information(
                "战斗配置：已导出「{Name}」到 {Path}", selected.Name, path);
            _owner.ShowMessage("导出成功", $"已导出到 {path}", InfoBarSeverity.Success);
        }
        catch (Exception ex)
        {
            Log.ForContext(typeof(AutoBattleReleaseConfigPage)).Error(ex, "战斗配置：导出失败：{Path}", path);
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
