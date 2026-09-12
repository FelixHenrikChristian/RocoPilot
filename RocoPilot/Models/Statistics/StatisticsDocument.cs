using System.Text.Json.Serialization;

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

    public const string CurrentVersion = "1.3";
}

public sealed class AccountStatisticsData
{
    public string Uid { get; set; } = string.Empty;

    public List<SeasonStatisticsData> Seasons { get; set; } = [];

    public List<PendingShinyCaptureRecord> PendingShinyCaptures { get; set; } = [];

    public List<PendingEncounterRecord> PendingEncounters { get; set; } = [];
}

public sealed class SeasonStatisticsData
{
    public string Id { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public string DateRange { get; set; } = string.Empty;

    public string EncounterTypeName { get; set; } = string.Empty;

    public List<EncounterSpiritRecord> Encounters { get; set; } = [];

    public List<ShinySpiritCaptureRecord> ShinyCaptures { get; set; } = [];

    public List<EncounterCountResetRecord> EncounterCountResets { get; set; } = [];
}

public sealed class PendingEncounterRecord
{
    public string Id { get; set; } = string.Empty;
    public string RawText { get; set; } = string.Empty;
    public string Season { get; set; } = string.Empty;
    public DateTimeOffset DetectedAt { get; set; }

    // 名称和赛季可以分别补齐；没有名称时才需要使用原始 OCR 重新匹配。
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Name { get; set; }

    // 处理后保留标记，合并旧的云端记录时不会重新进入待确认队列。
    public DateTimeOffset? HandledAt { get; set; }
}

public sealed class EncounterCountResetRecord
{
    public string Name { get; set; } = string.Empty;
    public DateTimeOffset ResetAt { get; set; }
}

public enum PendingEncounterConfirmationResult
{
    NotFound,
    Counted,
    AwaitingSeason,
    BeforeReset
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
    public string Id { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public string Season { get; set; } = string.Empty;

    public DateTimeOffset CapturedAt { get; set; }

    public int EncounterCountBeforeCapture { get; set; }
}

public sealed class PendingShinyCaptureRecord
{
    public string Id { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public string Season { get; set; } = string.Empty;

    public DateTimeOffset DetectedAt { get; set; }
}
