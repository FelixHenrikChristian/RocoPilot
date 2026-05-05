namespace RocoPilot.Models.Overlay;

public sealed record InfoOverlayCounter(
    string CreatureName,
    int PollutionCount,
    int ShinyCount,
    DateTimeOffset LastCountedAt);
