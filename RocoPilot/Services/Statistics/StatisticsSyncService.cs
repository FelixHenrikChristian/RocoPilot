using Microsoft.Extensions.Logging;

using RocoPilot.Configuration;
using RocoPilot.Contracts.Services;
using RocoPilot.Contracts.Services.Statistics;
using RocoPilot.Models.Statistics;
using RocoPilot.Services.Statistics.Sync;

using static RocoPilot.Services.Statistics.Sync.StatisticsSyncRules;

namespace RocoPilot.Services.Statistics;

public sealed class StatisticsSyncService : IStatisticsSyncService
{
    private const int MaxConditionalUploadAttempts = 3;
    private static readonly TimeSpan AutoUploadDelay = TimeSpan.FromSeconds(8);

    private readonly ILocalSettingsService _localSettingsService;
    private readonly IStatisticsService _statisticsService;
    private readonly ILogger<StatisticsSyncService> _logger;
    private readonly IStatisticsRemoteStore _remoteStore;
    private readonly IStatisticsSyncCredentialStore _credentials;
    private readonly SemaphoreSlim _operationGate = new(1, 1);
    private readonly SemaphoreSlim _settingsGate = new(1, 1);
    private readonly StatisticsAutoUploadScheduler _autoUpload;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly object _disposeGate = new();
    private readonly object _statusGate = new();
    private Task? _disposeTask;
    private bool _stopping;

    private StatisticsSyncSettings _settings = CreateDefaultSettings();
    private StatisticsSyncStatus _status = new();
    private bool _isSettingsLoaded;

    public event EventHandler<StatisticsSyncStatusChangedEventArgs>? StatusChanged;

    public StatisticsSyncStatus CurrentStatus
    {
        get { lock (_statusGate) return CloneStatus(_status); }
    }

    public StatisticsSyncService(
        ILocalSettingsService localSettingsService,
        IStatisticsService statisticsService,
        ILogger<StatisticsSyncService> logger,
        IStatisticsRemoteStore remoteStore,
        IStatisticsSyncCredentialStore credentials)
        : this(localSettingsService, statisticsService, logger, remoteStore, credentials, Task.Delay)
    {
    }

    internal StatisticsSyncService(
        ILocalSettingsService localSettingsService,
        IStatisticsService statisticsService,
        ILogger<StatisticsSyncService> logger,
        IStatisticsRemoteStore remoteStore,
        IStatisticsSyncCredentialStore credentials,
        Func<TimeSpan, CancellationToken, Task> delay)
    {
        _localSettingsService = localSettingsService;
        _statisticsService = statisticsService;
        _logger = logger;
        _remoteStore = remoteStore;
        _credentials = credentials;
        _autoUpload = new StatisticsAutoUploadScheduler(AutoUploadDelay,
            token => UploadAsync(automatic: true, token),
            ex => _logger.LogWarning(ex, "自动上传统计数据失败。"), delay);
        _statisticsService.DocumentChanged += StatisticsService_DocumentChanged;
        ApplyStatusFromSettings(_settings, "未配置云同步");
    }

    public IReadOnlyList<StatisticsSyncProviderOption> GetProviders()
    {
        return GetProviderOptions();
    }

    public async Task<StatisticsSyncSettings> LoadSettingsAsync(CancellationToken cancellationToken = default)
    {
        using var operation = CreateOperationCancellation(cancellationToken);
        cancellationToken = operation.Token;
        cancellationToken.ThrowIfCancellationRequested();
        return CloneSettings(await LoadSettingsCoreAsync(cancellationToken));
    }

    public async Task<StatisticsSyncStatus> LoadStatusAsync(CancellationToken cancellationToken = default)
    {
        using var operation = CreateOperationCancellation(cancellationToken);
        cancellationToken = operation.Token;
        var settings = await LoadSettingsCoreAsync(cancellationToken);
        ApplyStatusFromSettings(settings, BuildIdleMessage(settings), onlyWhenIdle: true);
        return CurrentStatus;
    }

