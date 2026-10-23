using RocoPilot.Models.Runtime;

namespace RocoPilot.Contracts.Services;

public interface IManualChallengeRunner
{
    Task RunLegendaryAsync(
        RuntimeTaskState state,
        string targetName,
        int runCount,
        bool useLegendaryMark,
        AutoBattleSettings battleSettings,
        Action<IndependentTaskProgress> progress,
        CancellationToken cancellationToken);

    Task RunAsync(
        RuntimeTaskState state,
        FlowerSeedOption target,
        int runCount,
        AutoBattleSettings battleSettings,
        Action<IndependentTaskProgress> progress,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<FlowerSeedOption>> ScanAsync(
        RuntimeTaskState state,
        Action<IndependentTaskProgress> progress,
        CancellationToken cancellationToken);
}
