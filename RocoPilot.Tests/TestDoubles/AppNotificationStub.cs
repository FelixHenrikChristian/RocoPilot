using RocoPilot.Contracts.Services;

namespace RocoPilot.Tests.TestDoubles;

internal sealed class AppNotificationStub : IAppNotificationService
{
    public bool IsEnabled { get; private set; } = true;
    public bool IsSoundEnabled { get; private set; } = true;
    public string? LastError => null;
    public List<(string Title, string Message, string? ImagePath)> Messages { get; } = [];
    public bool Fail { get; set; }
    public Task InitializeAsync() => Task.CompletedTask;
    public Task SetEnabledAsync(bool enabled) { IsEnabled = enabled; return Task.CompletedTask; }
    public Task SetSoundEnabledAsync(bool enabled) { IsSoundEnabled = enabled; return Task.CompletedTask; }
    public Task<bool> ShowAsync(string title, string message, string? imagePath = null)
    {
        Messages.Add((title, message, imagePath));
        if (Fail) throw new IOException("通知不可用");
        return Task.FromResult(true);
    }
    public void Unregister() { }
}
