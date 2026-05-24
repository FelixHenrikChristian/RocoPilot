namespace RocoPilot.ViewModels;

public sealed class HotkeyGroupViewModel
{
    public HotkeyGroupViewModel(
        string name,
        string glyph,
        IReadOnlyList<HotkeyBindingItemViewModel> items)
    {
        Name = name;
        Glyph = glyph;
        Items = items;
    }

    public string Name
    {
        get;
    }

    public string Glyph
    {
        get;
    }

    public IReadOnlyList<HotkeyBindingItemViewModel> Items
    {
        get;
    }
}
