using RocoPilot.Models.Encounters;
using RocoPilot.Models.Statistics;

namespace RocoPilot.Contracts.Services.Statistics;

public interface IStatisticsService
{
    event EventHandler<StatisticsDocumentChangedEventArgs>? DocumentChanged;

    event EventHandler? SelectedAccountChanged;

    StatisticsDocument CurrentDocument { get; }

    string? SelectedAccountUid { get; }

    string? ActiveAccountUid { get; }

    bool IsActiveAccountSelectionRequired { get; }

    Task<StatisticsDocument> LoadAsync();

    Task<StatisticsDocument> ReplaceAsync(StatisticsDocument document);

    Task<StatisticsDocument> AddAccountAsync(string uid);

    Task<StatisticsDocument> DeleteAccountAsync(string uid);

    Task<StatisticsDocument> ClearAsync();

    Task<StatisticsDocument> RecordEncounterAsync(
        EncounterSeasonDefinition season,
        string spiritName,
        DateTimeOffset capturedAt);

    Task<StatisticsDocument> UpsertEncounterAsync(
        string seasonId,
        string spiritName,
        int count,
        DateTimeOffset countedAt);

    Task<StatisticsDocument> EditEncounterAsync(
        string seasonId,
        string originalName,
        string nextName,
        int nextCount,
        DateTimeOffset editedAt);

    Task<StatisticsDocument> DeleteEncounterAsync(
        string seasonId,
        string spiritName);

    Task<StatisticsDocument> AddShinyCapturesAsync(
        string seasonId,
        string spiritName,
        int count,
        DateTimeOffset capturedAt,
        bool resetEncounterCount = false,
        int? encounterCountBeforeCapture = null);

    Task<StatisticsDocument> DeleteShinyCapturesAsync(
        string? seasonId,
        string spiritName);

    Task<StatisticsDocument> EditShinyCaptureAsync(
        string captureId,
        string nextName,
        int encounterCountBeforeCapture,
        DateTimeOffset capturedAt);

    Task<StatisticsDocument> DeleteShinyCaptureAsync(
        string captureId);

    Task<StatisticsDocument> AddPendingShinyCaptureAsync(
        EncounterSeasonDefinition season,
        string spiritName,
        DateTimeOffset detectedAt);

    Task<StatisticsDocument> ConfirmPendingShinyCaptureAsync(
        string pendingCaptureId,
        string spiritName,
        int encounterCount,
        DateTimeOffset confirmedAt);

    Task<StatisticsDocument> DiscardPendingShinyCaptureAsync(
        string pendingCaptureId);

    IReadOnlyList<EncounterSpiritRecord> GetActiveAccountSeasonEncounters(string seasonId);

    IReadOnlyList<PendingShinyCaptureRecord> GetSelectedAccountPendingShinyCaptures();

    void SetSelectedAccountUid(string? uid);

    void SetActiveAccountUid(string uid);

    void RequireActiveAccountSelection();
}

public sealed class StatisticsDocumentChangedEventArgs : EventArgs
{
    public StatisticsDocumentChangedEventArgs(StatisticsDocument document)
    {
        Document = document;
    }

    public StatisticsDocument Document { get; }
}
