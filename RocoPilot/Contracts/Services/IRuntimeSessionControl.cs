using RocoPilot.Models.Runtime;

namespace RocoPilot.Contracts.Services;

/// <summary>独立任务接管会话并读取当前共享战斗配置，不暴露 UID 流程。</summary>
public interface IRuntimeSessionControl
{
    bool IsRunning { get; }
    bool IsSuspended { get; }
    RuntimeTaskState? CurrentState { get; }
    AutoBattleSettings AutoBattleSettings { get; }
    Task SuspendAsync(string reason, CancellationToken cancellationToken = default);
    void Resume();
    void UpdateIndependentTaskStatus(string? taskName, IndependentTaskProgress? progress = null);
    void ShowIndependentTaskResult(string title, string description);
}
