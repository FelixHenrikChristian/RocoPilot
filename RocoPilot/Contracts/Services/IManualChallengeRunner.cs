using RocoPilot.Models.Runtime;

namespace RocoPilot.Contracts.Services;

public interface IManualChallengeRunner
{
    Task<IReadOnlyList<string>> ScanLegendaryAsync(
        RuntimeTaskState state,
        Action<IndependentTaskProgress> progress,
        CancellationToken cancellationToken);

    Task RunLegendaryAsync(
        RuntimeTaskState state,
        string targetName,
        int runCount,
        int difficulty,
        bool useLegendaryMark,
        AutoBattleSettings battleSettings,
        Action<IndependentTaskProgress> progress,
        CancellationToken cancellationToken);

    Task RunAsync(
        bool atTargetLocation,
        bool preferCondensedMark,

    Task<IReadOnlyList<FlowerSeedOption>> ScanAsync(
        RuntimeTaskState state,
        Action<IndependentTaskProgress> progress,
        CancellationToken cancellationToken);
}
