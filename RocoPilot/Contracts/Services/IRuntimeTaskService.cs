using RocoPilot.Models.Runtime;

namespace RocoPilot.Contracts.Services;

public interface IRuntimeTaskService
{
    event EventHandler? SettingsChanged;

    bool IsRunning
    {
        get;
    }

    bool IsSuspended
    {
        get;
    }

    RuntimeTaskState? CurrentState
    {
        get;
    }

    bool EncounterStatisticsEnabled
    {
        get;
    }

    AutoBattleSettings AutoBattleSettings
    {
        get;
    }

    RuntimeRecognitionSettings RuntimeRecognitionSettings
    {
        get;
    }

    Task<RuntimeTaskStartResult> StartAsync(
        RuntimeTaskStartOptions options,
        CancellationToken cancellationToken = default);

    Task LoadSettingsAsync(CancellationToken cancellationToken = default);

    void SetEncounterStatisticsEnabled(bool isEnabled);

    void SetRecognitionOverlayEnabled(bool isEnabled);

    void SetInfoOverlayEnabled(bool isEnabled);

    void SetInfoOverlayLocked(bool isLocked);

    void SetAutoBattleSettings(AutoBattleSettings settings);

    void SetRuntimeRecognitionSettings(RuntimeRecognitionSettings settings);

    Task StopAsync();

    /// <summary>
    /// 挂起实时任务循环：暂停截图、识别与按键，但保持任务处于运行状态。
    /// 供独立任务等临时接管游戏窗口的场景使用，结束后调用 <see cref="Resume"/> 恢复。
    /// </summary>
    void Suspend(string reason);

    void Resume();
}
