using RocoPilot.Models.Encounters;
using RocoPilot.Models.Statistics;

namespace RocoPilot.Contracts.Services.Statistics;

public interface IStatisticsService
{
    event EventHandler<StatisticsDocumentChangedEventArgs>? DocumentChanged;

    event EventHandler? SelectedAccountChanged;

    StatisticsDocument CurrentDocument { get; }

    Task<StatisticsDocument> LoadAsync();

    Task<StatisticsDocument> ReplaceAsync(StatisticsDocument document);

    Task<StatisticsDocument> AddAccountAsync(string uid);

    Task<StatisticsDocument> DeleteAccountAsync(string uid);

    Task<StatisticsDocument> ClearAsync();

    Task<StatisticsDocument> RecordEncounterAsync(
        EncounterSeasonDefinition season,
        string spiritName,
        DateTimeOffset capturedAt);

    IReadOnlyList<EncounterSpiritRecord> GetSelectedAccountSeasonEncounters(string seasonId);

    void SetSelectedAccountUid(string? uid);
}

public sealed class StatisticsDocumentChangedEventArgs : EventArgs
{
    public StatisticsDocumentChangedEventArgs(StatisticsDocument document)
    {
        Document = document;
    }

    public StatisticsDocument Document { get; }
}
