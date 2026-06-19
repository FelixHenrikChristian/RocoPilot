using System.Text.Json;
using System.Text.Json.Serialization;

using Microsoft.Extensions.Logging;

using RocoPilot.Contracts.Services.Recognition;
using RocoPilot.Models.Recognition;

namespace RocoPilot.Services.Recognition;

public sealed class RecognitionRegionConfigService : IRecognitionRegionConfigService
{
    private const double AspectRatioTolerance = 0.01d;
    private static readonly (int Width, int Height)[] SupportedConfigResolutions =
    [
        (2048, 1152),
        (1920, 1440)
    ];

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
        if (!TryResolveConfigResolution(width, height, out var configWidth, out var configHeight))
        {
            var unsupportedPath = GetConfigPath(width, height);
            _logger.LogWarning(
                "不支持的游戏客户区分辨率：{Width}x{Height}，宽高比 {AspectRatio:F6}。当前仅支持 16:9 和 4:3。",
                width,
                height,
                GetAspectRatio(width, height));
            return RecognitionRegionConfig.Empty(width, height, unsupportedPath);
        }

        var path = GetConfigPath(configWidth, configHeight);
        if (!File.Exists(path))
        {
            _logger.LogWarning(
                "未找到游戏分辨率配置：Detected={Width}x{Height}, Config={ConfigWidth}x{ConfigHeight}, Path={ConfigPath}",
                width,
                height,
                configWidth,
                configHeight,
                path);
            return RecognitionRegionConfig.Empty(configWidth, configHeight, path);
        }

        _logger.LogDebug(
            "游戏分辨率配置匹配：Detected={Width}x{Height}, AspectRatio={AspectRatio:F6}, Config={ConfigWidth}x{ConfigHeight}, Path={ConfigPath}",
            width,
            height,
            GetAspectRatio(width, height),
            configWidth,
            configHeight,
            path);

        return LoadFromPath(path, configWidth, configHeight);
    }

    public bool TryResolveConfigResolution(
        int width,
        int height,
        out int configWidth,
        out int configHeight)
    {
        configWidth = 0;
        configHeight = 0;

        var aspectRatio = GetAspectRatio(width, height);
        if (aspectRatio <= 0)
        {
            return false;
        }

        var match = SupportedConfigResolutions
            .Select(resolution => new
            {
                resolution.Width,
                resolution.Height,
                Difference = Math.Abs(aspectRatio - GetAspectRatio(resolution.Width, resolution.Height))
                    / GetAspectRatio(resolution.Width, resolution.Height)
            })
            .Where(candidate => candidate.Difference <= AspectRatioTolerance)
            .OrderBy(candidate => candidate.Difference)
            .FirstOrDefault();
        if (match is null)
        {
            return false;
        }

        configWidth = match.Width;
        configHeight = match.Height;
        return true;
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

    private static double GetAspectRatio(int width, int height)
    {
        return width > 0 && height > 0 ? width / (double)height : 0d;
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
