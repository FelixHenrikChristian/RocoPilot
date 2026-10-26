using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using RocoPilot.Controls;
using RocoPilot.Models.Encounters;
using RocoPilot.Models.Statistics;
using RocoPilot.ViewModels;

namespace RocoPilot.Views;

internal static class TemporaryStatisticsDialog
{
    public static async Task<IReadOnlyList<TemporaryStatisticsTransfer>?> ShowAsync(
        XamlRoot? root, IReadOnlyList<TemporaryStatisticsItem> items, IReadOnlyList<EncounterSeasonDefinition> seasons)
    {
        if (root is null || items.Count == 0) return null;
        var content = new StackPanel { Spacing = 12, Width = Math.Min(560, Math.Max(260, root.Size.Width - 96)) };
        content.Children.Add(new TextBlock
        {
            Text = "勾选要计入的数据，可修改精灵名、奇遇次数和赛季。未勾选的继续保留。",
            TextWrapping = TextWrapping.Wrap
        });
        var cards = new StackPanel { Spacing = 12, MinWidth = 460, Margin = new Thickness(0, 0, 12, 0) };
        content.Children.Add(new ScrollViewer
        {
            Content = cards, MaxHeight = Math.Max(120, Math.Min(560, root.Size.Height - 280)),
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollMode = ScrollMode.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto
        });
        var selectAll = new Button { Content = "全选", MinWidth = 0, Padding = new Thickness(12, 4, 12, 4) };
        var selectionSummary = new TextBlock { Text = "已选 0 项", VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Right };
        var footer = new Grid();
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(selectionSummary, 1);
        footer.Children.Add(selectAll);
        footer.Children.Add(selectionSummary);
        content.Children.Add(footer);
        var rows = new List<(TemporaryStatisticsItem Item, ToggleButton Selected, TextBox Name, NumberBox Count, ComboBox Season)>();
        var dialog = new AppContentDialog
        {
            XamlRoot = root, Title = "确认新赛季统计", PrimaryButtonText = "确认计入",
            CloseButtonText = "稍后处理", IsPrimaryButtonEnabled = false,
            Content = content
        };
        var updatingSelection = false;
        void Validate()
        {
            if (updatingSelection) return;
            var selectedCount = rows.Count(x => x.Selected.IsChecked == true);
            selectionSummary.Text = $"已选 {selectedCount} 项";
            selectAll.Content = selectedCount == rows.Count ? "取消全选" : "全选";
            dialog.IsPrimaryButtonEnabled = selectedCount > 0
                && rows.Where(x => x.Selected.IsChecked == true).All(x => x.Name.Text.Trim().Length > 0
                && x.Season.SelectedItem is EncounterSeasonDefinition && !double.IsNaN(x.Count.Value)
                && x.Count.Value >= 0 && x.Count.Value == Math.Truncate(x.Count.Value));
        }
        void SelectAll(object sender, RoutedEventArgs args)
        {
            if (updatingSelection) return;
            var select = rows.Any(row => row.Selected.IsChecked != true);
            updatingSelection = true;
            foreach (var row in rows) row.Selected.IsChecked = select;
            updatingSelection = false;
            Validate();
        }
        selectAll.Click += SelectAll;
        foreach (var group in items.GroupBy(x => x.IsShiny))
        {
            var body = new StackPanel { Spacing = 12 };
            cards.Children.Add(new Border
            {
                Style = (Style)Application.Current.Resources["DialogCardStyle"],
                Padding = new Thickness(14), Child = body
            });
            var heading = new Grid { ColumnSpacing = 10 };
            heading.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            heading.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            heading.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            heading.Children.Add(new Border
            {
                Width = 36, Height = 36, CornerRadius = new CornerRadius(8),
                Background = new SolidColorBrush(group.Key
                    ? global::Windows.UI.Color.FromArgb(36, 139, 92, 246) : global::Windows.UI.Color.FromArgb(36, 15, 118, 110)),
                Child = new FontIcon
                {
                    Glyph = group.Key ? "\uE794" : "\uE728", FontSize = 18,
                    FontFamily = (FontFamily)Application.Current.Resources["SymbolThemeFontFamily"],
                    Foreground = (Brush)Application.Current.Resources["AccentTextFillColorPrimaryBrush"]
                }
            });
            var title = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
            title.Children.Add(new TextBlock { Text = group.Key ? "异色" : "奇遇", FontSize = 16,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
            title.Children.Add(new TextBlock
            {
                Text = group.Key ? "已确认捕获 · 本次仅归档" : "按精灵累计 · 可调整计入次数",
                Style = (Style)Application.Current.Resources["DialogDescriptionTextStyle"]
            });
            Grid.SetColumn(title, 1);
            heading.Children.Add(title);
            var groupCount = new Border
            {
                Padding = new Thickness(10, 4, 10, 4), CornerRadius = new CornerRadius(12),
                VerticalAlignment = VerticalAlignment.Center,
                Background = (Brush)Application.Current.Resources["ControlFillColorDefaultBrush"],
                Child = new TextBlock { Text = $"{group.Count()} 项", FontSize = 12 }
            };
            Grid.SetColumn(groupCount, 2);
            heading.Children.Add(groupCount);
            body.Children.Add(heading);
            body.Children.Add(new Border { Height = 1,
                Background = (Brush)Application.Current.Resources["DividerStrokeColorDefaultBrush"] });
            var table = new Grid { ColumnSpacing = 8, RowSpacing = 8 };
            table.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(28) });
            table.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            table.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(88) });
            table.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(128) });
            table.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var headers = new[] { "", "精灵名", group.Key ? "只数" : "次数", "归入赛季" };
            for (var column = 1; column < headers.Length; column++)
            {
                var header = new TextBlock { Text = headers[column], FontSize = 12,
                    Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"] };
                Grid.SetColumn(header, column);
                table.Children.Add(header);
            }
            body.Children.Add(table);
            foreach (var item in group)
            {
                var check = new FontIcon { Glyph = "\uE73E", FontSize = 12, Opacity = 0 };
                var selected = new ToggleButton { IsChecked = false, MinWidth = 0, MinHeight = 0,
                    Width = 28, Height = 28, Padding = new Thickness(0), Content = check,
                    VerticalAlignment = VerticalAlignment.Center, CornerRadius = new CornerRadius(8) };
                var name = new TextBox { Text = item.Name == "未识别精灵" ? "" : item.Name, MaxLength = 32,
                    MinWidth = 0, PlaceholderText = "输入精灵名" };
                var count = new NumberBox { Value = item.Count, MinWidth = 0,
                    Minimum = 0, Maximum = int.MaxValue, IsEnabled = !item.IsShiny, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact };
                var season = new ComboBox { ItemsSource = seasons, DisplayMemberPath = nameof(EncounterSeasonDefinition.Name), MinWidth = 0,
                    SelectedItem = seasons.FirstOrDefault(x => x.Id == item.SuggestedSeasonId), PlaceholderText = "选择赛季",
                    HorizontalAlignment = HorizontalAlignment.Stretch };
                AutomationProperties.SetName(selected, $"计入{item.Kind}：{item.Name}");
                AutomationProperties.SetName(name, "精灵名");
                AutomationProperties.SetName(count, item.IsShiny ? "已捕获只数" : "计入次数");
                AutomationProperties.SetName(season, "归入赛季");
                var rowIndex = table.RowDefinitions.Count;
                table.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                FrameworkElement[] fields = [selected, name, count, season];
                for (var column = 0; column < fields.Length; column++)
                {
                    Grid.SetRow(fields[column], rowIndex);
                    Grid.SetColumn(fields[column], column);
                    table.Children.Add(fields[column]);
                }
                rows.Add((item, selected, name, count, season));
                selected.Checked += (_, _) => { check.Opacity = 1; Validate(); };
                selected.Unchecked += (_, _) => { check.Opacity = 0; Validate(); };
                name.TextChanged += (_, _) => Validate(); count.ValueChanged += (_, _) => Validate();
                season.SelectionChanged += (_, _) => Validate();
            }
        }
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return null;
        return rows.Where(x => x.Selected.IsChecked == true).Select(x => new TemporaryStatisticsTransfer(
            x.Item.AccountUid, x.Item.Snapshot, x.Item.EncounterIds, x.Item.CaptureIds,
            x.Name.Text.Trim(), ((EncounterSeasonDefinition)x.Season.SelectedItem).Id, (int)x.Count.Value)).ToList();
    }
}
