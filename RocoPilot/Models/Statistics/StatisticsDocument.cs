using System.Text.Json.Serialization;

namespace RocoPilot.Models.Statistics;

public sealed class StatisticsDocument
{
    public StatisticsDocumentInfo Info { get; set; } = new();

    public List<AccountStatisticsData> Accounts { get; set; } = [];

    // 删除后只保留账号和版本，防止离线设备恢复旧数据；不保存业务记录。
    public Dictionary<string, StatisticsAccountVersion> DeletedAccounts { get; set; } = [];
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

    public const string CurrentVersion = "3.0";
}

public sealed class AccountStatisticsData
{
    public string Uid { get; set; } = string.Empty;

    public List<SeasonStatisticsData> Seasons { get; set; } = [];

    public List<PendingShinyCaptureRecord> PendingShinyCaptures { get; set; } = [];

    // 只读取旧文档，迁移后始终为空。
    public List<PendingEncounterRecord> PendingEncounters { get; set; } = [];

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public TemporaryStatistics? Temporary { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public StatisticsAccountVersion? SyncVersion { get; set; }
}

// 每个账号只保留每台设备的最新版本，不保存逐次奇遇或已处理业务记录。
public sealed class StatisticsAccountVersion
{
    public Dictionary<string, long> Devices { get; set; } = [];
    public string? LegacyFingerprint { get; set; }
}

public sealed class TemporaryStatistics
{
    public List<TemporaryEncounterCount> Encounters { get; set; } = [];
}

public sealed class TemporaryEncounterCount
{
    public string Id { get; set; } = string.Empty;
    public DateOnly Date { get; set; }
    public string RawText { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public int Count { get; set; }
    // 仅当前累计桶的最后一次写入，用于同一战斗补读和写入重试；桶删除时一并删除。
    public string LastEventId { get; set; } = string.Empty;
}

public sealed record TemporaryStatisticsTransfer(
    string AccountUid, string Snapshot, IReadOnlyList<string> EncounterIds,
    IReadOnlyList<string> CaptureIds, string Name, string SeasonId, int Count);

public sealed class SeasonStatisticsData
{
    public string Id { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public string DateRange { get; set; } = string.Empty;

    public string EncounterTypeName { get; set; } = string.Empty;

    public List<EncounterSpiritRecord> Encounters { get; set; } = [];

    public List<ShinySpiritCaptureRecord> ShinyCaptures { get; set; } = [];

    // 旧暂存补计的重置标记，仅用于迁移读取，不再写入新标记。
    public List<EncounterCountResetRecord> EncounterCountResets { get; set; } = [];
}

// 仅用于读取 1.x 文档，载入后转换成按日累计并清空。
public sealed class PendingEncounterRecord
{
    public string Id { get; set; } = string.Empty;
    public string RawText { get; set; } = string.Empty;
    public string Season { get; set; } = string.Empty;
    public DateTimeOffset DetectedAt { get; set; }

    // 旧格式字段，不再用于运行时记录。
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Name { get; set; }

    // 旧格式处理标记，迁移后不再保留对应业务记录。
    public DateTimeOffset? HandledAt { get; set; }
}

public sealed class EncounterCountResetRecord
{
    public string Name { get; set; } = string.Empty;
    public DateTimeOffset ResetAt { get; set; }
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

    public string RawText { get; set; } = string.Empty;

    public string Season { get; set; } = string.Empty;

    public DateTimeOffset DetectedAt { get; set; }

    // 仅兼容旧格式；新流程确认或忽略后物理删除记录。
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateTimeOffset? HandledAt { get; set; }
}
