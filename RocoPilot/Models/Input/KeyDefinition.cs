namespace RocoPilot.Models.Input;

public sealed record KeyDefinition(
    string DisplayName,
    int VirtualKey,
    bool IsExtended = false,
    bool IsModifier = false);
