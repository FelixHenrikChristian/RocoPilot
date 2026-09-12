using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using RocoPilot.ViewModels;

namespace RocoPilot.Views;

internal static class PendingEncounterDialog
{
    public static async Task<PendingEncounterInput?> ShowAsync(
        XamlRoot? xamlRoot, IReadOnlyList<PendingEncounterItem> items)
    {
        if (xamlRoot is null || items.Count == 0) return null;

        var selector = new ComboBox
        {
            Header = $"暂存记录（{items.Count} 条）",
            ItemsSource = items,
            DisplayMemberPath = nameof(PendingEncounterItem.DisplayName),
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        var rawText = new TextBlock { TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true };
        var context = new TextBlock
        {
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Foreground = GetBrush("TextFillColorSecondaryBrush")
        };
        var name = new TextBox { Header = "精灵名", PlaceholderText = "输入正确的精灵名", MaxLength = 32 };
        var content = new StackPanel
        {
            Width = 440,
            Spacing = 16,
            Children =
            {
                new TextBlock
                {
                    Text = "这些奇遇已保存。赛季配置更新后会按发生日期自动归档；未识别的名称可通过同步图鉴补齐，也可以手动填写。",
                    TextWrapping = TextWrapping.Wrap,
                    Foreground = GetBrush("TextFillColorSecondaryBrush")
                },
                selector,
                new Border
                {
                    Padding = new Thickness(14),
                    CornerRadius = new CornerRadius(8),
                    Background = GetBrush("CardBackgroundFillColorDefaultBrush"),
                    BorderBrush = GetBrush("CardStrokeColorDefaultBrush"),
                    BorderThickness = new Thickness(1),
                    Child = new StackPanel
                    {
                        Spacing = 8,
                        Children =
                        {
                            new TextBlock { Text = "精灵名 / 原始识别文字", FontSize = 12, Foreground = context.Foreground },
                            rawText,
                            context
                        }
                    }
                },
                name
            }
        };
        var dialog = new ContentDialog
        {
            XamlRoot = xamlRoot,
            Title = "暂存奇遇",
            Content = new ScrollViewer { Content = content, VerticalScrollBarVisibility = ScrollBarVisibility.Auto },
            PrimaryButtonText = "确认计入",
            SecondaryButtonText = "忽略此条",
            CloseButtonText = "关闭",
            IsPrimaryButtonEnabled = false,
            DefaultButton = ContentDialogButton.Primary
        };
        selector.SelectionChanged += (_, _) =>
        {
            if (selector.SelectedItem is not PendingEncounterItem item) return;
            rawText.Text = item.NameDisplay;
            context.Text = item.ContextDisplay;
            name.Text = item.Name ?? item.RawText;
            dialog.PrimaryButtonText = item.IsSeasonPending ? "保存名称" : "确认计入";
        };
        name.TextChanged += (_, _) => dialog.IsPrimaryButtonEnabled = !string.IsNullOrWhiteSpace(name.Text);
        selector.SelectedIndex = 0;

        var result = await dialog.ShowAsync();
        return result == ContentDialogResult.None || selector.SelectedItem is not PendingEncounterItem selected
            ? null
            : new PendingEncounterInput(selected, name.Text, result == ContentDialogResult.Secondary);
    }

    private static Brush? GetBrush(string key) =>
        Application.Current.Resources.TryGetValue(key, out var resource) ? resource as Brush : null;
}

internal sealed record PendingEncounterInput(PendingEncounterItem Item, string Name, bool Discard);