    public async Task<StatisticsSyncStatus> SaveSettingsAsync(
        StatisticsSyncSettings settings,
        string? password,
        CancellationToken cancellationToken = default)
    {
        using var operation = CreateOperationCancellation(cancellationToken);
        cancellationToken = operation.Token;
        await _operationGate.WaitAsync(cancellationToken);
        try
        {
            var currentSettings = await LoadSettingsCoreAsync(cancellationToken);
            var normalizedSettings = NormalizeSettings(settings);
            if (AreSameRemoteTarget(currentSettings, normalizedSettings))
            {
                CopySyncMetadata(currentSettings, normalizedSettings);
            }
            else
            {
                ClearSyncMetadata(normalizedSettings);
            }

            ValidateSettingsForSave(normalizedSettings, password);
            if (!string.IsNullOrEmpty(password))
            {
                _credentials.Save(normalizedSettings.UserName, password);
            }

            await SaveSettingsCoreAsync(normalizedSettings, cancellationToken);
            if (!normalizedSettings.IsEnabled) _autoUpload.CancelPending();
            ApplyStatusFromSettings(normalizedSettings, normalizedSettings.IsEnabled ? "云同步设置已保存" : "云同步未启用");
            return CurrentStatus;
        }
        finally
        {
            _operationGate.Release();
        }
    }

    private void ValidateSettingsForSave(StatisticsSyncSettings settings, string? password)
    {
        if (!settings.IsEnabled)
        {
            return;
        }

        if (!HasRequiredSettings(settings))
        {
            throw new InvalidOperationException("启用云同步前，请先填写 Account ID、Bucket 和 Access Key ID。");
        }

        if (string.IsNullOrWhiteSpace(password) && string.IsNullOrEmpty(_credentials.Read(settings.UserName)))
        {
            throw new InvalidOperationException("启用云同步前，请先填写 Secret Access Key。");
        }
    }

    public async Task<StatisticsSyncRemoteInfo> RefreshRemoteInfoAsync(CancellationToken cancellationToken = default)
    {
        using var operation = CreateOperationCancellation(cancellationToken);
        cancellationToken = operation.Token;
        await _operationGate.WaitAsync(cancellationToken);
        try
        {
            var (settings, password) = await LoadConfiguredSettingsAsync(cancellationToken);
            SetBusy(true, "正在读取云端时间");
            var info = await _remoteStore.ReadInfoAsync(settings, password, cancellationToken);
            await SaveRemoteInfoAsync(settings, info, cancellationToken);
            ApplyStatusFromSettings(settings, info.Exists ? "已更新云端时间" : "云端暂无统计数据");
            return info;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            SetFailureStatus("读取云端时间失败", ex);
            throw;
        }
        finally
        {
            SetBusy(false);
            _operationGate.Release();
        }
    }

    public async Task<StatisticsSyncResult> TestConnectionAsync(CancellationToken cancellationToken = default)
    {
        using var operation = CreateOperationCancellation(cancellationToken);
        cancellationToken = operation.Token;
        await _operationGate.WaitAsync(cancellationToken);
        try
        {
            var (settings, password) = await LoadConfiguredSettingsAsync(cancellationToken);
            SetBusy(true, "正在测试云同步连接");
            var info = await _remoteStore.ReadInfoAsync(settings, password, cancellationToken);
            await SaveRemoteInfoAsync(settings, info, cancellationToken);
            var result = new StatisticsSyncResult
            {
                CompletedAt = DateTimeOffset.Now,
                RemoteLastModifiedAt = info.LastModifiedAt,
                ContentLength = info.ContentLength,
                EntityTag = info.EntityTag
            };

            ApplyStatusFromSettings(settings, info.Exists ? "连接成功，已读取云端文件" : "连接成功，云端暂无统计数据");
            return result;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            SetFailureStatus("云同步连接失败", ex);
            throw;
        }
        finally
        {
            SetBusy(false);
            _operationGate.Release();
        }
    }

