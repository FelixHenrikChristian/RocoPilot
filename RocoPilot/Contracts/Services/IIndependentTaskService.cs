using RocoPilot.Models.Runtime;

namespace RocoPilot.Contracts.Services;

public interface IIndependentTaskService
{
    event EventHandler? StateChanged;

    bool IsRunning
    {
        get;
    }

    IndependentTaskKind? RunningTaskKind
    {
        get;
    }

    IndependentTaskSettings Settings
    {
        get;
    }

    Task LoadSettingsAsync(CancellationToken cancellationToken = default);

    void SetSettings(IndependentTaskSettings settings);

    Task<IndependentTaskStartResult> StartAsync(
        IndependentTaskKind kind,
        CancellationToken cancellationToken = default);

    Task StopAsync();
}
