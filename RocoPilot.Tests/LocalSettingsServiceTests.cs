using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json;
using RocoPilot.Core.Contracts.Services;
using RocoPilot.Models;
using RocoPilot.Services;
using RocoPilot.Tests.TestDoubles;

namespace RocoPilot.Tests;

[TestClass]
public sealed class LocalSettingsServiceTests
{
    [TestMethod]
    public async Task ConcurrentSavesInitializeOnceAndKeepEveryKey()
    {
        var files = new MemoryFiles();
        var service = CreateService(files);
        await Task.WhenAll(Enumerable.Range(0, 40).Select(i => service.SaveSettingAsync($"key-{i}", i)));

        Assert.AreEqual(1, files.ReadCount);
        Assert.AreEqual(40, files.Read<IDictionary<string, object>>("", "").Count);
        var reloaded = CreateService(files);
        for (var i = 0; i < 40; i++) Assert.AreEqual(i, await reloaded.ReadSettingAsync<int>($"key-{i}"));
    }

    [TestMethod]
    public async Task FailedSaveDoesNotLeakIntoCacheOrLaterWrites()
    {
        var files = new MemoryFiles();
        var service = CreateService(files);
        await service.SaveSettingAsync("count", 1);
        files.FailSave = true;
        await Assert.ThrowsExactlyAsync<IOException>(() => service.SaveSettingAsync("count", 2));
        Assert.AreEqual(1, await service.ReadSettingAsync<int>("count"));

        files.FailSave = false;
        await service.SaveSettingAsync("other", true);
        Assert.AreEqual(1, await CreateService(files).ReadSettingAsync<int>("count"));
    }

    [TestMethod]
    public async Task ResetWaitsForPendingSaveAndDoesNotResurrectOldValues()
    {
        var files = new MemoryFiles();
        var service = CreateService(files);
        using var pause = new AsyncPause();
        files.BeforeSave = () => pause.PauseAsync("").GetAwaiter().GetResult();
        var saving = service.SaveSettingAsync("old", "value");
        await pause.WaitUntilEnteredAsync();
        var resetting = service.ResetAllAsync();
        Assert.IsFalse(resetting.IsCompleted);
        pause.Dispose();
        await Task.WhenAll(saving, resetting);
        Assert.IsNull(await service.ReadSettingAsync<string>("old"));
        Assert.IsNull(await CreateService(files).ReadSettingAsync<string>("old"));
    }

    [TestMethod]
    public async Task FailedResetKeepsTheLastSavedValues()
    {
        var files = new MemoryFiles();
        var service = CreateService(files);
        await service.SaveSettingAsync("old", 7);
        files.FailDelete = true;
        await Assert.ThrowsExactlyAsync<IOException>(() => service.ResetAllAsync());
        Assert.AreEqual(7, await service.ReadSettingAsync<int>("old"));
    }

    private static LocalSettingsService CreateService(MemoryFiles files) =>
        new(files, Options.Create(new LocalSettingsOptions()), usePackagedStorage: false);

    private sealed class MemoryFiles : IFileService
    {
        private string? _json;
        public int ReadCount;
        public bool FailSave;
        public bool FailDelete;
        public Action? BeforeSave;

        public T Read<T>(string folderPath, string fileName)
        {
            Interlocked.Increment(ref ReadCount);
            return _json is null ? default! : JsonConvert.DeserializeObject<T>(_json)!;
        }
        public void Save<T>(string folderPath, string fileName, T content)
        {
            BeforeSave?.Invoke();
            if (FailSave) throw new IOException("save failed");
            _json = JsonConvert.SerializeObject(content);
        }
        public void Delete(string folderPath, string fileName)
        {
            if (FailDelete) throw new IOException("delete failed");
            _json = null;
        }
    }
}
