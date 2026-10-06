using RocoPilot.Models.Runtime;

namespace RocoPilot.Contracts.Services;

/// <summary>独立任务接管会话所需的最小接口，不暴露战斗配置或 UID 流程。</summary>
public interface IRuntimeSessionControl
{
    bool IsRunning { get; }
    bool IsSuspended { get; }
    RuntimeTaskState? CurrentState { get; }
    Task SuspendAsync(string reason, CancellationToken cancellationToken = default);
    void Resume();
    void UpdateIndependentTaskStatus(string? taskName, IndependentTaskProgress? progress = null);
    void ShowIndependentTaskResult(string title, string description);
}
