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
            Text = "两端都有独立修改。请选择要保留的整个账号版本，另一端独有的内容不会自动相加。",
            FontSize = 13, LineHeight = 20, TextWrapping = TextWrapping.Wrap
        });
        content.Children.Add(CreateSide(DialogGlyphs.Computer, "本地", Describe(conflict.Local.Account)));
        content.Children.Add(CreateSide(DialogGlyphs.Cloud, "云端", Describe(conflict.Remote.Account)));
        var hintIcon = new FontIcon { Glyph = DialogGlyphs.Info, Style = Resource<Style>("DialogRowIconStyle") };
        var hintText = new TextBlock
        {
            Text = "取消可先返回统计页面导出备份或修改数据。",
            Style = Resource<Style>("DialogCaptionTextStyle")
        };
        var hint = new Grid { ColumnSpacing = 10 };
        hint.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        hint.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(hintText, 1);
        hint.Children.Add(hintIcon);
        hint.Children.Add(hintText);
        content.Children.Add(new Border { Style = Resource<Style>("ConfigHintBarStyle"), Child = hint });
        var dialog = new AppContentDialog
        {
            XamlRoot = root, Title = $"账号 {conflict.Local.Uid} 同步冲突",
            TitleGlyph = DialogGlyphs.Sync, Tone = AppDialogTone.Caution,
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

    private static Border CreateSide(string glyph, string title, string description)
    {
        var header = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        header.Children.Add(new FontIcon
        {
            Glyph = glyph,
            Style = Resource<Style>("DialogRowIconStyle"),
            Foreground = Resource<Microsoft.UI.Xaml.Media.Brush>("AccentTextFillColorPrimaryBrush")
        });
        header.Children.Add(new TextBlock
        {
            Text = title,
            Style = Resource<Style>("DialogSectionTitleStyle"),
            VerticalAlignment = VerticalAlignment.Center
        });
        var body = new StackPanel { Spacing = 10 };
        body.Children.Add(header);
        body.Children.Add(new Border
        {
            Height = 1,
            Background = Resource<Microsoft.UI.Xaml.Media.Brush>("DividerStrokeColorDefaultBrush")
        });
        body.Children.Add(new TextBlock
        {
            Text = description,
            FontSize = 12,
            LineHeight = 18,
            TextWrapping = TextWrapping.Wrap,
            IsTextSelectionEnabled = true
        });
        return new Border { Style = Resource<Style>("DialogCardStyle"), Padding = new Thickness(16), Child = body };
    }

    private static T Resource<T>(string key) => (T)Application.Current.Resources[key];

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
