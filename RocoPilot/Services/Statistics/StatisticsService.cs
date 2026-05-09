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

    private StatisticsDocument _document = StatisticsDocumentNormalizer.CreateDefault();
    private bool _isLoaded;
    private string? _selectedAccountUid;

    public event EventHandler<StatisticsDocumentChangedEventArgs>? DocumentChanged;

    public event EventHandler? SelectedAccountChanged;

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
        var changedDocument = await UpdateAsync(() => StatisticsDocumentNormalizer.Normalize(document));
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
            return StatisticsDocumentNormalizer.Normalize(_document);
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

            return StatisticsDocumentNormalizer.Normalize(_document);
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
            return StatisticsDocumentNormalizer.Normalize(_document);
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

            StatisticsMutationRules.RecordEncounter(account, season, spiritName, capturedAt);

            return StatisticsDocumentNormalizer.Normalize(_document);
        });

        RaiseDocumentChanged(changedDocument);
        return changedDocument;
    }

    public async Task<StatisticsDocument> UpsertEncounterAsync(
        string seasonId,
        string spiritName,
        int count,
        DateTimeOffset countedAt)
    {
        seasonId = seasonId.Trim();
        spiritName = spiritName.Trim();
        count = Math.Max(0, count);
        if (string.IsNullOrWhiteSpace(seasonId)
            || string.IsNullOrWhiteSpace(spiritName)
            || count <= 0)
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

            StatisticsMutationRules.UpsertEncounter(account, seasonId, spiritName, count, countedAt);

            return StatisticsDocumentNormalizer.Normalize(_document);
        });

        RaiseDocumentChanged(changedDocument);
        return changedDocument;
    }

    public async Task<StatisticsDocument> EditEncounterAsync(
        string seasonId,
        string originalName,
        string nextName,
        int nextCount,
        DateTimeOffset editedAt)
    {
        seasonId = seasonId.Trim();
        originalName = originalName.Trim();
        nextName = nextName.Trim();
        nextCount = Math.Max(0, nextCount);
        if (string.IsNullOrWhiteSpace(seasonId)
            || string.IsNullOrWhiteSpace(originalName)
            || string.IsNullOrWhiteSpace(nextName)
            || nextCount <= 0)
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

            StatisticsMutationRules.EditEncounter(account, seasonId, originalName, nextName, nextCount, editedAt);

            return StatisticsDocumentNormalizer.Normalize(_document);
        });

        RaiseDocumentChanged(changedDocument);
        return changedDocument;
    }

    public async Task<StatisticsDocument> DeleteEncounterAsync(string seasonId, string spiritName)
    {
        seasonId = seasonId.Trim();
        spiritName = spiritName.Trim();
        if (string.IsNullOrWhiteSpace(seasonId) || string.IsNullOrWhiteSpace(spiritName))
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

            StatisticsMutationRules.DeleteEncounter(account, seasonId, spiritName);

            return StatisticsDocumentNormalizer.Normalize(_document);
        });

        RaiseDocumentChanged(changedDocument);
        return changedDocument;
    }

    public async Task<StatisticsDocument> AddShinyCapturesAsync(
        string seasonId,
        string spiritName,
        int count,
        DateTimeOffset capturedAt,
        bool resetEncounterCount = false,
        int? encounterCountBeforeCapture = null)
    {
        seasonId = seasonId.Trim();
        spiritName = spiritName.Trim();
        count = Math.Max(0, count);
        if (string.IsNullOrWhiteSpace(seasonId)
            || string.IsNullOrWhiteSpace(spiritName)
            || count <= 0)
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

            StatisticsMutationRules.AddShinyCaptures(
                account,
                seasonId,
                spiritName,
                count,
                capturedAt,
                resetEncounterCount,
                encounterCountBeforeCapture);

            return StatisticsDocumentNormalizer.Normalize(_document);
        });

        RaiseDocumentChanged(changedDocument);
        return changedDocument;
    }

    public async Task<StatisticsDocument> DeleteShinyCapturesAsync(string? seasonId, string spiritName)
    {
        seasonId = string.IsNullOrWhiteSpace(seasonId) ? null : seasonId.Trim();
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

            StatisticsMutationRules.DeleteShinyCaptures(account, seasonId, spiritName);

            return StatisticsDocumentNormalizer.Normalize(_document);
        });

        RaiseDocumentChanged(changedDocument);
        return changedDocument;
    }

    public async Task<StatisticsDocument> EditShinyCaptureAsync(
        string captureId,
        string nextName,
        int encounterCountBeforeCapture,
        DateTimeOffset capturedAt)
    {
        captureId = captureId.Trim();
        nextName = nextName.Trim();
        encounterCountBeforeCapture = Math.Max(0, encounterCountBeforeCapture);
        if (string.IsNullOrWhiteSpace(captureId) || string.IsNullOrWhiteSpace(nextName))
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

            StatisticsMutationRules.EditShinyCapture(
                account,
                captureId,
                nextName,
                encounterCountBeforeCapture,
                capturedAt);

            return StatisticsDocumentNormalizer.Normalize(_document);
        });

        RaiseDocumentChanged(changedDocument);
        return changedDocument;
    }

    public async Task<StatisticsDocument> DeleteShinyCaptureAsync(string captureId)
    {
        captureId = captureId.Trim();
        if (string.IsNullOrWhiteSpace(captureId))
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

            StatisticsMutationRules.DeleteShinyCapture(account, captureId);

            return StatisticsDocumentNormalizer.Normalize(_document);
        });

        RaiseDocumentChanged(changedDocument);
        return changedDocument;
    }

    public async Task<StatisticsDocument> AddPendingShinyCaptureAsync(
        EncounterSeasonDefinition season,
        string spiritName,
        DateTimeOffset detectedAt)
    {
        spiritName = spiritName.Trim();
        if (string.IsNullOrWhiteSpace(season.Id) || string.IsNullOrWhiteSpace(spiritName))
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

            StatisticsMutationRules.AddPendingShinyCapture(account, season, spiritName, detectedAt);

            return StatisticsDocumentNormalizer.Normalize(_document);
        });

        RaiseDocumentChanged(changedDocument);
        return changedDocument;
    }

    public async Task<StatisticsDocument> ConfirmPendingShinyCaptureAsync(
        string pendingCaptureId,
        string spiritName,
        int encounterCount,
        DateTimeOffset confirmedAt)
    {
        pendingCaptureId = pendingCaptureId.Trim();
        spiritName = spiritName.Trim();
        encounterCount = Math.Max(0, encounterCount);
        if (string.IsNullOrWhiteSpace(pendingCaptureId)
            || string.IsNullOrWhiteSpace(spiritName))
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

            StatisticsMutationRules.ConfirmPendingShinyCapture(
                account,
                pendingCaptureId,
                spiritName,
                encounterCount,
                confirmedAt);

            return StatisticsDocumentNormalizer.Normalize(_document);
        });

        RaiseDocumentChanged(changedDocument);
        return changedDocument;
    }

    public async Task<StatisticsDocument> DiscardPendingShinyCaptureAsync(string pendingCaptureId)
    {
        pendingCaptureId = pendingCaptureId.Trim();
        if (string.IsNullOrWhiteSpace(pendingCaptureId))
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

            StatisticsMutationRules.DiscardPendingShinyCapture(account, pendingCaptureId);

            return StatisticsDocumentNormalizer.Normalize(_document);
        });

        RaiseDocumentChanged(changedDocument);
        return changedDocument;
    }

    public void SetSelectedAccountUid(string? uid)
    {
        var nextUid = string.IsNullOrWhiteSpace(uid) ? null : uid.Trim();
        if (string.Equals(_selectedAccountUid, nextUid, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _selectedAccountUid = nextUid;
        SelectedAccountChanged?.Invoke(this, EventArgs.Empty);
    }

    public IReadOnlyList<EncounterSpiritRecord> GetSelectedAccountSeasonEncounters(string seasonId)
    {
        if (string.IsNullOrWhiteSpace(seasonId))
        {
            return [];
        }

        var account = ResolveSelectedAccountForRead(_document);
        var season = account?.Seasons.FirstOrDefault(item =>
            string.Equals(item.Id, seasonId.Trim(), StringComparison.OrdinalIgnoreCase));
        if (season is null)
        {
            return [];
        }

        return season.Encounters
            .Where(record => !string.IsNullOrWhiteSpace(record.Name) && record.Count > 0)
            .Select(record => new EncounterSpiritRecord
            {
                Name = record.Name.Trim(),
                Count = record.Count,
                Season = string.IsNullOrWhiteSpace(record.Season) ? season.Id : record.Season.Trim(),
                LastCapturedAt = record.LastCapturedAt
            })
            .OrderByDescending(record => record.LastCapturedAt)
            .ThenBy(record => record.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public IReadOnlyList<PendingShinyCaptureRecord> GetSelectedAccountPendingShinyCaptures()
    {
        var account = ResolveSelectedAccountForRead(_document);
        if (account is null)
        {
            return [];
        }

        return account.PendingShinyCaptures
            .Where(record => !string.IsNullOrWhiteSpace(record.Id)
                && !string.IsNullOrWhiteSpace(record.Name)
                && !string.IsNullOrWhiteSpace(record.Season))
            .Select(record => new PendingShinyCaptureRecord
            {
                Id = record.Id.Trim(),
                Name = record.Name.Trim(),
                Season = record.Season.Trim(),
                DetectedAt = record.DetectedAt
            })
            .OrderByDescending(record => record.DetectedAt)
            .ThenBy(record => record.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
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
                ? StatisticsDocumentNormalizer.CreateDefault()
                : StatisticsDocumentNormalizer.Normalize(savedDocument);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "读取统计数据失败，已使用空统计数据。");
            _document = StatisticsDocumentNormalizer.CreateDefault();
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

    private AccountStatisticsData? ResolveSelectedAccountForRead(StatisticsDocument document)
    {
        if (!string.IsNullOrWhiteSpace(_selectedAccountUid))
        {
            var selectedAccount = document.Accounts.FirstOrDefault(account =>
                string.Equals(account.Uid, _selectedAccountUid, StringComparison.OrdinalIgnoreCase));
            if (selectedAccount is not null)
            {
                return selectedAccount;
            }
        }

        return document.Accounts.FirstOrDefault();
    }

    private static StatisticsDocument CloneDocument(StatisticsDocument document)
    {
        var json = JsonSerializer.Serialize(document, JsonOptions);
        return JsonSerializer.Deserialize<StatisticsDocument>(json, JsonOptions) ?? new StatisticsDocument();
    }
}

