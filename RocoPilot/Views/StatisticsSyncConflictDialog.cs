using System.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using RocoPilot.Controls;
using RocoPilot.Models.Statistics;

namespace RocoPilot.Views;

internal static class StatisticsSyncConflictDialog
{
    public static async Task<bool?> ShowAsync(XamlRoot root, StatisticsSyncConflictException conflict)
    {
        var content = new StackPanel
        {
            Width = Math.Min(500, Math.Max(240, root.Size.Width - 96)), Spacing = 12
        };
        content.Children.Add(new TextBlock
        {
            Text = "两端都有独立修改。请选择要保留的整个账号版本，另一端独有的内容不会自动相加。取消可先返回统计页面导出备份或修改数据。",
            TextWrapping = TextWrapping.Wrap
        });
        content.Children.Add(new TextBlock { Text = "本地", Style = (Style)Application.Current.Resources["DialogSectionTitleStyle"] });
        content.Children.Add(new TextBlock { Text = Describe(conflict.Local.Account), TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true });
        content.Children.Add(new TextBlock { Text = "云端", Style = (Style)Application.Current.Resources["DialogSectionTitleStyle"] });
        content.Children.Add(new TextBlock { Text = Describe(conflict.Remote.Account), TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true });
        var dialog = new AppContentDialog
        {
            XamlRoot = root, Title = $"账号 {conflict.Local.Uid} 同步冲突",
            PrimaryButtonText = "保留本地", SecondaryButtonText = "采用云端", CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
            Content = new ScrollViewer
            {
                Content = content, MaxHeight = Math.Min(480, Math.Max(120, root.Size.Height - 220)),
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto
            }
        };
        return await dialog.ShowAsync() switch
        {
            ContentDialogResult.Primary => false,
            ContentDialogResult.Secondary => true,
            _ => null
        };
    }

    internal static string Describe(AccountStatisticsData? account)
    {
        if (account is null) return "已删除账号及全部统计记录";
        var text = new StringBuilder();
        foreach (var season in account.Seasons)
        {
            text.AppendLine(season.Name);
            foreach (var item in season.Encounters)
                text.AppendLine($"  奇遇：{item.Name} × {item.Count}");
            foreach (var item in season.ShinyCaptures)
                text.AppendLine($"  异色：{item.Name} · {item.CapturedAt:yyyy-MM-dd HH:mm:ss zzz} · 出异色前 {item.EncounterCountBeforeCapture} 次");
        }
        foreach (var item in account.Temporary?.Encounters ?? [])
            text.AppendLine($"暂存奇遇：{item.Date:yyyy-MM-dd} · {item.Name}（{item.RawText}）× {item.Count}");
        foreach (var item in account.PendingShinyCaptures)
            text.AppendLine($"待确认异色：{item.Name}（{item.RawText}）· {item.DetectedAt:yyyy-MM-dd HH:mm:ss zzz}");
        return text.Length == 0 ? "账号存在，暂无统计记录" : text.ToString().TrimEnd();
    }
}
