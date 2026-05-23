using RocoPilot.Models.Runtime;

namespace RocoPilot.Contracts.Services;

public interface IRuntimeTaskService
{
    bool IsRunning
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

    Task<RuntimeTaskStartResult> StartAsync(
        RuntimeTaskStartOptions options,
        CancellationToken cancellationToken = default);

    Task LoadSettingsAsync(CancellationToken cancellationToken = default);

    void SetEncounterStatisticsEnabled(bool isEnabled);

    void SetRecognitionOverlayEnabled(bool isEnabled);

    void SetInfoOverlayEnabled(bool isEnabled);

    void SetInfoOverlayLocked(bool isLocked);

    void SetAutoBattleSettings(AutoBattleSettings settings);

    Task StopAsync();
}
