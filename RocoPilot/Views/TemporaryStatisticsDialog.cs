using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Automation;
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
        var table = new Grid { ColumnSpacing = 8, RowSpacing = 8, MinWidth = 460, Margin = new Thickness(0, 0, 12, 0) };
        table.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(32) });
        table.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        table.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(96) });
        table.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(144) });
        table.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var headers = new[] { "选择", "精灵名", "次数", "归入赛季" };
        for (var column = 0; column < headers.Length; column++)
        {
            var header = new TextBlock { Text = headers[column] };
            Grid.SetColumn(header, column);
            table.Children.Add(header);
        }
        content.Children.Add(new ScrollViewer
        {
            Content = table, MaxHeight = Math.Max(120, Math.Min(480, root.Size.Height - 320)),
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollMode = ScrollMode.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto
        });
        var selectAll = new CheckBox { Content = "全选", IsChecked = false };
        var selectionSummary = new TextBlock { Text = "已选 0 项", VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Right };
        var footer = new Grid();
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(selectionSummary, 1);
        footer.Children.Add(selectAll);
        footer.Children.Add(selectionSummary);
        content.Children.Add(footer);
        var rows = new List<(TemporaryStatisticsItem Item, CheckBox Selected, TextBox Name, NumberBox Count, ComboBox Season)>();
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
            updatingSelection = true;
            selectAll.IsChecked = selectedCount == 0 ? false : selectedCount == rows.Count ? true : null;
            updatingSelection = false;
            dialog.IsPrimaryButtonEnabled = selectedCount > 0
                && rows.Where(x => x.Selected.IsChecked == true).All(x => x.Name.Text.Trim().Length > 0
                && x.Season.SelectedItem is EncounterSeasonDefinition && !double.IsNaN(x.Count.Value)
                && x.Count.Value >= 0 && x.Count.Value == Math.Truncate(x.Count.Value));
        }
        void SelectAll(object sender, RoutedEventArgs args)
        {
            if (updatingSelection) return;
            updatingSelection = true;
            foreach (var row in rows) row.Selected.IsChecked = selectAll.IsChecked == true;
            updatingSelection = false;
            Validate();
        }
        selectAll.Checked += SelectAll;
        selectAll.Unchecked += SelectAll;
        foreach (var group in items.GroupBy(x => x.IsShiny))
        {
            var groupTitle = new TextBlock { Text = group.Key ? "异色 · 已确认捕获，归档不再清零奇遇" : "奇遇",
                Margin = new Thickness(0, 8, 0, 0) };
            Grid.SetRow(groupTitle, table.RowDefinitions.Count);
            Grid.SetColumnSpan(groupTitle, 4);
            table.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            table.Children.Add(groupTitle);
            foreach (var item in group)
            {
                var selected = new CheckBox { IsChecked = false, MinWidth = 0, VerticalAlignment = VerticalAlignment.Center,
                    HorizontalAlignment = HorizontalAlignment.Center };
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
                selected.Checked += (_, _) => Validate(); selected.Unchecked += (_, _) => Validate();
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
