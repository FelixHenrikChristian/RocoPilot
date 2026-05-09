namespace RocoPilot.Models.Overlay;

public sealed record InfoOverlaySnapshot(
    string StatusText,
    IReadOnlyList<InfoOverlayCounter> Counters,
    DateTimeOffset UpdatedAt,
    int? MagicPointCount = null,
    int MagicPointMaximum = 6,
    InfoOverlayPendingShinyCapture? PendingShinyCapture = null)
{
    public static InfoOverlaySnapshot CreateInitial(DateTimeOffset startedAt)
    {
        return new InfoOverlaySnapshot(
            "状态待识别",
            [
                new InfoOverlayCounter("待识别目标", 0, 0, startedAt)
            ],
            DateTimeOffset.Now);
    }
}

public sealed record InfoOverlayPendingShinyCapture(
    string CreatureName,
    string Season,
    DateTimeOffset DetectedAt);
