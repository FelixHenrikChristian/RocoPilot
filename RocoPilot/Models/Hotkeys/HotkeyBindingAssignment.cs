namespace RocoPilot.Models.Hotkeys;

public sealed class HotkeyBindingAssignment
{
    public HotkeyAction Action
    {
        get;
        set;
    }

    public HotkeyBinding? Binding
    {
        get;
        set;
    }

    public HotkeyBindingAssignment Clone()
    {
        return new HotkeyBindingAssignment
        {
            Action = Action,
            Binding = Binding?.Clone()
        };
    }
}
