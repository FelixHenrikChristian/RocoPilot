namespace RocoPilot.Contracts.Services.Statistics;

public interface IStatisticsSyncCredentialStore
{
    string? Read(string userName);
    void Save(string userName, string password);
}
