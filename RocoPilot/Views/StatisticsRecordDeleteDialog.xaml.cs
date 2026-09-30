using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;

using RocoPilot.Controls;
using RocoPilot.ViewModels;

namespace RocoPilot.Views;

public sealed partial class StatisticsRecordDeleteDialog : AppContentDialog
{
    public string SpiritName { get; }
    public string Subtitle { get; }
    public BitmapImage? Avatar { get; }
    public string CountLabel { get; }
    public string CountValue { get; }
    public string SecondLabel { get; }
    public string SecondValue { get; }
    public string? LatestValue { get; }
    public string DeleteScope { get; }
    public string RetainedInfo { get; }
    public Visibility AvatarVisibility => Avatar is null ? Visibility.Collapsed : Visibility.Visible;
    public Visibility AvatarFallbackVisibility => Avatar is null ? Visibility.Visible : Visibility.Collapsed;
    public Visibility LatestVisibility => LatestValue is null ? Visibility.Collapsed : Visibility.Visible;

    public static StatisticsRecordDeleteDialog ForEncounter(string seasonDisplay, SpiritCountItem item) => new(
        "删除奇遇条目", item.Name, $"{seasonDisplay} · 奇遇统计", item.Avatar,
        "奇遇次数", $"{item.Count} 次", "保底进度", $"{Math.Clamp(item.Count / Math.Max(1, item.PityThreshold), 0, 1):P0}",
        item.LastCapturedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"),
        "将删除该精灵在当前赛季的奇遇计数。",
        "已记录的异色精灵会保留，后续保底计数会受影响。");

    public static StatisticsRecordDeleteDialog ForShiny(ShinyCaptureDetailItem item) => new(
        "删除异色记录", item.Name, $"{item.SeasonDisplay} · {item.PositionDisplay}", item.Avatar,
        "异色前奇遇次数", item.EncounterCountDisplay, "获取时间",
        $"{item.CapturedDateDisplay} {item.CapturedTimeDisplay}", null,
        "仅删除当前这一条异色记录。",
        "其他异色记录会保留，奇遇计数不会回滚。");

    private StatisticsRecordDeleteDialog(
        string title, string spiritName, string subtitle, BitmapImage? avatar,
        string countLabel, string countValue, string secondLabel, string secondValue,
        string? latestValue, string deleteScope, string retainedInfo)
    {
        SpiritName = spiritName;
        Subtitle = subtitle;
        Avatar = avatar;
        CountLabel = countLabel;
        CountValue = countValue;
        SecondLabel = secondLabel;
        SecondValue = secondValue;
        LatestValue = latestValue;
        DeleteScope = deleteScope;
        RetainedInfo = retainedInfo;
        InitializeComponent();
        Title = title;
        Loaded += (_, _) => UpdateContentBounds();
    }

    protected override void OnApplyTemplate()
    {
        base.OnApplyTemplate();

        // WinUI replaces the default button's Style; local values preserve geometry.
        foreach (var name in new[] { "PrimaryButton", "CloseButton" })
        {
            if (GetTemplateChild(name) is not Button button) continue;
            button.FontSize = 13;
            button.MinHeight = 34;
            button.MinWidth = 96;
            button.Padding = new Thickness(14, 6, 14, 6);
            button.CornerRadius = new CornerRadius(8);
        }
    }

    private void UpdateContentBounds()
    {
        if (XamlRoot is null) return;
        RecordContent.Width = Math.Min(380, Math.Max(240, XamlRoot.Size.Width - 96));
        RecordScrollViewer.MaxHeight = Math.Min(440, Math.Max(120, XamlRoot.Size.Height - 200));
    }
}
