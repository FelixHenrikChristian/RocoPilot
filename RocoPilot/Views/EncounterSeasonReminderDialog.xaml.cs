using RocoPilot.Controls;
using RocoPilot.Models.Encounters;

namespace RocoPilot.Views;

public sealed partial class EncounterSeasonReminderDialog : AppContentDialog
{
    public EncounterSeasonReminder Reminder { get; }

    public EncounterSeasonReminderDialog(EncounterSeasonReminder reminder)
    {
        Reminder = reminder;
        InitializeComponent();
    }
}
