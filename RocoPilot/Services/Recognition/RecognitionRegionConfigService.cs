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
        if (!File.Exists(path))
        {
            _logger.LogWarning("未找到识别区域配置：{ConfigPath}", path);
            return RecognitionRegionConfig.Empty(width, height, path);
        }

        return LoadFromPath(path, width, height);
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
            var width = expectedWidth > 0 ? expectedWidth : fallbackWidth;
            var height = expectedHeight > 0 ? expectedHeight : fallbackHeight;
            var json = File.ReadAllText(path);
            var config = JsonSerializer.Deserialize<RecognitionRegionConfig>(json, SerializerOptions)
                ?? RecognitionRegionConfig.Empty(width, height, path);

            NormalizeConfig(config, width, height, path);
            _logger.LogDebug(
                "已载入识别区域配置：{ConfigPath}，区域数量：{RegionCount}",
                path,
                config.Regions.Count(region => region.Enabled));

            return config;
        }
        catch (JsonException ex)
        {
            _logger.LogError(ex, "识别区域配置 JSON 无效：{ConfigPath}", path);
            return RecognitionRegionConfig.Empty(expectedWidth, expectedHeight, path);
        }
        catch (IOException ex)
        {
            _logger.LogError(ex, "读取识别区域配置失败：{ConfigPath}", path);
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
            throw new InvalidOperationException("识别区域配置缺少保存路径。");
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
            "已保存识别区域配置：{ConfigPath}，区域数量：{RegionCount}",
            path,
            config.Regions.Count(region => region.Enabled));
    }

    public string GetConfigPath(int width, int height)
    {
        return Path.Combine(_configDirectory, $"{width}x{height}.json");
    }

    private void NormalizeConfig(RecognitionRegionConfig config, int width, int height, string path)
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

        if (width > 0
            && height > 0
            && (config.ResolutionWidth != width || config.ResolutionHeight != height))
        {
            _logger.LogWarning(
                "识别区域配置分辨率与当前截图不一致。配置：{ConfigWidth}x{ConfigHeight}，当前：{FrameWidth}x{FrameHeight}",
                config.ResolutionWidth,
                config.ResolutionHeight,
                width,
                height);
        }
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
