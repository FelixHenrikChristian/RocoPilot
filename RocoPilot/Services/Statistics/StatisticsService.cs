using System.Text.Json;
using System.Text.Json.Serialization;

using Microsoft.Extensions.Logging;

using RocoPilot.Configuration;
using RocoPilot.Contracts.Services;
using RocoPilot.Contracts.Services.Statistics;
using RocoPilot.Models.Encounters;
using RocoPilot.Models.Statistics;

namespace RocoPilot.Services.Statistics;

public sealed class StatisticsService : IStatisticsService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true
    };

    private readonly ILocalSettingsService _localSettingsService;
    private readonly ILogger<StatisticsService> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private StatisticsDocument _document = CreateDefaultDocument();
    private bool _isLoaded;
    private string? _selectedAccountUid;

    public event EventHandler<StatisticsDocumentChangedEventArgs>? DocumentChanged;

    public StatisticsDocument CurrentDocument => CloneDocument(_document);

    public StatisticsService(
        ILocalSettingsService localSettingsService,
        ILogger<StatisticsService> logger)
    {
        _localSettingsService = localSettingsService;
        _logger = logger;
    }

    public async Task<StatisticsDocument> LoadAsync()
    {
        await _gate.WaitAsync();
        try
        {
            if (_isLoaded)
            {
                return CloneDocument(_document);
            }

            await LoadCoreAsync();
            return CloneDocument(_document);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<StatisticsDocument> ReplaceAsync(StatisticsDocument document)
    {
        var changedDocument = await UpdateAsync(() => NormalizeDocument(document));
        RaiseDocumentChanged(changedDocument);
        return changedDocument;
    }

    public async Task<StatisticsDocument> AddAccountAsync(string uid)
    {
        uid = uid.Trim();
        var changedDocument = await UpdateAsync(() =>
        {
            if (_document.Accounts.Any(account => string.Equals(account.Uid, uid, StringComparison.OrdinalIgnoreCase)))
            {
                return _document;
            }

            _document.Accounts.Add(new AccountStatisticsData { Uid = uid });
            return NormalizeDocument(_document);
        });
        RaiseDocumentChanged(changedDocument);
        return changedDocument;
    }

    public async Task<StatisticsDocument> DeleteAccountAsync(string uid)
    {
        var changedDocument = await UpdateAsync(() =>
        {
            var account = _document.Accounts.FirstOrDefault(account =>
                string.Equals(account.Uid, uid, StringComparison.OrdinalIgnoreCase));
            if (account is not null)
            {
                _document.Accounts.Remove(account);
            }

            if (string.Equals(_selectedAccountUid, uid, StringComparison.OrdinalIgnoreCase))
            {
                _selectedAccountUid = _document.Accounts.FirstOrDefault()?.Uid;
            }

            return NormalizeDocument(_document);
        });
        RaiseDocumentChanged(changedDocument);
        return changedDocument;
    }

    public async Task<StatisticsDocument> ClearAsync()
    {
        var changedDocument = await UpdateAsync(() =>
        {
            _document.Accounts.Clear();
            _selectedAccountUid = null;
            return NormalizeDocument(_document);
        });
        RaiseDocumentChanged(changedDocument);
        return changedDocument;
    }

    public async Task<StatisticsDocument> RecordEncounterAsync(
        EncounterSeasonDefinition season,
        string spiritName,
        DateTimeOffset capturedAt)
    {
        spiritName = spiritName.Trim();
        if (string.IsNullOrWhiteSpace(spiritName))
        {
            return await LoadAsync();
        }

        var changedDocument = await UpdateAsync(() =>
        {
            var account = ResolveTargetAccount(_document);
            if (account is null)
            {
                return _document;
            }

            var seasonData = ResolveSeason(account, season);
            var record = seasonData.Encounters.FirstOrDefault(item =>
                string.Equals(item.Name, spiritName, StringComparison.OrdinalIgnoreCase));

            if (record is null)
            {
                seasonData.Encounters.Add(new EncounterSpiritRecord
                {
                    Name = spiritName,
                    Count = 1,
                    Season = season.Id,
                    LastCapturedAt = capturedAt
                });
            }
            else
            {
                record.Count++;
                record.Season = season.Id;
                record.LastCapturedAt = capturedAt;
            }

            return NormalizeDocument(_document);
        });

        RaiseDocumentChanged(changedDocument);
        return changedDocument;
    }

    public void SetSelectedAccountUid(string? uid)
    {
        _selectedAccountUid = string.IsNullOrWhiteSpace(uid) ? null : uid.Trim();
    }

    private async Task<StatisticsDocument> UpdateAsync(Func<StatisticsDocument> update)
    {
        await _gate.WaitAsync();
        try
        {
            if (!_isLoaded)
            {
                await LoadCoreAsync();
            }

            _document = update();
            await PersistAsync();
            return CloneDocument(_document);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task LoadCoreAsync()
    {
        try
        {
            var savedDocument = await _localSettingsService.ReadSettingAsync<StatisticsDocument>(SettingsKeys.StatisticsData);
            _document = savedDocument is null
                ? CreateDefaultDocument()
                : NormalizeDocument(savedDocument);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "读取统计数据失败，已使用空统计数据。");
            _document = CreateDefaultDocument();
        }

        _isLoaded = true;
    }

    private async Task PersistAsync()
    {
        await _localSettingsService.SaveSettingAsync(SettingsKeys.StatisticsData, _document);
    }

    private void RaiseDocumentChanged(StatisticsDocument document)
    {
        DocumentChanged?.Invoke(this, new StatisticsDocumentChangedEventArgs(document));
    }

    private AccountStatisticsData? ResolveTargetAccount(StatisticsDocument document)
    {
        var account = !string.IsNullOrWhiteSpace(_selectedAccountUid)
            ? document.Accounts.FirstOrDefault(account =>
                string.Equals(account.Uid, _selectedAccountUid, StringComparison.OrdinalIgnoreCase))
            : null;

        if (account is not null)
        {
            return account;
        }

        account = document.Accounts.FirstOrDefault();
        if (account is not null)
        {
            _selectedAccountUid = account.Uid;
            return account;
        }

        _logger.LogWarning("没有可写入的统计账号，本次奇遇记录已跳过。");
        return null;
    }

    private static SeasonStatisticsData ResolveSeason(
        AccountStatisticsData account,
        EncounterSeasonDefinition season)
    {
        var seasonData = account.Seasons.FirstOrDefault(item =>
            string.Equals(item.Id, season.Id, StringComparison.OrdinalIgnoreCase));
        if (seasonData is null)
        {
            seasonData = new SeasonStatisticsData
            {
                Id = season.Id,
                Name = string.IsNullOrWhiteSpace(season.Name) ? $"{season.Id}赛季" : season.Name,
                DateRange = season.DateRange,
                EncounterTypeName = season.EncounterTypeName
            };
            account.Seasons.Add(seasonData);
        }

        if (!string.IsNullOrWhiteSpace(season.Name))
        {
            seasonData.Name = season.Name;
        }

        if (!string.IsNullOrWhiteSpace(season.DateRange))
        {
            seasonData.DateRange = season.DateRange;
        }

        if (!string.IsNullOrWhiteSpace(season.EncounterTypeName))
        {
            seasonData.EncounterTypeName = season.EncounterTypeName;
        }

        return seasonData;
    }

    private static StatisticsDocument NormalizeDocument(StatisticsDocument document)
    {
        document.Info ??= new StatisticsDocumentInfo();
        document.Info.Format = string.IsNullOrWhiteSpace(document.Info.Format)
            ? StatisticsDocumentFormats.RocoPilotStatistics
            : document.Info.Format.Trim();
        document.Info.Version = string.IsNullOrWhiteSpace(document.Info.Version)
            ? StatisticsDocumentFormats.CurrentVersion
            : document.Info.Version.Trim();
        document.Info.ExportApp = string.IsNullOrWhiteSpace(document.Info.ExportApp)
            ? "RocoPilot"
            : document.Info.ExportApp.Trim();
        document.Accounts ??= [];

        document.Accounts = document.Accounts
            .Where(account => !string.IsNullOrWhiteSpace(account.Uid))
            .GroupBy(account => account.Uid.Trim(), StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                var account = group.First();
                account.Uid = group.Key;
                account.Seasons = group
                    .SelectMany(item => item.Seasons ?? [])
                    .GroupBy(season => ResolveSeasonId(season), StringComparer.OrdinalIgnoreCase)
                    .Select(seasonGroup => NormalizeSeason(seasonGroup.Key, seasonGroup))
                    .Where(season => !string.IsNullOrWhiteSpace(season.Id))
                    .OrderBy(season => season.Id, StringComparer.OrdinalIgnoreCase)
                    .ToList();
                return account;
            })
            .OrderBy(account => account.Uid, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return document;
    }

    private static SeasonStatisticsData NormalizeSeason(
        string seasonId,
        IEnumerable<SeasonStatisticsData> seasons)
    {
        var seasonList = seasons.ToList();
        var first = seasonList.First();
        var normalized = new SeasonStatisticsData
        {
            Id = seasonId,
            Name = string.IsNullOrWhiteSpace(first.Name) ? $"{seasonId}赛季" : first.Name.Trim(),
            DateRange = first.DateRange?.Trim() ?? string.Empty,
            EncounterTypeName = first.EncounterTypeName?.Trim() ?? string.Empty
        };

        normalized.Encounters = seasonList
            .SelectMany(season => season.Encounters ?? [])
            .Where(record => !string.IsNullOrWhiteSpace(record.Name) && record.Count > 0)
            .GroupBy(record => record.Name.Trim(), StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                var latestRecord = group
                    .OrderByDescending(record => record.LastCapturedAt)
                    .First();
                return new EncounterSpiritRecord
                {
                    Name = latestRecord.Name.Trim(),
                    Count = group.Sum(record => Math.Max(0, record.Count)),
                    Season = string.IsNullOrWhiteSpace(latestRecord.Season) ? seasonId : latestRecord.Season.Trim(),
                    LastCapturedAt = latestRecord.LastCapturedAt
                };
            })
            .OrderByDescending(record => record.LastCapturedAt)
            .ThenBy(record => record.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        normalized.ShinyCaptures = seasonList
            .SelectMany(season => season.ShinyCaptures ?? [])
            .Where(record => !string.IsNullOrWhiteSpace(record.Name))
            .Select(record => new ShinySpiritCaptureRecord
            {
                Name = record.Name.Trim(),
                Season = string.IsNullOrWhiteSpace(record.Season) ? seasonId : record.Season.Trim(),
                CapturedAt = record.CapturedAt
            })
            .OrderByDescending(record => record.CapturedAt)
            .ThenBy(record => record.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return normalized;
    }

    private static string ResolveSeasonId(SeasonStatisticsData season)
    {
        if (!string.IsNullOrWhiteSpace(season.Id))
        {
            return season.Id.Trim();
        }

        if (!string.IsNullOrWhiteSpace(season.Name))
        {
            return season.Name.Trim().Replace("赛季", string.Empty, StringComparison.OrdinalIgnoreCase);
        }

        return string.Empty;
    }

    private static StatisticsDocument CloneDocument(StatisticsDocument document)
    {
        var json = JsonSerializer.Serialize(document, JsonOptions);
        return JsonSerializer.Deserialize<StatisticsDocument>(json, JsonOptions) ?? new StatisticsDocument();
    }

    private static StatisticsDocument CreateDefaultDocument()
    {
        return NormalizeDocument(new StatisticsDocument
        {
            Info = new StatisticsDocumentInfo
            {
                Format = StatisticsDocumentFormats.RocoPilotStatistics,
                Version = StatisticsDocumentFormats.CurrentVersion,
                ExportApp = "RocoPilot",
                ExportedAt = DateTimeOffset.Now
            },
            Accounts = []
        });
    }
}
