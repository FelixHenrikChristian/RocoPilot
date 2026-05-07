namespace RocoPilot.Models.Input;

public sealed record KeyStroke(
    IReadOnlyList<KeyDefinition> Modifiers,
    KeyDefinition Key)
{
    public string DisplayText => Modifiers.Count == 0
        ? Key.DisplayName
        : $"{string.Join("+", Modifiers.Select(modifier => modifier.DisplayName))}+{Key.DisplayName}";
}
