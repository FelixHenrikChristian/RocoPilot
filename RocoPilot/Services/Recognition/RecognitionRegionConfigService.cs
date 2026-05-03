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

    public RecognitionRegionConfig LoadForResolution(int width, int height)
    {
        var path = GetConfigPath(width, height);
        if (!File.Exists(path))
        {
            _logger.LogWarning("未找到识别区域配置：{ConfigPath}", path);
            return RecognitionRegionConfig.Empty(width, height, path);
        }

        try
        {
            var json = File.ReadAllText(path);
            var config = JsonSerializer.Deserialize<RecognitionRegionConfig>(json, SerializerOptions)
                ?? RecognitionRegionConfig.Empty(width, height, path);

            NormalizeConfig(config, width, height, path);
            _logger.LogInformation(
                "已载入识别区域配置：{ConfigPath}，区域数量：{RegionCount}",
                path,
                config.Regions.Count(region => region.Enabled));

            return config;
        }
        catch (JsonException ex)
        {
            _logger.LogError(ex, "识别区域配置 JSON 无效：{ConfigPath}", path);
            return RecognitionRegionConfig.Empty(width, height, path);
        }
        catch (IOException ex)
        {
            _logger.LogError(ex, "读取识别区域配置失败：{ConfigPath}", path);
            return RecognitionRegionConfig.Empty(width, height, path);
        }
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

        if (config.ResolutionWidth != width || config.ResolutionHeight != height)
        {
            _logger.LogWarning(
                "识别区域配置分辨率与当前截图不一致。配置：{ConfigWidth}x{ConfigHeight}，当前：{FrameWidth}x{FrameHeight}",
                config.ResolutionWidth,
                config.ResolutionHeight,
                width,
                height);
        }
    }
}
