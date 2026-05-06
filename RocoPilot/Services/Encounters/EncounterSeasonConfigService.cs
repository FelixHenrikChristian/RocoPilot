using System.Text.Json;

using Microsoft.Extensions.Logging;

using RocoPilot.Contracts.Services.Encounters;
using RocoPilot.Models.Encounters;

namespace RocoPilot.Services.Encounters;

public sealed class EncounterSeasonConfigService : IEncounterSeasonConfigService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    private readonly ILogger<EncounterSeasonConfigService> _logger;
    private EncounterSeasonConfig? _config;

    public EncounterSeasonConfigService(ILogger<EncounterSeasonConfigService> logger)
    {
        _logger = logger;
    }

    public EncounterSeasonConfig Load()
    {
        if (_config is not null)
        {
            return _config;
        }

        var configPath = Path.Combine(
            AppContext.BaseDirectory,
            "Configuration",
            "EncounterSeasons",
            "seasons.json");

        try
        {
            if (File.Exists(configPath))
            {
                var json = File.ReadAllText(configPath);
                var config = JsonSerializer.Deserialize<EncounterSeasonConfig>(json, JsonOptions);
                if (config?.Seasons is { Count: > 0 })
                {
                    _config = Normalize(config);
                    return _config;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "读取赛季奇遇配置失败。");
        }

        _logger.LogWarning("未找到有效赛季奇遇配置文件。路径：{ConfigPath}", configPath);
        _config = new EncounterSeasonConfig();
        return _config;
    }

    public EncounterSeasonDefinition? GetCurrentSeason()
    {
        var config = Load();
        return config.Seasons.FirstOrDefault(season =>
                string.Equals(season.Id, config.CurrentSeasonId, StringComparison.OrdinalIgnoreCase))
            ?? config.Seasons.FirstOrDefault();
    }

    private static EncounterSeasonConfig Normalize(EncounterSeasonConfig config)
    {
        config.Seasons = config.Seasons
            .Where(season => !string.IsNullOrWhiteSpace(season.Id))
            .Select(NormalizeSeason)
            .ToList();

        config.CurrentSeasonId = string.IsNullOrWhiteSpace(config.CurrentSeasonId)
            ? config.Seasons.FirstOrDefault()?.Id ?? string.Empty
            : config.CurrentSeasonId.Trim();

        return config;
    }

    private static EncounterSeasonDefinition NormalizeSeason(EncounterSeasonDefinition season)
    {
        season.Id = season.Id.Trim();
        season.Name = string.IsNullOrWhiteSpace(season.Name)
            ? $"{season.Id}赛季"
            : season.Name.Trim();
        season.DateRange = season.DateRange?.Trim() ?? string.Empty;
        season.EncounterTypeName = season.EncounterTypeName?.Trim() ?? string.Empty;
        season.TipText = season.TipText?.Trim() ?? string.Empty;
        season.MatchThreshold = Math.Clamp(season.MatchThreshold, 0.5, 1);
        return season;
    }
}
