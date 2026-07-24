using RocoPilot.Models.Statistics;

namespace RocoPilot.Contracts.Services.Statistics;

public interface IStatisticsUidCoordinatorService
{
    event EventHandler? PendingConfirmationChanged;

    StatisticsUidConfirmationRequest? PendingConfirmation { get; }

    void MarkPendingConfirmationPresented();

    Task<StatisticsUidDetectionResult> RetryDetectionAsync(
        CancellationToken cancellationToken = default);

    Task<string> ConfirmUidAsync(
        string uid,
        CancellationToken cancellationToken = default);
}
