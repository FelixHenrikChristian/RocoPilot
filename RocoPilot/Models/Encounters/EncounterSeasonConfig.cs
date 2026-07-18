namespace RocoPilot.Models.Encounters;

public sealed class EncounterSeasonConfig
{
    public string CurrentSeasonId { get; set; } = "S1";

    public double SpiritNameMatchThreshold { get; set; } = 0.55;

    public List<EncounterSeasonDefinition> Seasons { get; set; } = [];
}

public sealed class EncounterSeasonDefinition
{
    public string Id { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public string DateRange { get; set; } = string.Empty;

    public string EncounterTypeName { get; set; } = string.Empty;
}
