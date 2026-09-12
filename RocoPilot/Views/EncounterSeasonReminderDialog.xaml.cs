using Microsoft.UI.Xaml.Controls;

using RocoPilot.Models.Encounters;

namespace RocoPilot.Views;

public sealed partial class EncounterSeasonReminderDialog : ContentDialog
{
    public EncounterSeasonReminder Reminder { get; }

    public EncounterSeasonReminderDialog(EncounterSeasonReminder reminder)
    {
        Reminder = reminder;
        InitializeComponent();
    }
}
