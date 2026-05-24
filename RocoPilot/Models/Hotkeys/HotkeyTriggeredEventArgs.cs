namespace RocoPilot.Models.Hotkeys;

public sealed class HotkeyTriggeredEventArgs : EventArgs
{
    public HotkeyTriggeredEventArgs(HotkeyAction action)
    {
        Action = action;
    }

    public HotkeyAction Action
    {
        get;
    }
}
