using Microsoft.Extensions.Options;

using RocoPilot.Contracts.Services;
using RocoPilot.Core.Contracts.Services;
using RocoPilot.Core.Helpers;
using RocoPilot.Helpers;
using RocoPilot.Models;

using Windows.Storage;

namespace RocoPilot.Services;

public class LocalSettingsService : ILocalSettingsService
{
    private const string _defaultApplicationDataFolder = "RocoPilot/ApplicationData";
    private const string _defaultLocalSettingsFile = "LocalSettings.json";

    private readonly IFileService _fileService;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly bool _usePackagedStorage;

    private readonly string _localApplicationData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
    private readonly string _applicationDataFolder;
    private readonly string _localsettingsFile;

    private IDictionary<string, object> _settings;

    private bool _isInitialized;

    public LocalSettingsService(IFileService fileService, IOptions<LocalSettingsOptions> options)
        : this(fileService, options, RuntimeHelper.IsMSIX)
    {
    }

    internal LocalSettingsService(IFileService fileService, IOptions<LocalSettingsOptions> options, bool usePackagedStorage)
    {
        _fileService = fileService;
        _usePackagedStorage = usePackagedStorage;
        _applicationDataFolder = Path.Combine(_localApplicationData, options.Value.ApplicationDataFolder ?? _defaultApplicationDataFolder);
        _localsettingsFile = options.Value.LocalSettingsFile ?? _defaultLocalSettingsFile;

        _settings = new Dictionary<string, object>();
    }

    private async Task InitializeAsync()
    {
        if (!_isInitialized)
        {
            _settings = await Task.Run(() => _fileService.Read<IDictionary<string, object>>(_applicationDataFolder, _localsettingsFile)) ?? new Dictionary<string, object>();

            _isInitialized = true;
        }
    }

    public async Task<T?> ReadSettingAsync<T>(string key)
    {
        await _gate.WaitAsync();
        try
        {
            if (_usePackagedStorage)
            {
                return ApplicationData.Current.LocalSettings.Values.TryGetValue(key, out var value)
                    ? await Json.ToObjectAsync<T>((string)value)
                    : default;
            }

            await InitializeAsync();
            return _settings.TryGetValue(key, out var savedValue)
                ? await Json.ToObjectAsync<T>((string)savedValue)
                : default;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SaveSettingAsync<T>(string key, T value)
    {
        await _gate.WaitAsync();
        try
        {
            var serializedValue = await Json.StringifyAsync(value);
            if (_usePackagedStorage)
            {
                ApplicationData.Current.LocalSettings.Values[key] = serializedValue;
                return;
            }

            await InitializeAsync();
            // 保存成功后才替换缓存，失败的修改不会混入后续写入。
            var nextSettings = new Dictionary<string, object>(_settings) { [key] = serializedValue };
            await Task.Run(() => _fileService.Save(_applicationDataFolder, _localsettingsFile, nextSettings));
            _settings = nextSettings;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task ResetAllAsync()
    {
        await _gate.WaitAsync();
        try
        {
            if (_usePackagedStorage)
            {
                ApplicationData.Current.LocalSettings.Values.Clear();
                return;
            }

            await Task.Run(() => _fileService.Delete(_applicationDataFolder, _localsettingsFile));
            _settings = new Dictionary<string, object>();
            _isInitialized = true;
        }
        finally
        {
            _gate.Release();
        }
    }
}
