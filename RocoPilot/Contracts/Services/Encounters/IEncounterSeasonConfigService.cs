using RocoPilot.Models.Encounters;

namespace RocoPilot.Contracts.Services.Encounters;

public interface IEncounterSeasonConfigService
{
    EncounterSeasonConfig Load();

    EncounterSeasonDefinition? GetCurrentSeason();
}
