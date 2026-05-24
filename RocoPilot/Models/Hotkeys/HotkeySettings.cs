namespace RocoPilot.Models.Hotkeys;

public sealed class HotkeySettings
{
    public List<HotkeyBindingAssignment> Bindings
    {
        get;
        set;
    } = [];

    public HotkeyBinding? GetBinding(HotkeyAction action)
    {
        return Bindings.FirstOrDefault(binding => binding.Action == action)?.Binding?.Clone();
    }

    public HotkeySettings Clone()
    {
        return new HotkeySettings
        {
            Bindings = Bindings.Select(binding => binding.Clone()).ToList()
        };
    }

    public static HotkeySettings CreateDefault()
    {
        return new HotkeySettings();
    }
}
