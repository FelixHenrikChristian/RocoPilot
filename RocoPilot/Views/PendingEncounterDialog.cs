using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using RocoPilot.Controls;
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
        var detectedAt = new TextBlock
        {
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Foreground = context.Foreground,
            HorizontalAlignment = HorizontalAlignment.Right
        };
        var metadata = new Grid { ColumnSpacing = 12 };
        metadata.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        metadata.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(detectedAt, 1);
        metadata.Children.Add(context);
        metadata.Children.Add(detectedAt);

        var hint = new TextBlock
        {
            Style = (Style)Application.Current.Resources["DialogDescriptionTextStyle"]
        };
        var name = new TextBox { Header = "精灵名", PlaceholderText = "输入正确的精灵名", MaxLength = 32 };
        var content = new StackPanel
        {
            Width = Math.Min(440, Math.Max(240, xamlRoot.Size.Width - 96)),
            Spacing = 12,
            Children =
            {
                selector,
                new Border
                {
                    Style = (Style)Application.Current.Resources["DialogCardStyle"],
                    Child = new StackPanel
                    {
                        Spacing = 12,
                        Children =
                        {
                            SpiritNamePreview.CreateField(name),
                            new Border
                            {
                                Height = 1,
                                Background = GetBrush("DividerStrokeColorDefaultBrush")
                            },
                            metadata,
                            new StackPanel
                            {
                                Spacing = 4,
                                Children =
                                {
                                    new TextBlock { Text = "原始识别文字", FontSize = 12, Foreground = context.Foreground },
                                    rawText
                                }
                            }
                        }
                    }
                },
                hint
            }
        };
        var dialog = new AppContentDialog
        {
            XamlRoot = xamlRoot,
            Title = "暂存奇遇",
            Content = new ScrollViewer
            {
                Content = content,
                MaxHeight = Math.Min(560, Math.Max(120, xamlRoot.Size.Height - 200)),
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                HorizontalScrollMode = ScrollMode.Disabled,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                VerticalScrollMode = ScrollMode.Auto
            },
            PrimaryButtonText = "确认计入",
            SecondaryButtonText = "忽略此条",
            CloseButtonText = "关闭",
            IsPrimaryButtonEnabled = false,
            DefaultButton = ContentDialogButton.Primary
        };
        selector.SelectionChanged += (_, _) =>
        {
            if (selector.SelectedItem is not PendingEncounterItem item) return;
            rawText.Text = item.RawTextDisplay;
            context.Text = $"UID {item.AccountUid} · {item.SeasonDisplay}";
            detectedAt.Text = $"{item.DetectedAt:yyyy-MM-dd HH:mm:ss}";
            name.Text = item.Name ?? item.RawText;
            hint.Text = item.IsSeasonPending
                ? "记录已保存。赛季资料更新后，将按发生日期自动归档。"
                : "修正精灵名后确认计入，也可通过同步图鉴补齐名称。";
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
