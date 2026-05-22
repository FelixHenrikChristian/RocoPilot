namespace RocoPilot.Models.Encounters;

public sealed class EncounterSeasonConfig
{
    public string CurrentSeasonId { get; set; } = "S1";

    public List<EncounterSeasonDefinition> Seasons { get; set; } = [];
}

public static class EncounterDetectionModes
{
    public const string TipText = "TipText";

    public const string EnemyNameTransition = "EnemyNameTransition";
}

public sealed class EncounterSeasonDefinition
{
    public string Id { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public string DateRange { get; set; } = string.Empty;

    public string EncounterTypeName { get; set; } = string.Empty;

    public string DetectionMode { get; set; } = EncounterDetectionModes.TipText;

    public string TipText { get; set; } = string.Empty;

    public double MatchThreshold { get; set; } = 0.78;

    public string PlaceholderName { get; set; } = string.Empty;

    public double PlaceholderMatchThreshold { get; set; } = 0.78;

    public double SpiritNameMatchThreshold { get; set; } = 0.55;
}