    public async Task<bool> DownloadRemoteChangesIfNeededAsync(CancellationToken cancellationToken = default)
    {
        using var operation = CreateOperationCancellation(cancellationToken);
        cancellationToken = operation.Token;
        await _operationGate.WaitAsync(cancellationToken);
        try
        {
            var settings = await LoadSettingsCoreAsync(cancellationToken);
            if (!settings.IsEnabled || !HasRequiredSettings(settings))
            {
                ApplyStatusFromSettings(settings, BuildIdleMessage(settings));
                return false;
            }

            var (_, password) = await LoadConfiguredSettingsAsync(cancellationToken);
            SetBusy(true, "正在检查云端统计更新");
            var remoteInfo = await _remoteStore.ReadInfoAsync(settings, password, cancellationToken);
            await SaveRemoteInfoAsync(settings, remoteInfo, cancellationToken);
            if (!remoteInfo.Exists)
            {
                ApplyStatusFromSettings(settings, "云端暂无统计数据");
                return false;
            }

            if (!HasRemoteChangedSinceLastSync(settings, remoteInfo))
            {
                ApplyStatusFromSettings(settings, "云端统计数据已是最新");
                return false;
            }

            SetBusy(true, "检测到云端更新，正在先同步到本地");
            await DownloadAndMergeCoreAsync(
                settings,
                password,
                "启动时已同步云端统计更新",
                cancellationToken);
            return true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            SetFailureStatus("检查云端统计更新失败", ex);
            throw;
        }
        finally
        {
            SetBusy(false);
            _operationGate.Release();
        }
    }

    public async Task<StatisticsSyncResult> UploadAsync(CancellationToken cancellationToken = default)
    {
        return await UploadAsync(automatic: false, cancellationToken);
    }

