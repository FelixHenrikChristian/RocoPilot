using System.Text.Json;
using System.Text.Json.Serialization;

using Microsoft.Extensions.Logging;

using RocoPilot.Contracts.Services.Recognition;
using RocoPilot.Models.Recognition;

namespace RocoPilot.Services.Recognition;

public sealed class RecognitionRegionConfigService : IRecognitionRegionConfigService
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        Converters =
        {
            new JsonStringEnumConverter()
        },
        WriteIndented = true
    };

    private readonly ILogger<RecognitionRegionConfigService> _logger;
    private readonly string _configDirectory;

    public RecognitionRegionConfigService(ILogger<RecognitionRegionConfigService> logger)
    {
        _logger = logger;
        _configDirectory = Path.Combine(AppContext.BaseDirectory, "Configuration", "RecognitionRegions");
    }

    public IReadOnlyList<string> ListConfigPaths()
    {
        if (!Directory.Exists(_configDirectory))
        {
            return [];
        }

        return Directory
            .EnumerateFiles(_configDirectory, "*.json", SearchOption.TopDirectoryOnly)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public RecognitionRegionConfig LoadForResolution(int width, int height)
    {
        var path = GetConfigPath(width, height);
        if (File.Exists(path))
        {
            return LoadFromPath(path, width, height);
        }

        var fallbackPath = FindBestFallbackConfigPath(width, height);
        if (string.IsNullOrWhiteSpace(fallbackPath))
        {
            _logger.LogWarning(
                "No recognition region config found for {Width}x{Height}: {ConfigPath}",
                width,
                height,
                path);
            return RecognitionRegionConfig.Empty(width, height, path);
        }

        _logger.LogInformation(
            "No exact recognition region config found for {Width}x{Height}. Using fallback config: {ConfigPath}",
            width,
            height,
            fallbackPath);

        return LoadFromPath(fallbackPath, width, height);
    }

    public RecognitionRegionConfig LoadFromPath(string path)
    {
        return LoadFromPath(path, 0, 0);
    }

    private RecognitionRegionConfig LoadFromPath(string path, int expectedWidth, int expectedHeight)
    {
        try
        {
            var (fallbackWidth, fallbackHeight) = GetResolutionFromPath(path);
            var width = fallbackWidth > 0 ? fallbackWidth : expectedWidth;
            var height = fallbackHeight > 0 ? fallbackHeight : expectedHeight;
            var json = File.ReadAllText(path);
            var config = JsonSerializer.Deserialize<RecognitionRegionConfig>(json, SerializerOptions)
                ?? RecognitionRegionConfig.Empty(width, height, path);

            NormalizeConfig(config, width, height, path, expectedWidth, expectedHeight);
            _logger.LogDebug(
                "Loaded recognition region config {ConfigPath}. Enabled region count: {RegionCount}",
                path,
                config.Regions.Count(region => region.Enabled));

            return config;
        }
        catch (JsonException ex)
        {
            _logger.LogError(ex, "Recognition region config JSON is invalid: {ConfigPath}", path);
            return RecognitionRegionConfig.Empty(expectedWidth, expectedHeight, path);
        }
        catch (IOException ex)
        {
            _logger.LogError(ex, "Failed to read recognition region config: {ConfigPath}", path);
            return RecognitionRegionConfig.Empty(expectedWidth, expectedHeight, path);
        }
    }

    public void Save(RecognitionRegionConfig config)
    {
        var path = string.IsNullOrWhiteSpace(config.SourcePath)
            ? GetConfigPath(config.ResolutionWidth, config.ResolutionHeight)
            : config.SourcePath;

        if (string.IsNullOrWhiteSpace(path))
        {
            throw new InvalidOperationException("Recognition region config is missing a save path.");
        }

        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        NormalizeConfig(config, config.ResolutionWidth, config.ResolutionHeight, path);
        config.LoadedFromFile = true;

        var json = JsonSerializer.Serialize(config, SerializerOptions);
        File.WriteAllText(path, json);

        _logger.LogInformation(
            "Saved recognition region config {ConfigPath}. Enabled region count: {RegionCount}",
            path,
            config.Regions.Count(region => region.Enabled));
    }

    public string GetConfigPath(int width, int height)
    {
        return Path.Combine(_configDirectory, $"{width}x{height}.json");
    }

    private void NormalizeConfig(
        RecognitionRegionConfig config,
        int width,
        int height,
        string path,
        int expectedWidth = 0,
        int expectedHeight = 0)
    {
        if (config.ResolutionWidth == 0)
        {
            config.ResolutionWidth = width;
        }

        if (config.ResolutionHeight == 0)
        {
            config.ResolutionHeight = height;
        }

        config.SourcePath = path;
        config.LoadedFromFile = true;

        if (expectedWidth > 0
            && expectedHeight > 0
            && (config.ResolutionWidth != expectedWidth || config.ResolutionHeight != expectedHeight))
        {
            _logger.LogWarning(
                "Recognition region config resolution differs from current frame. Config={ConfigWidth}x{ConfigHeight}, Current={FrameWidth}x{FrameHeight}",
                config.ResolutionWidth,
                config.ResolutionHeight,
                expectedWidth,
                expectedHeight);
        }
    }

    private string? FindBestFallbackConfigPath(int width, int height)
    {
        if (width <= 0 || height <= 0)
        {
            return ListConfigPaths().FirstOrDefault();
        }

        return ListConfigPaths()
            .Select(path => new
            {
                Path = path,
                Resolution = GetResolutionFromPath(path)
            })
            .Where(candidate => candidate.Resolution.Width > 0 && candidate.Resolution.Height > 0)
            .OrderBy(candidate => GetResolutionMatchScore(
                width,
                height,
                candidate.Resolution.Width,
                candidate.Resolution.Height))
            .ThenBy(candidate => candidate.Path, StringComparer.OrdinalIgnoreCase)
            .Select(candidate => candidate.Path)
            .FirstOrDefault();
    }

    private static double GetResolutionMatchScore(
        int targetWidth,
        int targetHeight,
        int configWidth,
        int configHeight)
    {
        var targetAspect = targetWidth / (double)targetHeight;
        var configAspect = configWidth / (double)configHeight;
        var aspectDifference = Math.Abs(targetAspect - configAspect) / targetAspect;

        var targetArea = targetWidth * (double)targetHeight;
        var configArea = configWidth * (double)configHeight;
        var areaDifference = Math.Abs(Math.Log(configArea / targetArea));

        return (aspectDifference * 1000d) + areaDifference;
    }

    private static (int Width, int Height) GetResolutionFromPath(string path)
    {
        var fileName = Path.GetFileNameWithoutExtension(path);
        var parts = fileName.Split('x', 'X');
        if (parts.Length == 2
            && int.TryParse(parts[0], out var width)
            && int.TryParse(parts[1], out var height))
        {
            return (width, height);
        }

        return (0, 0);
    }
}
