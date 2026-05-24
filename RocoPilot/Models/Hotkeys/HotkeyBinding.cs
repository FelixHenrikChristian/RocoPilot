using RocoPilot.Models.Input;

namespace RocoPilot.Models.Hotkeys;

public sealed class HotkeyBinding
{
    public List<int> Modifiers
    {
        get;
        set;
    } = [];

    public int Key
    {
        get;
        set;
    }

    public string DisplayText
    {
        get
        {
            var parts = Modifiers
                .OrderBy(GetModifierSortOrder)
                .ThenBy(modifier => modifier)
                .Select(KeyCatalog.GetDisplayName)
                .Append(KeyCatalog.GetDisplayName(Key));
            return string.Join("+", parts);
        }
    }

    public string GestureId
    {
        get
        {
            var parts = Modifiers
                .Distinct()
                .OrderBy(GetModifierSortOrder)
                .ThenBy(modifier => modifier)
                .Append(Key);
            return string.Join("+", parts);
        }
    }

    public HotkeyBinding Clone()
    {
        return new HotkeyBinding
        {
            Modifiers = Modifiers.ToList(),
            Key = Key
        };
    }

    public static HotkeyBinding Create(IEnumerable<int> modifiers, int key)
    {
        return new HotkeyBinding
        {
            Modifiers = modifiers
                .Distinct()
                .OrderBy(GetModifierSortOrder)
                .ThenBy(modifier => modifier)
                .ToList(),
            Key = key
        };
    }

    private static int GetModifierSortOrder(int virtualKey)
    {
        return virtualKey switch
        {
            0x11 => 0,
            0x12 => 1,
            0x10 => 2,
            0x5B => 3,
            0x5C => 4,
            _ => 10
        };
    }
}
