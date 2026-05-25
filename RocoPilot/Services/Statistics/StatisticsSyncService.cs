using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

using Microsoft.Extensions.Logging;

using RocoPilot.Configuration;
using RocoPilot.Contracts.Services;
using RocoPilot.Contracts.Services.Statistics;
using RocoPilot.Models.Statistics;

using Windows.Security.Credentials;

namespace RocoPilot.Services.Statistics;

public sealed class StatisticsSyncService : IStatisticsSyncService
{
    private const string CloudflareR2ProviderId = "cloudflare-r2";
    private const string CredentialResource = "RocoPilot.StatisticsSync";
    private const string S3Region = "auto";
    private const string S3Service = "s3";
    private const string S3Algorithm = "AWS4-HMAC-SHA256";
    private static readonly TimeSpan AutoUploadDelay = TimeSpan.FromSeconds(8);

    private static readonly IReadOnlyList<StatisticsSyncProviderOption> ProviderOptions =
    [
        new()
        {
            Id = CloudflareR2ProviderId,
            Name = "Cloudflare R2",
            Kind = StatisticsSyncProviderKinds.S3,
            DefaultEndpoint = string.Empty,
            DefaultRemotePath = "RocoPilot/statistics.json"
        }
    ];

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    private readonly ILocalSettingsService _localSettingsService;
    private readonly IStatisticsService _statisticsService;
    private readonly ILogger<StatisticsSyncService> _logger;
    private readonly HttpClient _httpClient = CreateHttpClient();
    private readonly SemaphoreSlim _operationGate = new(1, 1);
    private readonly object _autoUploadLock = new();

    private StatisticsSyncSettings _settings = CreateDefaultSettings();
    private StatisticsSyncStatus _status = new();
    private CancellationTokenSource? _autoUploadCts;
    private bool _isSettingsLoaded;
    private bool _suspendAutoUpload;

    public event EventHandler<StatisticsSyncStatusChangedEventArgs>? StatusChanged;

    public StatisticsSyncStatus CurrentStatus => CloneStatus(_status);

    public StatisticsSyncService(
        ILocalSettingsService localSettingsService,
        IStatisticsService statisticsService,
        ILogger<StatisticsSyncService> logger)
    {
        _localSettingsService = localSettingsService;
        _statisticsService = statisticsService;
        _logger = logger;
        _statisticsService.DocumentChanged += StatisticsService_DocumentChanged;
        ApplyStatusFromSettings(_settings, "未配置云同步");
    }

    public IReadOnlyList<StatisticsSyncProviderOption> GetProviders()
    {
        return ProviderOptions.Select(CloneProvider).ToList();
    }

