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
    private string _deviceId = string.Empty;
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
        var replacement = StatisticsDocumentMerger.Prepare(document);
        return UpdateAsync(_ => replacement);
    }

    public Task<StatisticsDocument> MergeRemoteAsync(StatisticsDocument remoteDocument,
        CancellationToken cancellationToken = default)
    {
        var remote = CloneDocument(remoteDocument);
        return UpdateAsync(local => StatisticsDocumentMerger.Merge(local, remote),
            StatisticsDocumentChangeSource.CloudSync, cancellationToken);
    }

    public Task<StatisticsDocument> ResolveConflictAsync(StatisticsSyncConflictException conflict, bool useRemote,
        CancellationToken cancellationToken = default)
    {
        // 在写锁内核对用户看到的快照；等待确认期间的新增不能被覆盖。
        return UpdateAsync(document =>
        {
            var current = StatisticsDocumentMerger.Find(document, conflict.Local.Uid);
            if (StatisticsDocumentMerger.Fingerprint(current) != StatisticsDocumentMerger.Fingerprint(conflict.Local))
                throw new InvalidOperationException("本地账号在核对期间发生了变化，请重新同步并核对最新内容。");
            // 深拷贝后修改，确保失败重试不会改变展示过的冲突快照。
            var selected = JsonSerializer.Deserialize<StatisticsAccountSnapshot>(
                JsonSerializer.Serialize(useRemote ? conflict.Remote : conflict.Local, JsonOptions), JsonOptions)!;
            StatisticsAccountVersions.Include(selected.Version, conflict.Local.Version);
            StatisticsAccountVersions.Include(selected.Version, conflict.Remote.Version);
            selected.Version.LegacyFingerprint = conflict.Remote.Version.LegacyFingerprint;
            StatisticsAccountVersions.Advance(selected.Version, _deviceId);
            StatisticsDocumentMerger.Put(document, selected);
            return document;
        }, StatisticsDocumentChangeSource.CloudSync, cancellationToken);
    }

    public Task<StatisticsDocument> AddAccountAsync(string uid)
    {
        uid = uid.Trim();
        return UpdateAsync(document =>
        {
            if (!document.Accounts.Any(account => string.Equals(account.Uid, uid, StringComparison.OrdinalIgnoreCase)))
            {
                document.Accounts.Add(new AccountStatisticsData { Uid = uid, SyncVersion = document.DeletedAccounts.GetValueOrDefault(uid) });
                document.DeletedAccounts.Remove(uid);
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
            return UpdateAccountAsync(account => TemporaryStatisticsRules.Record(
                account, Guid.NewGuid().ToString("N"), spiritName, spiritName, capturedAt, null),
                useActiveAccount: true, accountUid: accountUid);

        return UpdateAccountAsync(account =>
            StatisticsMutationRules.RecordEncounter(account, season, spiritName, capturedAt),
            useActiveAccount: true, accountUid: accountUid);
    }

    public async Task<string?> RecordTemporaryEncounterAsync(string accountUid, string eventId, string rawText,
        string exactName, DateTimeOffset detectedAt, string? previousBucketId = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accountUid);
        ArgumentException.ThrowIfNullOrWhiteSpace(eventId);
        if (previousBucketId is null && _seasonConfigService is not null
            && !EncounterSeasonTimeline.IsExpired(_seasonConfigService.Load(), DateOnly.FromDateTime(detectedAt.DateTime)))
            return null;
        string? bucketId = null;
        await UpdateAccountAsync(account => bucketId = TemporaryStatisticsRules.Record(
            account, eventId, rawText, exactName, detectedAt, previousBucketId),
            useActiveAccount: true, accountUid: accountUid);
        return bucketId;
    }

    public Task<StatisticsDocument> TransferTemporaryStatisticsAsync(IReadOnlyList<TemporaryStatisticsTransfer> transfers)
    {
        var seasons = _seasonConfigService?.Load().Seasons ?? [];
        return UpdateAsync(document =>
        {
            foreach (var transfer in transfers)
            {
                var season = seasons.FirstOrDefault(x => x.Id == transfer.SeasonId && x.Id != EncounterSeasonTimeline.PendingSeasonId)
                    ?? throw new InvalidOperationException("请选择软件已配置的正式赛季。");
                var account = document.Accounts.FirstOrDefault(x => x.Uid == transfer.AccountUid)
                    ?? throw new InvalidOperationException("统计账号已不存在。");
                TemporaryStatisticsRules.Transfer(account, transfer, season);
            }
            return document;
        });
    }

    public async Task<int> RefreshTemporaryNamesAsync(SpiritCatalogDocument catalog)
    {
        var index = new SpiritCatalogIndex(catalog);
        var count = 0;
        await UpdateAsync(document =>
        {
            count = MatchPendingNames(document, index, _seasonConfigService?.Load().SpiritNameMatchThreshold ?? 0.55);
            return document;
        });
        return count;
    }

    private static int MatchPendingNames(StatisticsDocument document, SpiritCatalogIndex index, double normalThreshold)
    {
        var count = 0;
        foreach (var account in document.Accounts)
        {
            foreach (var item in account.Temporary?.Encounters ?? [])
            {
                if (item.RawText.Length == 0) continue;
                var match = index.Match(item.RawText, 1);
                var name = match.Length == 0 ? string.Empty : index.ResolveEvolutionRecordName(match);
                if (item.Name == name) continue;
                item.Name = name;
                count++;
            }
            foreach (var capture in account.Seasons.Where(x => x.Id == EncounterSeasonTimeline.PendingSeasonId).SelectMany(x => x.ShinyCaptures))
            {
                var match = index.Match(capture.Name, 1);
                if (match.Length == 0) continue;
                var name = index.ResolveEvolutionRecordName(match);
                if (capture.Name == name) continue;
                capture.Name = name;
                count++;
            }
            foreach (var pending in account.PendingShinyCaptures.Where(x => x.HandledAt is null && x.Name.Length == 0))
            {
                var match = index.Match(pending.RawText, pending.Season == EncounterSeasonTimeline.PendingSeasonId ? 1 : normalThreshold);
                if (match.Length == 0) continue;
                pending.Name = index.ResolveEvolutionRecordName(match);
                count++;
            }
        }
        return count;
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
        string accountUid,
        string seasonId,
        string originalName,
        string nextName,
        int nextCount,
        DateTimeOffset editedAt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accountUid);
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
            StatisticsMutationRules.EditEncounter(account, seasonId, originalName, nextName, nextCount, editedAt),
            accountUid: accountUid);
    }

    public Task<StatisticsDocument> DeleteEncounterAsync(string accountUid, string seasonId, string spiritName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accountUid);
        seasonId = seasonId.Trim();
        spiritName = spiritName.Trim();
        if (string.IsNullOrWhiteSpace(seasonId) || string.IsNullOrWhiteSpace(spiritName))
        {
            return LoadAsync();
        }

        return UpdateAccountAsync(account =>
            StatisticsMutationRules.DeleteEncounter(account, seasonId, spiritName), accountUid: accountUid);
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

    public Task<StatisticsDocument> EditShinyCaptureAsync(
        string accountUid,
        string captureId,
        string nextName,
        int encounterCountBeforeCapture,
        DateTimeOffset capturedAt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accountUid);
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
                capturedAt), accountUid: accountUid);
    }

    public Task<StatisticsDocument> DeleteShinyCaptureAsync(string accountUid, string captureId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accountUid);
        captureId = captureId.Trim();
        if (string.IsNullOrWhiteSpace(captureId))
        {
            return LoadAsync();
        }

        return UpdateAccountAsync(account =>
            StatisticsMutationRules.DeleteShinyCapture(account, captureId), accountUid: accountUid);
    }

    public Task<StatisticsDocument> AddPendingShinyCaptureAsync(
        EncounterSeasonDefinition season,
        string spiritName,
        DateTimeOffset detectedAt,
        string? id = null,
        string? rawText = null,
        string? accountUid = null, bool updateOnly = false)
    {
        spiritName = spiritName.Trim();
        if (string.IsNullOrWhiteSpace(season.Id))
        {
            return LoadAsync();
        }

        season = ResolveRecordingSeason(season, detectedAt);
        var eventId = string.IsNullOrWhiteSpace(id) ? Guid.NewGuid().ToString("N") : id.Trim();
        return UpdateAccountAsync(account =>
        {
            if (updateOnly && !account.PendingShinyCaptures.Any(x => x.Id == eventId)) return;
            StatisticsMutationRules.AddPendingShinyCapture(account, season, spiritName, detectedAt, eventId, rawText?.Trim() ?? string.Empty);
        },
            useActiveAccount: true, accountUid: accountUid);
    }

    public Task<StatisticsDocument> ConfirmPendingShinyCaptureAsync(
        string pendingCaptureId,
        string spiritName,
        int? encounterCount,
        DateTimeOffset confirmedAt)
    {
        pendingCaptureId = pendingCaptureId.Trim();
        spiritName = spiritName.Trim();
        encounterCount = encounterCount is { } count ? Math.Max(0, count) : null;
        if (string.IsNullOrWhiteSpace(pendingCaptureId)
            || string.IsNullOrWhiteSpace(spiritName))
        {
            return LoadAsync();
        }

        return UpdateAccountAsync(account =>
        {
            StatisticsMutationRules.ConfirmPendingShinyCapture(
                account,
                pendingCaptureId,
                spiritName,
                encounterCount,
                confirmedAt);
        });
    }

    public Task<StatisticsDocument> DiscardPendingShinyCaptureAsync(string pendingCaptureId)
    {
        pendingCaptureId = pendingCaptureId.Trim();
        if (string.IsNullOrWhiteSpace(pendingCaptureId))
        {
            return LoadAsync();
        }

        return UpdateAccountAsync(account =>
        {
            StatisticsMutationRules.DiscardPendingShinyCapture(account, pendingCaptureId);
        });
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
            .Where(record => record.HandledAt is null && !string.IsNullOrWhiteSpace(record.Id)
                && !string.IsNullOrWhiteSpace(record.Season))
            .Select(record => new PendingShinyCaptureRecord
            {
                Id = record.Id.Trim(),
                Name = record.Name.Trim(),
                RawText = record.RawText,
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
            var currentJson = JsonSerializer.Serialize(_document, JsonOptions);
            var workingDocument = JsonSerializer.Deserialize<StatisticsDocument>(currentJson, JsonOptions)!;
            var nextDocument = StatisticsDocumentMerger.Prepare(update(workingDocument));
            if (source == StatisticsDocumentChangeSource.Local)
            {
                // 导入、清空和删除共用一条路径，删除标记不会随备份替换而丢失。
                foreach (var previous in StatisticsDocumentMerger.Snapshots(_document))
                {
                    var next = StatisticsDocumentMerger.Find(nextDocument, previous.Uid);
                    if (next is null)
                    {
                        var version = new StatisticsAccountVersion { LegacyFingerprint = previous.Version.LegacyFingerprint };
                        StatisticsAccountVersions.Include(version, previous.Version);
                        nextDocument.DeletedAccounts[previous.Uid] = version;
                    }
                    else StatisticsAccountVersions.Include(next.Version, previous.Version);
                }
                foreach (var next in StatisticsDocumentMerger.Snapshots(nextDocument))
                {
                    var previous = StatisticsDocumentMerger.Find(_document, next.Uid);
                    if (StatisticsDocumentMerger.Fingerprint(next) != StatisticsDocumentMerger.Fingerprint(previous))
                        StatisticsAccountVersions.Advance(next.Version, _deviceId);
                }
                StatisticsDocumentNormalizer.Normalize(nextDocument);
            }
            cancellationToken.ThrowIfCancellationRequested();
            // 复用快照序列化结果比较实际变化，空操作不写盘、不触发刷新或自动上传。
            var nextJson = JsonSerializer.Serialize(nextDocument, JsonOptions);
            if (string.Equals(currentJson, nextJson, StringComparison.Ordinal)) return nextDocument;

            await _localSettingsService.SaveSettingAsync(SettingsKeys.StatisticsData, nextDocument);
            Volatile.Write(ref _document, nextDocument);
            selectionChanged = ReconcileAccountSelection(nextDocument);
            changedDocument = JsonSerializer.Deserialize<StatisticsDocument>(nextJson, JsonOptions)!;
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
            _deviceId = await _localSettingsService.ReadSettingAsync<string>(SettingsKeys.StatisticsDeviceId) ?? string.Empty;
            if (_deviceId.Length == 0)
            {
                _deviceId = Guid.NewGuid().ToString("N");
                await _localSettingsService.SaveSettingAsync(SettingsKeys.StatisticsDeviceId, _deviceId);
            }
            var savedDocument = await _localSettingsService.ReadSettingAsync<StatisticsDocument>(SettingsKeys.StatisticsData);
            var before = JsonSerializer.Serialize(savedDocument, JsonOptions);
            var legacySettings = savedDocument is not null && savedDocument.Info?.Version != StatisticsDocumentFormats.CurrentVersion
                ? await _localSettingsService.ReadSettingAsync<StatisticsSyncSettings>(SettingsKeys.StatisticsSyncSettings) : null;
            var document = StatisticsDocumentMerger.Prepare(savedDocument ?? StatisticsDocumentNormalizer.CreateDefault(),
                legacySettings?.LastSyncedAccountFingerprints);
            var fingerprints = StatisticsDocumentMerger.ComputeAccountFingerprints(document);
            if (_spiritCatalogService is not null && document.Accounts.Any(x => x.Temporary is not null || x.PendingShinyCaptures.Count > 0))
            {
                try { MatchPendingNames(document, new SpiritCatalogIndex(await _spiritCatalogService.LoadAsync()), _seasonConfigService?.Load().SpiritNameMatchThreshold ?? 0.55); }
                catch (Exception ex) { _logger.LogWarning(ex, "暂存名称刷新失败，保留数据等待重试。"); }
            }
            foreach (var account in document.Accounts.Where(x => fingerprints.GetValueOrDefault(x.Uid)
                != StatisticsDocumentMerger.ComputeAccountFingerprint(x)))
                StatisticsAccountVersions.Advance(account.SyncVersion!, _deviceId);
            StatisticsDocumentNormalizer.Normalize(document);
            if (savedDocument is not null && before != JsonSerializer.Serialize(document, JsonOptions))
                await _localSettingsService.SaveSettingAsync(SettingsKeys.StatisticsData, document);
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

