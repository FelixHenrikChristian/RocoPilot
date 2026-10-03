namespace RocoPilot.Models.Overlay;

public sealed record InfoOverlaySnapshot(
    string StatusText,
    IReadOnlyList<InfoOverlayCounter> Counters,
    DateTimeOffset UpdatedAt,
    int? MagicPointCount = null,
    int MagicPointMaximum = 6,
    InfoOverlayPendingShinyCapture? PendingShinyCapture = null)
{
    public DateTimeOffset? LatestRecordUpdatedAt => Counters.Count == 0
        ? null
        : Counters.Max(counter => counter.LastCountedAt);

    public static InfoOverlaySnapshot CreateInitial(DateTimeOffset startedAt)
    {
        return new InfoOverlaySnapshot(
            "状态待识别",
            [],
            startedAt);
    }
}

public sealed record InfoOverlayPendingShinyCapture(
    string CreatureName,
    string Season,
    DateTimeOffset DetectedAt);

public sealed record InfoOverlayNotice(
    string Title,
    string Message);
