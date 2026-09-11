using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using RocoPilot.Contracts.Services.Statistics;
using RocoPilot.Models.Statistics;
using static RocoPilot.Services.Statistics.Sync.StatisticsSyncRules;

namespace RocoPilot.Services.Statistics.Sync;

public sealed class S3StatisticsRemoteStore(HttpClient httpClient, TimeProvider? timeProvider = null)
    : IStatisticsRemoteStore, IDisposable
{
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;
    private const string S3Region = "auto";
    private const string S3Service = "s3";
    private const string S3Algorithm = "AWS4-HMAC-SHA256";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    public async Task<StatisticsSyncRemoteInfo> ReadInfoAsync(
        StatisticsSyncSettings settings,
        string password,
        CancellationToken cancellationToken)
    {
        using var request = CreateS3Request(settings, HttpMethod.Head, password, []);
        using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (response.IsSuccessStatusCode)
        {
            return new StatisticsSyncRemoteInfo
            {
                Exists = true,
                LastModifiedAt = ReadLastModified(response),
                ContentLength = response.Content.Headers.ContentLength,
                EntityTag = ReadEntityTag(response),
                CheckedAt = _timeProvider.GetUtcNow()
            };
        }

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return new StatisticsSyncRemoteInfo
            {
                Exists = false,
                CheckedAt = _timeProvider.GetUtcNow()
            };
        }

        await EnsureSuccessAsync(response, cancellationToken);
        return new StatisticsSyncRemoteInfo
        {
            Exists = false,
            CheckedAt = _timeProvider.GetUtcNow()
        };
    }

    public async Task<StatisticsRemoteDownload> DownloadAsync(
        StatisticsSyncSettings settings, string secret, CancellationToken cancellationToken)
    {
        using var request = CreateS3Request(settings, HttpMethod.Get, secret, []);
        using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
            throw new InvalidOperationException("云端还没有统计数据，请先上传一次。");
        await EnsureSuccessAsync(response, cancellationToken);
        var document = DeserializeDocument(await response.Content.ReadAsStringAsync(cancellationToken));
        return new StatisticsRemoteDownload(document, new StatisticsSyncRemoteInfo
        {
            Exists = true,
            LastModifiedAt = ReadLastModified(response),
            ContentLength = response.Content.Headers.ContentLength,
            EntityTag = ReadEntityTag(response),
            CheckedAt = _timeProvider.GetUtcNow()
        });
    }

    public async Task<StatisticsRemoteUpload> UploadAsync(StatisticsSyncSettings settings, string secret,
        StatisticsDocument document, StatisticsSyncRemoteInfo expectedVersion, CancellationToken cancellationToken)
    {
        var payload = Encoding.UTF8.GetBytes(SerializeDocument(document));
        using var request = CreateS3Request(settings, HttpMethod.Put, secret, payload, expectedVersion);
        using var response = await httpClient.SendAsync(request, cancellationToken);
        if (response.StatusCode == HttpStatusCode.PreconditionFailed) return new StatisticsRemoteUpload(true);
        await EnsureSuccessAsync(response, cancellationToken);
        var completedAt = _timeProvider.GetUtcNow();
        var result = new StatisticsSyncResult
        {
            CompletedAt = completedAt,
            RemoteLastModifiedAt = ReadLastModified(response) ?? completedAt,
            ContentLength = payload.LongLength,
            EntityTag = ReadEntityTag(response)
        };
        if (string.IsNullOrWhiteSpace(result.EntityTag))
            throw new InvalidOperationException("云端已接收上传，但没有返回用于并发校验的 ETag；本地未记录本次同步基线，下次将重新检查云端。");
        return new StatisticsRemoteUpload(false, result);
    }

    public void Dispose() => httpClient.Dispose();

    private HttpRequestMessage CreateS3Request(
        StatisticsSyncSettings settings,
        HttpMethod method,
        string secretAccessKey,
        byte[] payload,
        StatisticsSyncRemoteInfo? expectedRemoteInfo = null)
    {
        var uri = BuildS3ObjectUri(settings);
        var request = new HttpRequestMessage(method, uri);
        try
        {
            if (method == HttpMethod.Put)
            {
                request.Content = new ByteArrayContent(payload);
                request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json")
                {
                    CharSet = Encoding.UTF8.WebName
                };
            }

            request.Headers.UserAgent.ParseAdd("RocoPilot");
            if (method == HttpMethod.Put && expectedRemoteInfo is not null)
            {
                ApplyUploadCondition(request, expectedRemoteInfo);
            }

            SignS3Request(request, settings.UserName, secretAccessKey, payload);
            return request;
        }
        catch
        {
            request.Dispose();
            throw;
        }
    }

    private static void ApplyUploadCondition(
        HttpRequestMessage request,
        StatisticsSyncRemoteInfo expectedRemoteInfo)
    {
        if (!expectedRemoteInfo.Exists)
        {
            request.Headers.IfNoneMatch.Add(EntityTagHeaderValue.Any);
            return;
        }

        var entityTag = NormalizeEntityTag(expectedRemoteInfo.EntityTag);
        if (!string.IsNullOrWhiteSpace(entityTag))
        {
            request.Headers.IfMatch.Add(new EntityTagHeaderValue($"\"{entityTag}\""));
            return;
        }

        if (expectedRemoteInfo.LastModifiedAt is not null)
        {
            request.Headers.IfUnmodifiedSince = expectedRemoteInfo.LastModifiedAt.Value.ToUniversalTime();
            return;
        }

        throw new InvalidOperationException("云端没有返回 ETag 或更新时间，无法安全地执行覆盖上传。");
    }

    private void SignS3Request(
        HttpRequestMessage request,
        string accessKeyId,
        string secretAccessKey,
        byte[] payload)
    {
        var now = _timeProvider.GetUtcNow();
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

    private string SerializeDocument(StatisticsDocument sourceDocument)
    {
        var json = JsonSerializer.Serialize(sourceDocument, JsonOptions);
        var document = JsonSerializer.Deserialize<StatisticsDocument>(json, JsonOptions) ?? new StatisticsDocument();
        document.Info = new StatisticsDocumentInfo
        {
            Format = StatisticsDocumentFormats.RocoPilotStatistics,
            Version = StatisticsDocumentFormats.CurrentVersion,
            ExportApp = "RocoPilot",
            ExportedAt = _timeProvider.GetUtcNow()
        };

        return JsonSerializer.Serialize(document, JsonOptions);
    }

    private static DateTimeOffset? ReadLastModified(HttpResponseMessage response)
    {
        return response.Content.Headers.LastModified
            ?? response.Headers.Date;
    }

    private static string? ReadEntityTag(HttpResponseMessage response)
    {
        var tag = response.Headers.ETag?.Tag;
        if (string.IsNullOrWhiteSpace(tag)
            && response.Headers.TryGetValues("ETag", out var values))
        {
            tag = values.FirstOrDefault();
        }

        return NormalizeEntityTag(tag);
    }
}
