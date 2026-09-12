using System.Text.Json;
using System.Text.Json.Serialization;

using Microsoft.Extensions.Logging;

using RocoPilot.Configuration;
using RocoPilot.Contracts.Services;
using RocoPilot.Contracts.Services.Encounters;
using RocoPilot.Contracts.Services.Spirits;
using RocoPilot.Contracts.Services.Statistics;
using RocoPilot.Helpers;
using RocoPilot.Models.Encounters;
using RocoPilot.Models.Statistics;
using RocoPilot.Models.Spirits;
using RocoPilot.Services.Spirits;
using RocoPilot.Services.Encounters;

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
    private readonly IEncounterSeasonConfigService? _seasonConfigService;
    private readonly ISpiritCatalogService? _spiritCatalogService;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private StatisticsDocument _document = StatisticsDocumentNormalizer.CreateDefault();
    private bool _isLoaded;
    private string? _selectedAccountUid;
    private string? _activeAccountUid;
    private bool _isActiveAccountSelectionRequired;
    private int _activeAccountWarningLogged;

    public event EventHandler<StatisticsDocumentChangedEventArgs>? DocumentChanged;

    public event EventHandler? SelectedAccountChanged;

    public StatisticsDocument CurrentDocument => CloneDocument(Volatile.Read(ref _document));

    public string? SelectedAccountUid => Volatile.Read(ref _selectedAccountUid);

    public string? ActiveAccountUid => Volatile.Read(ref _activeAccountUid);

    public bool IsActiveAccountSelectionRequired => Volatile.Read(ref _isActiveAccountSelectionRequired);

    public StatisticsService(
        ILocalSettingsService localSettingsService,
        ILogger<StatisticsService> logger,
        IEncounterSeasonConfigService? seasonConfigService = null,
        ISpiritCatalogService? spiritCatalogService = null)
    {
        _localSettingsService = localSettingsService;
        _logger = logger;
        _seasonConfigService = seasonConfigService;
        _spiritCatalogService = spiritCatalogService;
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

    public Task<StatisticsDocument> ReplaceAsync(StatisticsDocument document)
    {
        var replacement = CloneDocument(document);
        return UpdateAsync(_ => replacement);
    }

    public async Task<StatisticsDocumentMergeResult> MergeRemoteAsync(
        StatisticsDocument remoteDocument,
        IReadOnlyDictionary<string, string>? lastSyncedAccountFingerprints,
        bool preferRemoteAccountsWithoutBaseline,
        CancellationToken cancellationToken = default)
    {
        var remote = CloneDocument(remoteDocument);
        var baseline = lastSyncedAccountFingerprints?.ToDictionary(pair => pair.Key, pair => pair.Value);
        IReadOnlyList<string> conflicts = [];
        var changedDocument = await UpdateAsync(local =>
        {
            var result = StatisticsDocumentMerger.Merge(local, remote, baseline, preferRemoteAccountsWithoutBaseline);
            conflicts = result.ConflictingAccountUids;
            return result.Document;
        }, StatisticsDocumentChangeSource.CloudSync, cancellationToken);
        return new StatisticsDocumentMergeResult(changedDocument, conflicts);
    }

    public Task<StatisticsDocument> AddAccountAsync(string uid)
    {
        uid = uid.Trim();
        return UpdateAsync(document =>
        {
            if (!document.Accounts.Any(account => string.Equals(account.Uid, uid, StringComparison.OrdinalIgnoreCase)))
            {
                document.Accounts.Add(new AccountStatisticsData { Uid = uid });
            }
            return document;
        });
    }

    public Task<StatisticsDocument> DeleteAccountAsync(string uid)
    {
        return UpdateAsync(document =>
        {
            document.Accounts.RemoveAll(account => string.Equals(account.Uid, uid, StringComparison.OrdinalIgnoreCase));
            return document;
        });
    }

    public Task<StatisticsDocument> ClearAsync()
    {
        return UpdateAsync(document =>
        {
            document.Accounts.Clear();
            return document;
        });
    }

    public Task<StatisticsDocument> RecordEncounterAsync(
        EncounterSeasonDefinition season,
        string spiritName,
        DateTimeOffset capturedAt,
        string? accountUid = null)
    {
        spiritName = spiritName.Trim();
        if (string.IsNullOrWhiteSpace(spiritName))
        {
            return LoadAsync();
        }

        season = ResolveRecordingSeason(season, capturedAt);
        if (season.Id == EncounterSeasonTimeline.PendingSeasonId)
            return UpdateAccountAsync(account => StatisticsMutationRules.AddPendingEncounter(
                account, season, Guid.NewGuid().ToString("N"), string.Empty, capturedAt, spiritName),
                useActiveAccount: true, accountUid: accountUid);

        return UpdateAccountAsync(account =>
            StatisticsMutationRules.RecordEncounter(account, season, spiritName, capturedAt),
            useActiveAccount: true, accountUid: accountUid);
    }

    public Task<StatisticsDocument> AddPendingEncounterAsync(
        string accountUid, EncounterSeasonDefinition season, string id, string rawText, DateTimeOffset detectedAt,
        string? spiritName = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accountUid);
        ArgumentException.ThrowIfNullOrWhiteSpace(season.Id);
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        season = ResolveRecordingSeason(season, detectedAt);
        return UpdateAccountAsync(account =>
            StatisticsMutationRules.AddPendingEncounter(account, season, id, rawText, detectedAt, spiritName),
            useActiveAccount: true, accountUid: accountUid);
    }

    public async Task<PendingEncounterConfirmationResult> ConfirmPendingEncounterAsync(
        string accountUid, string id, string spiritName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accountUid);
        ArgumentException.ThrowIfNullOrWhiteSpace(spiritName);
        var result = PendingEncounterConfirmationResult.NotFound;
        await UpdateAccountAsync(account =>
            result = StatisticsMutationRules.ConfirmPendingEncounter(account, id, spiritName.Trim()),
            accountUid: accountUid);
        return result;
    }

    public Task<StatisticsDocument> DiscardPendingEncounterAsync(string accountUid, string id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accountUid);
        return UpdateAccountAsync(account =>
        {
            var pending = account.PendingEncounters.FirstOrDefault(item => item.Id == id);
            if (pending is { HandledAt: null }) pending.HandledAt = DateTimeOffset.Now;
        }, accountUid: accountUid);
    }

    public async Task<int> RematchPendingEncountersAsync(SpiritCatalogDocument catalog, double minimumSimilarity)
    {
        var index = new SpiritCatalogIndex(catalog);
        var matchedCount = 0;
        await UpdateAsync(document =>
        {
            matchedCount = MatchPendingNames(document, index, minimumSimilarity, out _);
            return document;
        });
        return matchedCount;
    }

    private static int MatchPendingNames(StatisticsDocument document, SpiritCatalogIndex index, double minimumSimilarity, out bool changed)
    {
        changed = false;
        var matchedCount = 0;
        foreach (var account in document.Accounts)
        {
            foreach (var pending in account.PendingEncounters.Where(item => item.HandledAt is null && string.IsNullOrWhiteSpace(item.Name)))
            {
                var matchedName = index.Match(pending.RawText, minimumSimilarity);
                if (string.IsNullOrWhiteSpace(matchedName)) continue;
                changed = true;
                var result = StatisticsMutationRules.ConfirmPendingEncounter(account, pending.Id, index.ResolveEvolutionRecordName(matchedName));
                if (result is PendingEncounterConfirmationResult.Counted or PendingEncounterConfirmationResult.AwaitingSeason)
                    matchedCount++;
            }
        }
        return matchedCount;
    }

    private EncounterSeasonDefinition ResolveRecordingSeason(EncounterSeasonDefinition fallback, DateTimeOffset occurredAt) =>
        _seasonConfigService is null ? fallback
            : EncounterSeasonTimeline.ResolveForRecording(_seasonConfigService.Load(), occurredAt, fallback);

    public Task<StatisticsDocument> UpsertEncounterAsync(
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
            return LoadAsync();
        }

        return UpdateAccountAsync(account =>
            StatisticsMutationRules.UpsertEncounter(account, seasonId, spiritName, count, countedAt));
    }

    public Task<StatisticsDocument> EditEncounterAsync(
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
            return LoadAsync();
        }

        return UpdateAccountAsync(account =>
            StatisticsMutationRules.EditEncounter(account, seasonId, originalName, nextName, nextCount, editedAt));
    }

    public Task<StatisticsDocument> DeleteEncounterAsync(string seasonId, string spiritName)
    {
        seasonId = seasonId.Trim();
        spiritName = spiritName.Trim();
        if (string.IsNullOrWhiteSpace(seasonId) || string.IsNullOrWhiteSpace(spiritName))
        {
            return LoadAsync();
        }

        return UpdateAccountAsync(account =>
            StatisticsMutationRules.DeleteEncounter(account, seasonId, spiritName));
    }

    public Task<StatisticsDocument> AddShinyCapturesAsync(
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
            return LoadAsync();
        }

        return UpdateAccountAsync(account =>
            StatisticsMutationRules.AddShinyCaptures(
                account,
                seasonId,
                spiritName,
                count,
                capturedAt,
                resetEncounterCount,
                encounterCountBeforeCapture));
    }

    public Task<StatisticsDocument> DeleteShinyCapturesAsync(string? seasonId, string spiritName)
    {
        seasonId = string.IsNullOrWhiteSpace(seasonId) ? null : seasonId.Trim();
        spiritName = spiritName.Trim();
        if (string.IsNullOrWhiteSpace(spiritName))
        {
            return LoadAsync();
        }

        return UpdateAccountAsync(account =>
            StatisticsMutationRules.DeleteShinyCaptures(account, seasonId, spiritName));
    }

    public Task<StatisticsDocument> EditShinyCaptureAsync(
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
            return LoadAsync();
        }

        return UpdateAccountAsync(account =>
            StatisticsMutationRules.EditShinyCapture(
                account,
                captureId,
                nextName,
                encounterCountBeforeCapture,
                capturedAt));
    }

    public Task<StatisticsDocument> DeleteShinyCaptureAsync(string captureId)
    {
        captureId = captureId.Trim();
        if (string.IsNullOrWhiteSpace(captureId))
        {
            return LoadAsync();
        }

        return UpdateAccountAsync(account =>
            StatisticsMutationRules.DeleteShinyCapture(account, captureId));
    }

    public Task<StatisticsDocument> AddPendingShinyCaptureAsync(
        EncounterSeasonDefinition season,
        string spiritName,
        DateTimeOffset detectedAt)
    {
        spiritName = spiritName.Trim();
        if (string.IsNullOrWhiteSpace(season.Id) || string.IsNullOrWhiteSpace(spiritName))
        {
            return LoadAsync();
        }

        season = ResolveRecordingSeason(season, detectedAt);
        return UpdateAccountAsync(account =>
            StatisticsMutationRules.AddPendingShinyCapture(account, season, spiritName, detectedAt), useActiveAccount: true);
    }

    public Task<StatisticsDocument> ConfirmPendingShinyCaptureAsync(
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
            return LoadAsync();
        }

        return UpdateAccountAsync(account =>
            StatisticsMutationRules.ConfirmPendingShinyCapture(
                account,
                pendingCaptureId,
                spiritName,
                encounterCount,
                confirmedAt));
    }

    public Task<StatisticsDocument> DiscardPendingShinyCaptureAsync(string pendingCaptureId)
    {
        pendingCaptureId = pendingCaptureId.Trim();
        if (string.IsNullOrWhiteSpace(pendingCaptureId))
        {
            return LoadAsync();
        }

        return UpdateAccountAsync(account =>
            StatisticsMutationRules.DiscardPendingShinyCapture(account, pendingCaptureId));
    }

    public void SetSelectedAccountUid(string? uid)
    {
        var nextUid = string.IsNullOrWhiteSpace(uid) ? null : uid.Trim();
        if (nextUid is not null && IsActiveAccountSelectionRequired)
        {
            SetActiveAccountUid(nextUid);
        }

        if (string.Equals(_selectedAccountUid, nextUid, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _selectedAccountUid = nextUid;
        SelectedAccountChanged?.Invoke(this, EventArgs.Empty);
    }

    public void SetActiveAccountUid(string uid)
    {
        uid = uid.Trim();
        if (string.IsNullOrWhiteSpace(uid))
        {
            throw new ArgumentException("UID 不能为空。", nameof(uid));
        }

        Volatile.Write(ref _activeAccountUid, uid);
        Volatile.Write(ref _isActiveAccountSelectionRequired, false);
        Interlocked.Exchange(ref _activeAccountWarningLogged, 0);
        _logger.LogInformation("本次启动的统计记录账号已设为 {Uid}。", uid);
    }

    public void RequireActiveAccountSelection()
    {
        Volatile.Write(ref _activeAccountUid, null);
        Volatile.Write(ref _isActiveAccountSelectionRequired, true);
        Interlocked.Exchange(ref _activeAccountWarningLogged, 0);
    }

    public IReadOnlyList<EncounterSpiritRecord> GetActiveAccountSeasonEncounters(string seasonId)
    {
        if (string.IsNullOrWhiteSpace(seasonId))
        {
            return [];
        }

        var account = ResolveActiveAccountForRead(Volatile.Read(ref _document));
        var season = account?.Seasons.FirstOrDefault(item =>
            string.Equals(item.Id, seasonId.Trim(), StringComparison.OrdinalIgnoreCase));
        if (season is null)
        {
            return [];
        }

        return season.Encounters
            .Where(record => !string.IsNullOrWhiteSpace(record.Name) && record.Count > 0)
            .GroupBy(record => TextMatchingHelper.NormalizeSpiritNameForMatching(record.Name), StringComparer.OrdinalIgnoreCase)
            .Where(group => !string.IsNullOrWhiteSpace(group.Key))
            .Select(group =>
            {
                var latestRecord = group
                    .OrderByDescending(record => record.LastCapturedAt)
                    .First();
                return new EncounterSpiritRecord
                {
                    Name = TextMatchingHelper.NormalizeSpiritNameForDisplay(latestRecord.Name),
                    Count = group.Sum(record => Math.Max(0, record.Count)),
                    Season = string.IsNullOrWhiteSpace(latestRecord.Season) ? season.Id : latestRecord.Season.Trim(),
                    LastCapturedAt = latestRecord.LastCapturedAt
                };
            })
            .OrderByDescending(record => record.LastCapturedAt)
            .ThenBy(record => record.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public IReadOnlyList<PendingShinyCaptureRecord> GetSelectedAccountPendingShinyCaptures()
    {
        var account = ResolveSelectedAccountForRead(Volatile.Read(ref _document));
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

    private Task<StatisticsDocument> UpdateAccountAsync(
        Action<AccountStatisticsData> update,
        bool useActiveAccount = false,
        string? accountUid = null)
    {
        // 账号属于这次操作的上下文，不能等拿到写锁后再读取用户的新选择。
        var selectionRequired = accountUid is null && useActiveAccount && IsActiveAccountSelectionRequired;
        var targetUid = accountUid ?? (useActiveAccount ? ActiveAccountUid : null)
            ?? SelectedAccountUid
            ?? Volatile.Read(ref _document).Accounts.FirstOrDefault()?.Uid;
        return UpdateAsync(document =>
        {
            if (selectionRequired)
            {
                LogMissingActiveAccountOnce("尚未确认本次启动使用的统计账号，本次自动统计已跳过。");
                return document;
            }

            // 首次载入前尚无默认账号时，才从载入的数据中选第一个账号。
            var account = targetUid is null ? document.Accounts.FirstOrDefault() : document.Accounts.FirstOrDefault(item =>
                string.Equals(item.Uid, targetUid, StringComparison.OrdinalIgnoreCase));
            if (account is null && !useActiveAccount)
                throw new InvalidOperationException("操作对应的统计账号已不存在，请重新选择账号后重试。");
            if (account is null)
            {
                if (string.Equals(ActiveAccountUid, targetUid, StringComparison.OrdinalIgnoreCase))
                    RequireActiveAccountSelection();
                LogMissingActiveAccountOnce($"统计账号 {targetUid} 已不存在，本次自动统计已跳过。");
            }
            if (account is not null) update(account);
            return document;
        });
    }

    private async Task<StatisticsDocument> UpdateAsync(
        Func<StatisticsDocument, StatisticsDocument> update,
        StatisticsDocumentChangeSource source = StatisticsDocumentChangeSource.Local,
        CancellationToken cancellationToken = default)
    {
        StatisticsDocument changedDocument;
        bool selectionChanged;
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (!_isLoaded) await LoadCoreAsync();

            // 已发布的文档只读。所有修改在副本上完成，持久化成功后再发布新快照。
            var workingDocument = CloneDocument(_document);
            if (_seasonConfigService is not null) StatisticsSeasonMigration.Apply(workingDocument, _seasonConfigService.Load());
            var nextDocument = StatisticsDocumentNormalizer.Normalize(update(workingDocument));
            if (_seasonConfigService is not null && StatisticsSeasonMigration.Apply(nextDocument, _seasonConfigService.Load()))
                nextDocument = StatisticsDocumentNormalizer.Normalize(nextDocument);
            cancellationToken.ThrowIfCancellationRequested();
            await _localSettingsService.SaveSettingAsync(SettingsKeys.StatisticsData, nextDocument);
            Volatile.Write(ref _document, nextDocument);
            selectionChanged = ReconcileAccountSelection(nextDocument);
            changedDocument = CloneDocument(nextDocument);
        }
        finally
        {
            _gate.Release();
        }

        DocumentChanged?.Invoke(this, new StatisticsDocumentChangedEventArgs(changedDocument, source));
        if (selectionChanged) SelectedAccountChanged?.Invoke(this, EventArgs.Empty);
        return changedDocument;
    }

    private async Task LoadCoreAsync()
    {
        try
        {
            var savedDocument = await _localSettingsService.ReadSettingAsync<StatisticsDocument>(SettingsKeys.StatisticsData);
            var document = savedDocument is null
                ? StatisticsDocumentNormalizer.CreateDefault()
                : StatisticsDocumentNormalizer.Normalize(CloneDocument(savedDocument));
            var changed = false;
            if (_seasonConfigService is not null)
            {
                var config = _seasonConfigService.Load();
                changed = StatisticsSeasonMigration.Apply(document, config);
                if (_spiritCatalogService is not null && document.Accounts.Any(account =>
                    account.PendingEncounters.Any(item => item.HandledAt is null && string.IsNullOrWhiteSpace(item.Name))))
                {
                    try
                    {
                        // 只读取本地图鉴；软件自带图鉴更新后也能补名称，不在启动时联网同步。
                        var catalog = await _spiritCatalogService.LoadAsync();
                        MatchPendingNames(document, new SpiritCatalogIndex(catalog), config.SpiritNameMatchThreshold, out var namesChanged);
                        changed |= namesChanged;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "暂存奇遇的本地图鉴匹配失败，保留原始记录等待后续同步。");
                    }
                }
            }
            if (changed)
            {
                document = StatisticsDocumentNormalizer.Normalize(document);
                await _localSettingsService.SaveSettingAsync(SettingsKeys.StatisticsData, document);
            }
            Volatile.Write(ref _document, document);
            _isLoaded = true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "读取统计数据失败，保留当前状态并等待重试。");
            throw;
        }
    }

    private bool ReconcileAccountSelection(StatisticsDocument document)
    {
        EnsureActiveAccountExists(document);
        if (document.Accounts.Count == 0) RequireActiveAccountSelection();

        var selectedUid = SelectedAccountUid;
        if (selectedUid is null || document.Accounts.Any(account =>
                string.Equals(account.Uid, selectedUid, StringComparison.OrdinalIgnoreCase))) return false;

        // 保存期间用户可能切换账号，只修复仍指向被删除账号的选择。
        var nextUid = document.Accounts.FirstOrDefault()?.Uid;
        return Interlocked.CompareExchange(ref _selectedAccountUid, nextUid, selectedUid) == selectedUid;
    }

    private AccountStatisticsData? ResolveActiveAccountForRead(StatisticsDocument document)
    {
        if (IsActiveAccountSelectionRequired)
        {
            return null;
        }

        var activeAccountUid = ActiveAccountUid;
        return !string.IsNullOrWhiteSpace(activeAccountUid)
            ? document.Accounts.FirstOrDefault(account =>
                string.Equals(account.Uid, activeAccountUid, StringComparison.OrdinalIgnoreCase))
            : ResolveSelectedAccountForRead(document);
    }

    private void EnsureActiveAccountExists(StatisticsDocument document)
    {
        var activeAccountUid = ActiveAccountUid;
        if (!string.IsNullOrWhiteSpace(activeAccountUid)
            && !document.Accounts.Any(account =>
                string.Equals(account.Uid, activeAccountUid, StringComparison.OrdinalIgnoreCase)))
        {
            RequireActiveAccountSelection();
        }
    }

    private void LogMissingActiveAccountOnce(string message)
    {
        if (Interlocked.Exchange(ref _activeAccountWarningLogged, 1) == 0)
        {
            _logger.LogWarning("{Message}", message);
        }
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

