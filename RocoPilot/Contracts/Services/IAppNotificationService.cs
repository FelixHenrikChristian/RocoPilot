namespace RocoPilot.Contracts.Services;

public interface IAppNotificationService
{
    bool IsEnabled { get; }
    bool IsSoundEnabled { get; }
    string? LastError { get; }
    Task InitializeAsync();
    Task SetEnabledAsync(bool enabled);
    Task SetSoundEnabledAsync(bool enabled);
    /// <param name="openGame">点击通知后回到游戏窗口；否则打开 RocoPilot。</param>
    Task<bool> ShowAsync(string title, string message, string? imagePath = null, bool openGame = false);
    void Unregister();
}
