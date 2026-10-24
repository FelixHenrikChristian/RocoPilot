namespace RocoPilot.Contracts.Services;

public interface IAppNotificationService
{
    bool IsEnabled { get; }
    bool IsSoundEnabled { get; }
    string? LastError { get; }
    Task InitializeAsync();
    Task SetEnabledAsync(bool enabled);
    Task SetSoundEnabledAsync(bool enabled);
    Task<bool> ShowAsync(string title, string message, string? imagePath = null);
    void Unregister();
}
