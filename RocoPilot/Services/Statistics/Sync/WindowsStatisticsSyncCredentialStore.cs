using Windows.Security.Credentials;
using RocoPilot.Contracts.Services.Statistics;

namespace RocoPilot.Services.Statistics.Sync;

public sealed class WindowsStatisticsSyncCredentialStore : IStatisticsSyncCredentialStore
{
    private const string CredentialResource = "RocoPilot.StatisticsSync";

    public void Save(string userName, string password)
    {
        if (string.IsNullOrWhiteSpace(userName) || string.IsNullOrWhiteSpace(password))
        {
            return;
        }

        var vault = new PasswordVault();
        foreach (var credential in FindCredentials(vault))
        {
            vault.Remove(credential);
        }

        vault.Add(new PasswordCredential(CredentialResource, userName.Trim(), password));
    }

    public string? Read(string userName)
    {
        if (string.IsNullOrWhiteSpace(userName))
        {
            return null;
        }

        var vault = new PasswordVault();
        var credentials = FindCredentials(vault);
        var credential = credentials.FirstOrDefault(item =>
            string.Equals(item.UserName, userName.Trim(), StringComparison.OrdinalIgnoreCase));
        if (credential is null)
        {
            return null;
        }

        credential.RetrievePassword();
        return credential.Password;
    }

    private static IReadOnlyList<PasswordCredential> FindCredentials(PasswordVault vault)
    {
        try
        {
            return vault.FindAllByResource(CredentialResource);
        }
        catch
        {
            return [];
        }
    }
}