    public async Task<StatisticsSyncResult> DownloadAsync(CancellationToken cancellationToken = default)
    {
        using var operation = CreateOperationCancellation(cancellationToken);
        cancellationToken = operation.Token;
        await _operationGate.WaitAsync(cancellationToken);
        try
        {
            var (settings, password) = await LoadConfiguredSettingsAsync(cancellationToken);
            SetBusy(true, "正在合并云端统计数据");
            return await DownloadAndMergeCoreAsync(
                settings,
                password,
                "已合并云端统计数据",
                cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            SetFailureStatus("合并云端统计失败", ex);
            throw;
        }
        finally
        {
            SetBusy(false);
            _operationGate.Release();
        }
    }

    private async Task<StatisticsSyncResult> DownloadAndMergeCoreAsync(
        StatisticsSyncSettings settings,
        string password,
        string successMessage,
        CancellationToken cancellationToken)
    {
        var downloaded = await _remoteStore.DownloadAsync(settings, password, cancellationToken);
        var remoteDocument = downloaded.Document;
        var downloadedRemoteInfo = downloaded.Info;
        var mergeResult = await _statisticsService.MergeRemoteAsync(
            remoteDocument,
            settings.LastSyncedAccountFingerprints,
            preferRemoteAccountsWithoutBaseline:
                settings.LastSyncedAccountFingerprints is null
                && HasRecordedSyncVersion(settings)
                && HasRemoteChangedSinceLastSync(settings, downloadedRemoteInfo),
            cancellationToken);
        if (mergeResult.ConflictingAccountUids.Count > 0)
        {
            _logger.LogWarning(
                "云同步检测到同一账号在本地和云端都发生变化，已采用云端版本：{AccountUids}",
                string.Join(", ", mergeResult.ConflictingAccountUids));
        }

        var completedAt = DateTimeOffset.Now;
        var result = new StatisticsSyncResult
        {
            CompletedAt = completedAt,
            RemoteLastModifiedAt = downloadedRemoteInfo.LastModifiedAt,
            ContentLength = downloadedRemoteInfo.ContentLength,
            EntityTag = downloadedRemoteInfo.EntityTag
        };

        settings.LastDownloadedAt = completedAt;
        settings.LastRemoteCheckedAt = completedAt;
        settings.LastRemoteModifiedAt = result.RemoteLastModifiedAt;
        settings.LastRemoteEntityTag = result.EntityTag;
        settings.LastSyncedRemoteModifiedAt = result.RemoteLastModifiedAt;
        settings.LastSyncedRemoteEntityTag = result.EntityTag;
        settings.LastSyncedAccountFingerprints = StatisticsDocumentMerger.ComputeAccountFingerprints(remoteDocument);
        await SaveSettingsCoreAsync(settings, cancellationToken);
        ApplyStatusFromSettings(settings, successMessage);
        return result;
    }

    private async Task<StatisticsSyncResult> UploadAsync(bool automatic, CancellationToken cancellationToken)
    {
        using var operation = CreateOperationCancellation(cancellationToken);
        cancellationToken = operation.Token;
        await _operationGate.WaitAsync(cancellationToken);
        try
        {
            if (automatic)
            {
                var automaticSettings = await LoadSettingsCoreAsync(cancellationToken);
                if (!automaticSettings.IsEnabled || !HasRequiredSettings(automaticSettings))
                    return new StatisticsSyncResult();
            }
            var (settings, password) = await LoadConfiguredSettingsAsync(cancellationToken);
            var mergedRemoteChanges = false;
            for (var attempt = 1; attempt <= MaxConditionalUploadAttempts; attempt++)
            {
                SetBusy(true, automatic ? "正在自动上传统计数据" : "正在上传统计数据");
                var currentRemoteInfo = await _remoteStore.ReadInfoAsync(settings, password, cancellationToken);
                if (HasRemoteChangedSinceLastSync(settings, currentRemoteInfo))
                {
                    SetBusy(true, "检测到云端更新，正在先同步到本地");
                    var downloadResult = await DownloadAndMergeCoreAsync(
                        settings,
                        password,
                        "已先同步云端统计更新",
                        cancellationToken);
                    mergedRemoteChanges = true;
                    currentRemoteInfo = new StatisticsSyncRemoteInfo
                    {
                        Exists = true,
                        LastModifiedAt = downloadResult.RemoteLastModifiedAt,
                        ContentLength = downloadResult.ContentLength,
                        EntityTag = downloadResult.EntityTag,
                        CheckedAt = downloadResult.CompletedAt
                    };
                }

                var document = await _statisticsService.LoadAsync();
                var upload = await _remoteStore.UploadAsync(settings, password, document, currentRemoteInfo, cancellationToken);
                if (upload.HasConflict)
                {
                    if (attempt < MaxConditionalUploadAttempts)
                    {
                        SetBusy(true, "上传期间云端再次更新，正在重新同步");
                        continue;
                    }

                    throw new InvalidOperationException("上传期间云端数据连续发生变化，已停止上传以避免覆盖，请稍后重试。");
                }

                var result = upload.Result ?? throw new InvalidOperationException("云端未返回上传结果。");
                var completedAt = result.CompletedAt;

                settings.LastUploadedAt = completedAt;
                settings.LastRemoteCheckedAt = completedAt;
                settings.LastRemoteModifiedAt = result.RemoteLastModifiedAt;
                settings.LastRemoteEntityTag = result.EntityTag;
                settings.LastSyncedRemoteModifiedAt = result.RemoteLastModifiedAt;
                settings.LastSyncedRemoteEntityTag = result.EntityTag;
                settings.LastSyncedAccountFingerprints = StatisticsDocumentMerger.ComputeAccountFingerprints(document);
                await SaveSettingsCoreAsync(settings, cancellationToken);
                var successMessage = mergedRemoteChanges
                    ? "已先同步云端更新并上传统计数据"
                    : automatic
                        ? "已自动上传统计数据"
                        : "已上传统计数据";
                ApplyStatusFromSettings(settings, successMessage);
                return result;
            }

            throw new InvalidOperationException("云端统计数据持续变化，已停止上传以避免覆盖。");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            SetFailureStatus(automatic ? "自动上传统计失败" : "上传统计失败", ex);
            throw;
        }
        finally
        {
            SetBusy(false);
            _operationGate.Release();
        }
    }

    private void StatisticsService_DocumentChanged(object? sender, StatisticsDocumentChangedEventArgs e)
    {
        if (e.Source == StatisticsDocumentChangeSource.Local) _autoUpload.Request();
    }

    private async Task<(StatisticsSyncSettings Settings, string Password)> LoadConfiguredSettingsAsync(CancellationToken cancellationToken)
    {
        var settings = await LoadSettingsCoreAsync(cancellationToken);
        if (!settings.IsEnabled)
        {
            throw new InvalidOperationException("请先启用云同步。");
        }

        if (!HasRequiredSettings(settings))
        {
            throw new InvalidOperationException("云同步配置不完整，请先补全当前同步方式需要的字段。");
        }

        if (!IsS3Provider(settings))
        {
            throw new NotSupportedException($"暂不支持 {settings.ProviderKind} 同步方式。");
        }

        var password = _credentials.Read(settings.UserName);
        if (string.IsNullOrEmpty(password))
        {
            throw new InvalidOperationException("未保存 Secret Access Key，请在云同步设置中重新输入。");
        }

        cancellationToken.ThrowIfCancellationRequested();
        return (settings, password);
    }

    private async Task<StatisticsSyncSettings> LoadSettingsCoreAsync(CancellationToken cancellationToken)
    {
        await _settingsGate.WaitAsync(cancellationToken);
        try
        {
            if (!_isSettingsLoaded)
            {
                var savedSettings = await _localSettingsService.ReadSettingAsync<StatisticsSyncSettings>(SettingsKeys.StatisticsSyncSettings);
                cancellationToken.ThrowIfCancellationRequested();
                _settings = NormalizeSettings(savedSettings ?? CreateDefaultSettings());
                _isSettingsLoaded = true;
            }
            return CloneSettings(_settings);
        }
        finally
        {
            _settingsGate.Release();
        }
    }

    private async Task SaveSettingsCoreAsync(StatisticsSyncSettings settings, CancellationToken cancellationToken)
    {
        await _settingsGate.WaitAsync(cancellationToken);
        try
        {
            var nextSettings = NormalizeSettings(settings);
            await _localSettingsService.SaveSettingAsync(SettingsKeys.StatisticsSyncSettings, nextSettings);
            _settings = nextSettings;
            _isSettingsLoaded = true;
        }
        finally
        {
            _settingsGate.Release();
        }
    }

    private async Task SaveRemoteInfoAsync(
        StatisticsSyncSettings settings,
        StatisticsSyncRemoteInfo info,
        CancellationToken cancellationToken)
    {
        settings.LastRemoteCheckedAt = info.CheckedAt;
        settings.LastRemoteModifiedAt = info.LastModifiedAt;
        settings.LastRemoteEntityTag = info.EntityTag;
        await SaveSettingsCoreAsync(settings, cancellationToken);
    }

    private void ApplyStatusFromSettings(StatisticsSyncSettings settings, string message, bool onlyWhenIdle = false)
    {
        StatisticsSyncStatus snapshot;
        lock (_statusGate)
        {
            if (onlyWhenIdle && _status.IsBusy) return;
            var provider = ResolveProvider(settings.ProviderId);
            _status = new StatisticsSyncStatus
            {
                IsConfigured = HasRequiredSettings(settings), IsEnabled = settings.IsEnabled,
                IsBusy = _status.IsBusy, ProviderId = settings.ProviderId, ProviderName = provider.Name,
                Message = message, RemoteLastModifiedAt = settings.LastRemoteModifiedAt,
                LastUploadedAt = settings.LastUploadedAt, LastDownloadedAt = settings.LastDownloadedAt,
                LastRemoteCheckedAt = settings.LastRemoteCheckedAt, RemoteEntityTag = settings.LastRemoteEntityTag
            };
            snapshot = CloneStatus(_status);
        }
        StatusChanged?.Invoke(this, new StatisticsSyncStatusChangedEventArgs(snapshot));
    }

    private void SetBusy(bool isBusy, string? message = null)
    {
        StatisticsSyncStatus snapshot;
        lock (_statusGate)
        {
            _status.IsBusy = isBusy;
            if (!string.IsNullOrWhiteSpace(message)) _status.Message = message;
            snapshot = CloneStatus(_status);
        }
        StatusChanged?.Invoke(this, new StatisticsSyncStatusChangedEventArgs(snapshot));
    }

    private void SetFailureStatus(string title, Exception exception)
    {
        _logger.LogWarning(exception, "{Title}", title);
        SetBusy(false, $"{title}：{exception.Message}");
    }

    private CancellationTokenSource CreateOperationCancellation(CancellationToken cancellationToken)
    {
        lock (_disposeGate)
        {
            if (_stopping) throw new OperationCanceledException("云同步服务正在关闭。", cancellationToken);
            return CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdown.Token);
        }
    }

    public ValueTask DisposeAsync()
    {
        lock (_disposeGate)
        {
            _stopping = true;
            _statisticsService.DocumentChanged -= StatisticsService_DocumentChanged;
            return new ValueTask(_disposeTask ??= DisposeCoreAsync());
        }
    }

    private async Task DisposeCoreAsync()
    {
        try
        {
            try { await _shutdown.CancelAsync(); }
            finally { await _autoUpload.DisposeAsync(); }
        }
        finally
        {
            // 已接收的手动操作与配置读写也必须退出，关闭后不再留下写入任务。
            await _operationGate.WaitAsync();
            try
            {
                await _settingsGate.WaitAsync();
                _settingsGate.Release();
            }
            finally
            {
                _operationGate.Release();
                _shutdown.Dispose();
            }
        }
    }
}
