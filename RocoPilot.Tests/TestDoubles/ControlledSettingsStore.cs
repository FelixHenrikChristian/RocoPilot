using System.Collections.Concurrent;
using System.Text.Json;
using RocoPilot.Contracts.Services;

namespace RocoPilot.Tests.TestDoubles;

internal sealed class ControlledSettingsStore : ILocalSettingsService
{
    private readonly ConcurrentDictionary<string, string> _values = new();
    public Func<string, Task>? BeforeRead { get; set; }
    public Func<string, Task>? BeforeSave { get; set; }
    public int ReadCount;
    public int SaveCount;

    public void Seed<T>(string key, T value) => _values[key] = JsonSerializer.Serialize(value);
    public T? ReadSaved<T>(string key) => _values.TryGetValue(key, out var json) ? JsonSerializer.Deserialize<T>(json) : default;

    public async Task<T?> ReadSettingAsync<T>(string key)
    {
        Interlocked.Increment(ref ReadCount);
        if (BeforeRead is { } beforeRead) await beforeRead(key);
        return ReadSaved<T>(key);
    }

    public async Task SaveSettingAsync<T>(string key, T value)
    {
        Interlocked.Increment(ref SaveCount);
        if (BeforeSave is { } beforeSave) await beforeSave(key);
        Seed(key, value);
    }

    public Task ResetAllAsync()
    {
        _values.Clear();
        return Task.CompletedTask;
    }
}

internal sealed class AsyncPause : IDisposable
{
    private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task WaitUntilEnteredAsync() => _entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
    public async Task PauseAsync(string _)
    {
        _entered.TrySetResult();
        await _released.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }
    public void Dispose() => _released.TrySetResult();
}
