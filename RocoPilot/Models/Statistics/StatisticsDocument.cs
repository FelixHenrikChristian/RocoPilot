namespace RocoPilot.Models.Statistics;

public sealed class StatisticsDocument
{
    public StatisticsDocumentInfo Info { get; set; } = new();

    public List<AccountStatisticsData> Accounts { get; set; } = [];
}

public sealed class StatisticsDocumentInfo
{
    public string Format { get; set; } = StatisticsDocumentFormats.RocoPilotStatistics;

    public string Version { get; set; } = StatisticsDocumentFormats.CurrentVersion;

    public string ExportApp { get; set; } = "RocoPilot";

    public DateTimeOffset ExportedAt { get; set; } = DateTimeOffset.Now;
}

public static class StatisticsDocumentFormats
{
    public const string RocoPilotStatistics = "RocoPilot.Statistics";

    public const string CurrentVersion = "1.0";
}

public sealed class AccountStatisticsData
{
    public string Uid { get; set; } = string.Empty;

    public List<SeasonStatisticsData> Seasons { get; set; } = [];
}

public sealed class SeasonStatisticsData
{
    public string Id { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public string DateRange { get; set; } = string.Empty;

    public string EncounterTypeName { get; set; } = string.Empty;

    public List<EncounterSpiritRecord> Encounters { get; set; } = [];

    public List<ShinySpiritCaptureRecord> ShinyCaptures { get; set; } = [];
}

public sealed class EncounterSpiritRecord
{
    public string Name { get; set; } = string.Empty;

    public int Count { get; set; }

    public string Season { get; set; } = string.Empty;

    public DateTimeOffset LastCapturedAt { get; set; }
}

public sealed class ShinySpiritCaptureRecord
{
    public string Name { get; set; } = string.Empty;

    public string Season { get; set; } = string.Empty;

    public DateTimeOffset CapturedAt { get; set; }
}
