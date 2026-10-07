using RocoPilot.Models.Runtime;

namespace RocoPilot.Contracts.Services;

public interface IFlowerSeedChallengeRunner
{
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
