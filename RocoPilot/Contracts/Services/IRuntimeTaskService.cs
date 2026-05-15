using RocoPilot.Models.Runtime;
using RocoPilot.Models.Spirits;

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

    SpiritEvolutionRecordMode EncounterStatisticsEvolutionRecordMode
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

    void SetEncounterStatisticsEvolutionRecordMode(SpiritEvolutionRecordMode mode);

    void SetAutoBattleSettings(AutoBattleSettings settings);

    Task StopAsync();
}
