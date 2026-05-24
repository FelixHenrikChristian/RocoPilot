namespace RocoPilot.Models.Input;

public static class KeyCatalog
{
    private static readonly IReadOnlyDictionary<string, KeyDefinition> DefinitionsByNameCore =
        BuildDefinitionsByName();

    private static readonly IReadOnlyDictionary<int, KeyDefinition> DefinitionsByVirtualKeyCore =
        DefinitionsByNameCore
            .Values
            .GroupBy(definition => definition.VirtualKey)
            .ToDictionary(group => group.Key, group => group.First());

    public static IReadOnlyDictionary<string, KeyDefinition> DefinitionsByName => DefinitionsByNameCore;

    public static bool TryGetDefinitionByName(string keyName, out KeyDefinition keyDefinition)
    {
        return DefinitionsByNameCore.TryGetValue(NormalizeKeyName(keyName), out keyDefinition!);
    }

    public static bool TryGetDefinitionByVirtualKey(int virtualKey, out KeyDefinition keyDefinition)
    {
        return DefinitionsByVirtualKeyCore.TryGetValue(virtualKey, out keyDefinition!);
    }

    public static bool IsModifierVirtualKey(int virtualKey)
    {
        return TryGetDefinitionByVirtualKey(virtualKey, out var keyDefinition)
            && keyDefinition.IsModifier;
    }

    public static string GetDisplayName(int virtualKey)
    {
        return TryGetDefinitionByVirtualKey(virtualKey, out var keyDefinition)
            ? keyDefinition.DisplayName
            : $"VK 0x{virtualKey:X2}";
    }

    public static string NormalizeKeyName(string keyName)
    {
        return keyName.Trim().Replace(" ", string.Empty, StringComparison.Ordinal).ToUpperInvariant();
    }

    private static IReadOnlyDictionary<string, KeyDefinition> BuildDefinitionsByName()
    {
        var definitions = new Dictionary<string, KeyDefinition>(StringComparer.OrdinalIgnoreCase);

        void Add(string displayName, int virtualKey, bool isExtended = false, bool isModifier = false, params string[] aliases)
        {
            var key = new KeyDefinition(displayName, virtualKey, isExtended, isModifier);
            definitions[NormalizeKeyName(displayName)] = key;
            foreach (var alias in aliases)
            {
                definitions[NormalizeKeyName(alias)] = key;
            }
        }

        for (var key = 'A'; key <= 'Z'; key++)
        {
            Add(key.ToString(), key);
        }

        for (var number = 0; number <= 9; number++)
        {
            Add(number.ToString(), 0x30 + number, aliases: [$"D{number}", $"Digit{number}"]);
        }

        for (var number = 0; number <= 9; number++)
        {
            Add($"Numpad{number}", 0x60 + number, aliases: [$"Num{number}"]);
        }

        for (var number = 1; number <= 24; number++)
        {
            Add($"F{number}", 0x70 + number - 1);
        }

        Add("Backspace", 0x08, aliases: ["Back"]);
        Add("Tab", 0x09);
        Add("Enter", 0x0D, aliases: ["Return"]);
        Add("Shift", 0x10, isModifier: true);
        Add("Ctrl", 0x11, isModifier: true, aliases: ["Control"]);
        Add("Alt", 0x12, isModifier: true, aliases: ["Menu"]);
        Add("Pause", 0x13);
        Add("CapsLock", 0x14, aliases: ["Caps"]);
        Add("Escape", 0x1B, aliases: ["Esc"]);
        Add("Space", 0x20, aliases: ["Spacebar"]);
        Add("PageUp", 0x21, isExtended: true, aliases: ["PgUp"]);
        Add("PageDown", 0x22, isExtended: true, aliases: ["PgDn"]);
        Add("End", 0x23, isExtended: true);
        Add("Home", 0x24, isExtended: true);
        Add("Left", 0x25, isExtended: true, aliases: ["ArrowLeft"]);
        Add("Up", 0x26, isExtended: true, aliases: ["ArrowUp"]);
        Add("Right", 0x27, isExtended: true, aliases: ["ArrowRight"]);
        Add("Down", 0x28, isExtended: true, aliases: ["ArrowDown"]);
        Add("Insert", 0x2D, isExtended: true, aliases: ["Ins"]);
        Add("Delete", 0x2E, isExtended: true, aliases: ["Del"]);
        Add("LeftWin", 0x5B, isExtended: true, isModifier: true, aliases: ["LWin", "Win"]);
        Add("RightWin", 0x5C, isExtended: true, isModifier: true, aliases: ["RWin"]);
        Add("Multiply", 0x6A, aliases: ["NumpadMultiply"]);
        Add("Add", 0x6B, aliases: ["NumpadAdd"]);
        Add("Subtract", 0x6D, aliases: ["NumpadSubtract"]);
        Add("Decimal", 0x6E, aliases: ["NumpadDecimal"]);
        Add("Divide", 0x6F, isExtended: true, aliases: ["NumpadDivide"]);

        return definitions;
    }
}
