namespace RocoPilot.Contracts.Services;

/// <summary>独立任务接管会话所需的最小接口，不暴露战斗配置或 UID 流程。</summary>
public interface IRuntimeSessionControl
{
    bool IsRunning { get; }
    bool IsSuspended { get; }
    void Suspend(string reason);
    void Resume();
}
