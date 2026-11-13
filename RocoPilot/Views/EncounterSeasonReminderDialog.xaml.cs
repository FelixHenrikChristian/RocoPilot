using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using RocoPilot.Controls;
using RocoPilot.Models.Encounters;

namespace RocoPilot.Views;

public sealed partial class EncounterSeasonReminderDialog : AppContentDialog
{
    public EncounterSeasonReminder Reminder { get; }

    public string ConfiguredSeasonDisplay => $"当前配置 · {Reminder.SeasonId} 赛季";

    public string EndDateDisplay => $"配置结束日期：{Reminder.EndDate:yyyy-MM-dd}";

    public EncounterSeasonReminderDialog(EncounterSeasonReminder reminder)
    {
        Reminder = reminder;
        InitializeComponent();
        Loaded += (_, _) => UpdateContentBounds();
    }

    private void UpdateContentBounds()
    {
        if (XamlRoot is null) return;

        ReminderContent.Width = Math.Min(400, Math.Max(240, XamlRoot.Size.Width - 96));
        ReminderScrollViewer.MaxHeight = Math.Min(440, Math.Max(120, XamlRoot.Size.Height - 200));
    }
}
