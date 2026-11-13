using System.Diagnostics;

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Windows.Storage.Pickers;
using RocoPilot.Controls;

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
            Debug.WriteLine($"[战斗配置] 删除配置：{BattleTypeName} / {selected.Name}（剩余 {Set.Items.Count} 个）");
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
