using RocoPilot.Models.TextRecognition;

namespace RocoPilot.Services.TextRecognition;

internal static class RuntimeOcrRecognitionMode
{
    private static readonly HashSet<string> SingleLineRegionIds = new(StringComparer.OrdinalIgnoreCase)
    {
        "battle-enemy-name",
        "battle-boss-name",
        "shouling_name",
        "battle-tip-shiny",
        "battle-tip-message",
        "battle-tip",
        "battle-tip-encounter-s3",
        "battle-tip-boss-combo",
        "shouling_tip"
    };

    public static TextRecognitionLayout ResolveLayout(string regionId)
    {
        return SingleLineRegionIds.Contains(regionId)
            ? TextRecognitionLayout.SingleLine
            : TextRecognitionLayout.Full;
    }
}
