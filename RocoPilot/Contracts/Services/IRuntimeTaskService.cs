using RocoPilot.Models.Runtime;

namespace RocoPilot.Contracts.Services;

public interface IRuntimeTaskService
{
    event EventHandler? SettingsChanged;

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

    bool InfoOverlayEncounterPanelEnabled
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

    void SetInfoOverlayEncounterPanelEnabled(bool isEnabled);

    void SetAutoBattleSettings(AutoBattleSettings settings);

    Task StopAsync();
}
