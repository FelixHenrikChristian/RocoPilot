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

    protected override void OnApplyTemplate()
    {
        base.OnApplyTemplate();

        // WinUI replaces the default button's Style with AccentButtonStyle.
        // Local values keep both buttons consistent when that state is applied.
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

        ReminderContent.Width = Math.Min(400, Math.Max(240, XamlRoot.Size.Width - 96));
        ReminderScrollViewer.MaxHeight = Math.Min(440, Math.Max(120, XamlRoot.Size.Height - 200));
    }
}
