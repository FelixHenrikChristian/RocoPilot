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
        bool atTargetLocation,
        AutoBattleSettings battleSettings,
        Action<IndependentTaskProgress> progress,
        CancellationToken cancellationToken);

    Task RunAsync(
        RuntimeTaskState state,
        FlowerSeedOption target,
        int runCount,
        bool atTargetLocation,
        bool preferCondensedMark,
        AutoBattleSettings battleSettings,
        Action<IndependentTaskProgress> progress,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<FlowerSeedOption>> ScanAsync(
        RuntimeTaskState state,
        Action<IndependentTaskProgress> progress,
        CancellationToken cancellationToken);
}
