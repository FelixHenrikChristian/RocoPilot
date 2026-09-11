namespace RocoPilot.Models.Statistics;

public sealed record StatisticsDocumentMergeResult(
    StatisticsDocument Document,
    IReadOnlyList<string> ConflictingAccountUids);