    public async Task<StatisticsSyncSettings> LoadSettingsAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return CloneSettings(await LoadSettingsCoreAsync());
    }

    public async Task<StatisticsSyncStatus> LoadStatusAsync(CancellationToken cancellationToken = default)
    {
        var settings = await LoadSettingsCoreAsync();
        ApplyStatusFromSettings(settings, BuildIdleMessage(settings));
        return CloneStatus(_status);
    }

    public async Task<StatisticsSyncStatus> SaveSettingsAsync(
        StatisticsSyncSettings settings,
        string? password,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var normalizedSettings = NormalizeSettings(settings);
        ValidateSettingsForSave(normalizedSettings, password);
        if (!string.IsNullOrEmpty(password))
        {
            SavePassword(normalizedSettings.UserName, password);
        }

        await SaveSettingsCoreAsync(normalizedSettings, cancellationToken);
        ApplyStatusFromSettings(normalizedSettings, normalizedSettings.IsEnabled ? "云同步设置已保存" : "云同步未启用");
        return CloneStatus(_status);
    }

    private static void ValidateSettingsForSave(StatisticsSyncSettings settings, string? password)
    {
        if (!settings.IsEnabled)
        {
            return;
        }

        if (!HasRequiredSettings(settings))
        {
            throw new InvalidOperationException("启用云同步前，请先填写 Account ID、Bucket 和 Access Key ID。");
        }

        if (string.IsNullOrWhiteSpace(password) && string.IsNullOrEmpty(ReadPassword(settings.UserName)))
        {
            throw new InvalidOperationException("启用云同步前，请先填写 Secret Access Key。");
        }
    }

    public async Task<StatisticsSyncRemoteInfo> RefreshRemoteInfoAsync(CancellationToken cancellationToken = default)
    {
        await _operationGate.WaitAsync(cancellationToken);
        try
        {
            var (settings, password) = await LoadConfiguredSettingsAsync(cancellationToken);
            SetBusy(true, "正在读取云端时间");
            var info = await ReadRemoteInfoCoreAsync(settings, password, cancellationToken);
            await SaveRemoteInfoAsync(settings, info, cancellationToken);
            ApplyStatusFromSettings(settings, info.Exists ? "已更新云端时间" : "云端暂无统计数据");
            return info;
        }
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
        await _operationGate.WaitAsync(cancellationToken);
        try
        {
            var (settings, password) = await LoadConfiguredSettingsAsync(cancellationToken);
            SetBusy(true, "正在测试云同步连接");
            var info = await ReadRemoteInfoCoreAsync(settings, password, cancellationToken);
            await SaveRemoteInfoAsync(settings, info, cancellationToken);
            var result = new StatisticsSyncResult
            {
                CompletedAt = DateTimeOffset.Now,
                RemoteLastModifiedAt = info.LastModifiedAt,
                ContentLength = info.ContentLength
            };

            ApplyStatusFromSettings(settings, info.Exists ? "连接成功，已读取云端文件" : "连接成功，云端暂无统计数据");
            return result;
        }
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

    public async Task<StatisticsSyncResult> UploadAsync(CancellationToken cancellationToken = default)
    {
        return await UploadAsync(automatic: false, cancellationToken);
    }

    public async Task<StatisticsSyncResult> DownloadAsync(CancellationToken cancellationToken = default)
    {
        await _operationGate.WaitAsync(cancellationToken);
        try
        {
            var (settings, password) = await LoadConfiguredSettingsAsync(cancellationToken);
            SetBusy(true, "正在下载云端统计数据");
            using var response = await SendDownloadRequestAsync(settings, password, cancellationToken);
            var json = await response.Content.ReadAsStringAsync(cancellationToken);
            var document = DeserializeDocument(json);

            _suspendAutoUpload = true;
            try
            {
                await _statisticsService.ReplaceAsync(document);
            }
            finally
            {
                _suspendAutoUpload = false;
            }

            var completedAt = DateTimeOffset.Now;
            var result = new StatisticsSyncResult
            {
                CompletedAt = completedAt,
                RemoteLastModifiedAt = ReadLastModified(response),
                ContentLength = response.Content.Headers.ContentLength
            };

            settings.LastDownloadedAt = completedAt;
            settings.LastRemoteCheckedAt = completedAt;
            settings.LastRemoteModifiedAt = result.RemoteLastModifiedAt;
            await SaveSettingsCoreAsync(settings, cancellationToken);
            ApplyStatusFromSettings(settings, "已下载云端统计数据");
            return result;
        }
        catch (Exception ex)
        {
            SetFailureStatus("下载云端统计失败", ex);
            throw;
        }
        finally
        {
            SetBusy(false);
            _operationGate.Release();
        }
    }

    private async Task<StatisticsSyncResult> UploadAsync(bool automatic, CancellationToken cancellationToken)
    {
        await _operationGate.WaitAsync(cancellationToken);
        try
        {
            var (settings, password) = await LoadConfiguredSettingsAsync(cancellationToken);
            SetBusy(true, automatic ? "正在自动上传统计数据" : "正在上传统计数据");
            var document = await _statisticsService.LoadAsync();
            var json = SerializeDocument(document);
            using var response = await SendUploadRequestAsync(settings, password, json, cancellationToken);
            await EnsureSuccessAsync(response, cancellationToken);

            var completedAt = DateTimeOffset.Now;
            var info = await ReadRemoteInfoCoreAsync(settings, password, cancellationToken);
            var result = new StatisticsSyncResult
            {
                CompletedAt = completedAt,
                RemoteLastModifiedAt = info.LastModifiedAt ?? ReadLastModified(response) ?? completedAt,
                ContentLength = info.ContentLength
            };

            settings.LastUploadedAt = completedAt;
            settings.LastRemoteCheckedAt = completedAt;
            settings.LastRemoteModifiedAt = result.RemoteLastModifiedAt;
            await SaveSettingsCoreAsync(settings, cancellationToken);
            ApplyStatusFromSettings(settings, automatic ? "已自动上传统计数据" : "已上传统计数据");
            return result;
        }
        catch (Exception ex)
        {
            SetFailureStatus(automatic ? "自动上传统计失败" : "上传统计失败", ex);
            if (!automatic)
            {
                throw;
            }

            _logger.LogWarning(ex, "自动上传统计数据失败。");
            return new StatisticsSyncResult { CompletedAt = DateTimeOffset.Now };
        }
        finally
        {
            SetBusy(false);
            _operationGate.Release();
        }
    }

    private async void StatisticsService_DocumentChanged(object? sender, StatisticsDocumentChangedEventArgs e)
    {
        if (_suspendAutoUpload)
        {
            return;
        }

        try
        {
            var settings = await LoadSettingsCoreAsync();
            if (!settings.IsEnabled || !HasRequiredSettings(settings))
            {
                return;
            }

            QueueAutoUpload();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "准备自动上传统计数据失败。");
        }
    }

    private void QueueAutoUpload()
    {
        CancellationToken token;
        lock (_autoUploadLock)
        {
            _autoUploadCts?.Cancel();
            _autoUploadCts?.Dispose();
            _autoUploadCts = new CancellationTokenSource();
            token = _autoUploadCts.Token;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(AutoUploadDelay, token);
                await UploadAsync(automatic: true, token);
            }
            catch (OperationCanceledException)
            {
            }
        }, token);
    }

    private async Task<(StatisticsSyncSettings Settings, string Password)> LoadConfiguredSettingsAsync(CancellationToken cancellationToken)
    {
        var settings = await LoadSettingsCoreAsync();
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

        var password = ReadPassword(settings.UserName);
        if (string.IsNullOrEmpty(password))
        {
            throw new InvalidOperationException("未保存 Secret Access Key，请在云同步设置中重新输入。");
        }

        cancellationToken.ThrowIfCancellationRequested();
        return (settings, password);
    }

    private async Task<StatisticsSyncSettings> LoadSettingsCoreAsync()
    {
        if (_isSettingsLoaded)
        {
            return CloneSettings(_settings);
        }

        var savedSettings = await _localSettingsService.ReadSettingAsync<StatisticsSyncSettings>(SettingsKeys.StatisticsSyncSettings);
        _settings = NormalizeSettings(savedSettings ?? CreateDefaultSettings());
        _isSettingsLoaded = true;
        return CloneSettings(_settings);
    }

    private async Task SaveSettingsCoreAsync(StatisticsSyncSettings settings, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _settings = NormalizeSettings(settings);
        _isSettingsLoaded = true;
        await _localSettingsService.SaveSettingAsync(SettingsKeys.StatisticsSyncSettings, _settings);
    }

    private async Task SaveRemoteInfoAsync(
        StatisticsSyncSettings settings,
        StatisticsSyncRemoteInfo info,
        CancellationToken cancellationToken)
    {
        settings.LastRemoteCheckedAt = info.CheckedAt;
        settings.LastRemoteModifiedAt = info.LastModifiedAt;
        await SaveSettingsCoreAsync(settings, cancellationToken);
    }

    private async Task<StatisticsSyncRemoteInfo> ReadRemoteInfoCoreAsync(
        StatisticsSyncSettings settings,
        string password,
        CancellationToken cancellationToken)
    {
        using var request = CreateS3Request(settings, HttpMethod.Head, password, []);
        using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (response.IsSuccessStatusCode)
        {
            return new StatisticsSyncRemoteInfo
            {
                Exists = true,
                LastModifiedAt = ReadLastModified(response),
                ContentLength = response.Content.Headers.ContentLength,
                CheckedAt = DateTimeOffset.Now
            };
        }

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return new StatisticsSyncRemoteInfo
            {
                Exists = false,
                CheckedAt = DateTimeOffset.Now
            };
        }

        await EnsureSuccessAsync(response, cancellationToken);
        return new StatisticsSyncRemoteInfo
        {
            Exists = false,
            CheckedAt = DateTimeOffset.Now
        };
    }

    private async Task<HttpResponseMessage> SendDownloadRequestAsync(
        StatisticsSyncSettings settings,
        string password,
        CancellationToken cancellationToken)
    {
        using var request = CreateS3Request(settings, HttpMethod.Get, password, []);
        var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            response.Dispose();
            throw new InvalidOperationException("云端还没有统计数据，请先上传一次。");
        }

        await EnsureSuccessAsync(response, cancellationToken);
        return response;
    }

    private async Task<HttpResponseMessage> SendUploadRequestAsync(
        StatisticsSyncSettings settings,
        string password,
        string json,
        CancellationToken cancellationToken)
    {
        using var request = CreateS3Request(settings, HttpMethod.Put, password, Encoding.UTF8.GetBytes(json));
        return await _httpClient.SendAsync(request, cancellationToken);
    }

    private static HttpRequestMessage CreateS3Request(
        StatisticsSyncSettings settings,
        HttpMethod method,
        string secretAccessKey,
        byte[] payload)
    {
        var uri = BuildS3ObjectUri(settings);
        var request = new HttpRequestMessage(method, uri);
        if (method == HttpMethod.Put)
        {
            request.Content = new ByteArrayContent(payload);
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json")
            {
                CharSet = Encoding.UTF8.WebName
            };
        }

        request.Headers.UserAgent.ParseAdd("RocoPilot");
        SignS3Request(request, settings.UserName, secretAccessKey, payload);
        return request;
    }

    private static void SignS3Request(
        HttpRequestMessage request,
        string accessKeyId,
        string secretAccessKey,
        byte[] payload)
    {
        var now = DateTimeOffset.UtcNow;
        var amzDate = now.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);
        var dateStamp = now.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
        var credentialScope = $"{dateStamp}/{S3Region}/{S3Service}/aws4_request";
        var payloadHash = ComputeSha256Hex(payload);
        var host = BuildCanonicalHost(request.RequestUri!);

        request.Headers.Host = host;
        request.Headers.TryAddWithoutValidation("x-amz-content-sha256", payloadHash);
        request.Headers.TryAddWithoutValidation("x-amz-date", amzDate);

        const string signedHeaders = "host;x-amz-content-sha256;x-amz-date";
        var canonicalHeaders =
            $"host:{host}\n" +
            $"x-amz-content-sha256:{payloadHash}\n" +
            $"x-amz-date:{amzDate}\n";
        var canonicalRequest = string.Join('\n',
            request.Method.Method,
            request.RequestUri!.AbsolutePath,
            string.Empty,
            canonicalHeaders,
            signedHeaders,
            payloadHash);
        var stringToSign = string.Join('\n',
            S3Algorithm,
            amzDate,
            credentialScope,
            ComputeSha256Hex(Encoding.UTF8.GetBytes(canonicalRequest)));
        var signingKey = BuildS3SigningKey(secretAccessKey, dateStamp);
        var signature = ToHexString(HmacSha256(signingKey, stringToSign));
        request.Headers.TryAddWithoutValidation(
            "Authorization",
            $"{S3Algorithm} Credential={accessKeyId.Trim()}/{credentialScope}, SignedHeaders={signedHeaders}, Signature={signature}");
    }

    private static Uri BuildS3ObjectUri(StatisticsSyncSettings settings)
    {
        var endpoint = BuildS3Endpoint(settings.Endpoint);
        var bucketName = Uri.EscapeDataString(settings.BucketName.Trim());
        var objectKey = string.Join("/", SplitRemotePath(settings.RemotePath).Select(Uri.EscapeDataString));
        return new Uri(endpoint, $"{bucketName}/{objectKey}");
    }

    private static Uri BuildS3Endpoint(string accountIdOrEndpoint)
    {
        var endpoint = accountIdOrEndpoint.Trim();
        if (!endpoint.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            && !endpoint.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            endpoint = $"https://{endpoint}.r2.cloudflarestorage.com";
        }

        if (!endpoint.EndsWith("/", StringComparison.Ordinal))
        {
            endpoint += "/";
        }

        return new Uri(endpoint, UriKind.Absolute);
    }

    private static string BuildCanonicalHost(Uri uri)
    {
        return uri.IsDefaultPort ? uri.Host : $"{uri.Host}:{uri.Port}";
    }

    private static byte[] BuildS3SigningKey(string secretAccessKey, string dateStamp)
    {
        var dateKey = HmacSha256(Encoding.UTF8.GetBytes($"AWS4{secretAccessKey}"), dateStamp);
        var dateRegionKey = HmacSha256(dateKey, S3Region);
        var dateRegionServiceKey = HmacSha256(dateRegionKey, S3Service);
        return HmacSha256(dateRegionServiceKey, "aws4_request");
    }

    private static byte[] HmacSha256(byte[] key, string value)
    {
        return HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(value));
    }

    private static string ComputeSha256Hex(byte[] value)
    {
        return ToHexString(SHA256.HashData(value));
    }

    private static string ToHexString(byte[] value)
    {
        return Convert.ToHexString(value).ToLowerInvariant();
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var body = response.Content is null
            ? string.Empty
            : await response.Content.ReadAsStringAsync(cancellationToken);
        var message = $"云同步请求失败：HTTP {(int)response.StatusCode} {response.ReasonPhrase}";
        if (!string.IsNullOrWhiteSpace(body))
        {
            message += $"。{TrimErrorBody(body)}";
        }

        throw new InvalidOperationException(message);
    }

    private static string TrimErrorBody(string body)
    {
        body = body.Trim();
        return body.Length <= 180 ? body : body[..180];
    }

    private static StatisticsDocument DeserializeDocument(string json)
    {
        var document = JsonSerializer.Deserialize<StatisticsDocument>(json, JsonOptions)
            ?? throw new InvalidOperationException("云端统计文件为空或格式不正确。");

        if (!string.Equals(
                document.Info?.Format,
                StatisticsDocumentFormats.RocoPilotStatistics,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("云端文件不是 RocoPilot 统计数据。");
        }

        return document;
    }

    private static string SerializeDocument(StatisticsDocument sourceDocument)
    {
        var json = JsonSerializer.Serialize(sourceDocument, JsonOptions);
        var document = JsonSerializer.Deserialize<StatisticsDocument>(json, JsonOptions) ?? new StatisticsDocument();
        document.Info = new StatisticsDocumentInfo
        {
            Format = StatisticsDocumentFormats.RocoPilotStatistics,
            Version = StatisticsDocumentFormats.CurrentVersion,
            ExportApp = "RocoPilot",
            ExportedAt = DateTimeOffset.Now
        };

        return JsonSerializer.Serialize(document, JsonOptions);
    }

    private void SavePassword(string userName, string password)
    {
        if (string.IsNullOrWhiteSpace(userName) || string.IsNullOrWhiteSpace(password))
        {
            return;
        }

        var vault = new PasswordVault();
        foreach (var credential in FindCredentials(vault))
        {
            vault.Remove(credential);
        }

        vault.Add(new PasswordCredential(CredentialResource, userName.Trim(), password));
    }

    private static string? ReadPassword(string userName)
    {
        if (string.IsNullOrWhiteSpace(userName))
        {
            return null;
        }

        var vault = new PasswordVault();
        var credentials = FindCredentials(vault);
        var credential = credentials.FirstOrDefault(item =>
            string.Equals(item.UserName, userName.Trim(), StringComparison.OrdinalIgnoreCase));
        if (credential is null)
        {
            return null;
        }

        credential.RetrievePassword();
        return credential.Password;
    }

    private static IReadOnlyList<PasswordCredential> FindCredentials(PasswordVault vault)
    {
        try
        {
            return vault.FindAllByResource(CredentialResource);
        }
        catch
        {
            return [];
        }
    }

    private static HttpClient CreateHttpClient()
    {
        return new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(30)
        };
    }

    private void ApplyStatusFromSettings(StatisticsSyncSettings settings, string message)
    {
        var provider = ResolveProvider(settings.ProviderId);
        _status = new StatisticsSyncStatus
        {
            IsConfigured = HasRequiredSettings(settings),
            IsEnabled = settings.IsEnabled,
            IsBusy = _status.IsBusy,
            ProviderId = settings.ProviderId,
            ProviderName = provider.Name,
            Message = message,
            RemoteLastModifiedAt = settings.LastRemoteModifiedAt,
            LastUploadedAt = settings.LastUploadedAt,
            LastDownloadedAt = settings.LastDownloadedAt,
            LastRemoteCheckedAt = settings.LastRemoteCheckedAt
        };
        RaiseStatusChanged();
    }

    private void SetBusy(bool isBusy, string? message = null)
    {
        _status.IsBusy = isBusy;
        if (!string.IsNullOrWhiteSpace(message))
        {
            _status.Message = message;
        }

        RaiseStatusChanged();
    }

    private void SetFailureStatus(string title, Exception exception)
    {
        _logger.LogWarning(exception, "{Title}", title);
        _status.IsBusy = false;
        _status.Message = $"{title}：{exception.Message}";
        RaiseStatusChanged();
    }

    private void RaiseStatusChanged()
    {
        StatusChanged?.Invoke(this, new StatisticsSyncStatusChangedEventArgs(CloneStatus(_status)));
    }

    private static string BuildIdleMessage(StatisticsSyncSettings settings)
    {
        if (!settings.IsEnabled)
        {
            return "云同步未启用";
        }

        if (!HasRequiredSettings(settings))
        {
            return "云同步配置不完整";
        }

        return "云同步已启用";
    }

    private static bool HasRequiredSettings(StatisticsSyncSettings settings)
    {
        if (IsS3Provider(settings))
        {
            return !string.IsNullOrWhiteSpace(settings.Endpoint)
                && !string.IsNullOrWhiteSpace(settings.BucketName)
                && !string.IsNullOrWhiteSpace(settings.RemotePath)
                && !string.IsNullOrWhiteSpace(settings.UserName);
        }

        return !string.IsNullOrWhiteSpace(settings.Endpoint)
            && !string.IsNullOrWhiteSpace(settings.RemotePath)
            && !string.IsNullOrWhiteSpace(settings.UserName);
    }

    private static StatisticsSyncSettings NormalizeSettings(StatisticsSyncSettings settings)
    {
        var provider = ResolveProvider(settings.ProviderId);
        var isSameProvider = string.Equals(settings.ProviderId, provider.Id, StringComparison.OrdinalIgnoreCase);
        var endpoint = isSameProvider && !string.IsNullOrWhiteSpace(settings.Endpoint)
            ? settings.Endpoint.Trim()
            : provider.DefaultEndpoint;
        var remotePath = isSameProvider && !string.IsNullOrWhiteSpace(settings.RemotePath)
            ? NormalizeRemotePath(settings.RemotePath)
            : provider.DefaultRemotePath;
        var bucketName = isSameProvider
            ? settings.BucketName.Trim()
            : string.Empty;
        var userName = isSameProvider
            ? settings.UserName.Trim()
            : string.Empty;
        var lastUploadedAt = isSameProvider ? settings.LastUploadedAt : null;
        var lastDownloadedAt = isSameProvider ? settings.LastDownloadedAt : null;
        var lastRemoteCheckedAt = isSameProvider ? settings.LastRemoteCheckedAt : null;
        var lastRemoteModifiedAt = isSameProvider ? settings.LastRemoteModifiedAt : null;
        var isEnabled = isSameProvider && settings.IsEnabled;

        remotePath = string.IsNullOrWhiteSpace(remotePath)
            ? provider.DefaultRemotePath
            : NormalizeRemotePath(remotePath);

        return new StatisticsSyncSettings
        {
            IsEnabled = isEnabled,
            ProviderId = provider.Id,
            ProviderKind = provider.Kind,
            Endpoint = endpoint,
            RemotePath = remotePath,
            BucketName = bucketName,
            UserName = userName,
            LastUploadedAt = lastUploadedAt,
            LastDownloadedAt = lastDownloadedAt,
            LastRemoteCheckedAt = lastRemoteCheckedAt,
            LastRemoteModifiedAt = lastRemoteModifiedAt
        };
    }

    private static StatisticsSyncSettings CreateDefaultSettings()
    {
        var provider = ProviderOptions[0];
        return new StatisticsSyncSettings
        {
            IsEnabled = false,
            ProviderId = provider.Id,
            ProviderKind = provider.Kind,
            Endpoint = provider.DefaultEndpoint,
            RemotePath = provider.DefaultRemotePath,
            BucketName = string.Empty
        };
    }

    private static bool IsS3Provider(StatisticsSyncSettings settings)
    {
        return string.Equals(settings.ProviderKind, StatisticsSyncProviderKinds.S3, StringComparison.OrdinalIgnoreCase);
    }

    private static StatisticsSyncProviderOption ResolveProvider(string? providerId)
    {
        return ProviderOptions.FirstOrDefault(provider =>
            string.Equals(provider.Id, providerId, StringComparison.OrdinalIgnoreCase)) ?? ProviderOptions[0];
    }

    private static string NormalizeRemotePath(string path)
    {
        path = path.Replace('\\', '/').Trim();
        var endsWithSlash = path.EndsWith("/", StringComparison.Ordinal);
        path = string.Join("/", path.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        return endsWithSlash && path.Length > 0 ? $"{path}/" : path;
    }

    private static string[] SplitRemotePath(string path)
    {
        var normalizedPath = NormalizeRemotePath(path);
        return normalizedPath.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    private static DateTimeOffset? ReadLastModified(HttpResponseMessage response)
    {
        return response.Content.Headers.LastModified
            ?? response.Headers.Date;
    }

    private static StatisticsSyncProviderOption CloneProvider(StatisticsSyncProviderOption provider)
    {
        return new StatisticsSyncProviderOption
        {
            Id = provider.Id,
            Name = provider.Name,
            Kind = provider.Kind,
            DefaultEndpoint = provider.DefaultEndpoint,
            DefaultRemotePath = provider.DefaultRemotePath
        };
    }

    private static StatisticsSyncSettings CloneSettings(StatisticsSyncSettings settings)
    {
        return new StatisticsSyncSettings
        {
            IsEnabled = settings.IsEnabled,
            ProviderId = settings.ProviderId,
            ProviderKind = settings.ProviderKind,
            Endpoint = settings.Endpoint,
            RemotePath = settings.RemotePath,
            BucketName = settings.BucketName,
            UserName = settings.UserName,
            LastUploadedAt = settings.LastUploadedAt,
            LastDownloadedAt = settings.LastDownloadedAt,
            LastRemoteCheckedAt = settings.LastRemoteCheckedAt,
            LastRemoteModifiedAt = settings.LastRemoteModifiedAt
        };
    }

    private static StatisticsSyncStatus CloneStatus(StatisticsSyncStatus status)
    {
        return new StatisticsSyncStatus
        {
            IsConfigured = status.IsConfigured,
            IsEnabled = status.IsEnabled,
            IsBusy = status.IsBusy,
            ProviderId = status.ProviderId,
            ProviderName = status.ProviderName,
            Message = status.Message,
            RemoteLastModifiedAt = status.RemoteLastModifiedAt,
            LastUploadedAt = status.LastUploadedAt,
            LastDownloadedAt = status.LastDownloadedAt,
            LastRemoteCheckedAt = status.LastRemoteCheckedAt
        };
    }
}
