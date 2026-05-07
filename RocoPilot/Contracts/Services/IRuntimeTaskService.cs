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

    Task<RuntimeTaskStartResult> StartAsync(
        RuntimeTaskStartOptions options,
        CancellationToken cancellationToken = default);

    Task LoadSettingsAsync(CancellationToken cancellationToken = default);

    void SetEncounterStatisticsEnabled(bool isEnabled);

    Task StopAsync();
}
