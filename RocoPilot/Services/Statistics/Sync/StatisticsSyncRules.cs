using RocoPilot.Models.Statistics;

namespace RocoPilot.Services.Statistics.Sync;

internal static class StatisticsSyncRules
{
    public static IReadOnlyList<StatisticsSyncProviderOption> GetProviderOptions() => ProviderOptions.Select(CloneProvider).ToList();

    private const string CloudflareR2ProviderId = "cloudflare-r2";

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

    public static string BuildIdleMessage(StatisticsSyncSettings settings)
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

    public static bool HasRequiredSettings(StatisticsSyncSettings settings)
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

    public static bool AreSameRemoteTarget(
        StatisticsSyncSettings left,
        StatisticsSyncSettings right)
    {
        return string.Equals(left.ProviderId, right.ProviderId, StringComparison.OrdinalIgnoreCase)
            && string.Equals(left.ProviderKind, right.ProviderKind, StringComparison.OrdinalIgnoreCase)
            && string.Equals(left.Endpoint.Trim(), right.Endpoint.Trim(), StringComparison.OrdinalIgnoreCase)
            && string.Equals(left.BucketName.Trim(), right.BucketName.Trim(), StringComparison.Ordinal)
            && string.Equals(
                NormalizeRemotePath(left.RemotePath),
                NormalizeRemotePath(right.RemotePath),
                StringComparison.Ordinal)
            && string.Equals(left.UserName.Trim(), right.UserName.Trim(), StringComparison.Ordinal);
    }

    public static void CopySyncMetadata(
        StatisticsSyncSettings source,
        StatisticsSyncSettings target)
    {
        target.LastUploadedAt = source.LastUploadedAt;
        target.LastDownloadedAt = source.LastDownloadedAt;
        target.LastRemoteCheckedAt = source.LastRemoteCheckedAt;
        target.LastRemoteModifiedAt = source.LastRemoteModifiedAt;
        target.LastRemoteEntityTag = source.LastRemoteEntityTag;
        target.LastSyncedRemoteModifiedAt = source.LastSyncedRemoteModifiedAt;
        target.LastSyncedRemoteEntityTag = source.LastSyncedRemoteEntityTag;
        target.LastSyncedAccountFingerprints = NormalizeAccountFingerprints(source.LastSyncedAccountFingerprints);
    }

    public static void ClearSyncMetadata(StatisticsSyncSettings settings)
    {
        settings.LastUploadedAt = null;
        settings.LastDownloadedAt = null;
        settings.LastRemoteCheckedAt = null;
        settings.LastRemoteModifiedAt = null;
        settings.LastRemoteEntityTag = null;
        settings.LastSyncedRemoteModifiedAt = null;
        settings.LastSyncedRemoteEntityTag = null;
        settings.LastSyncedAccountFingerprints = null;
    }

    public static Dictionary<string, string>? NormalizeAccountFingerprints(
        IReadOnlyDictionary<string, string>? fingerprints)
    {
        if (fingerprints is null)
        {
            return null;
        }

        var normalized = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (uid, fingerprint) in fingerprints)
        {
            if (!string.IsNullOrWhiteSpace(uid) && !string.IsNullOrWhiteSpace(fingerprint))
            {
                normalized[uid.Trim()] = fingerprint.Trim().ToLowerInvariant();
            }
        }

        return normalized;
    }

    public static StatisticsSyncSettings NormalizeSettings(StatisticsSyncSettings settings)
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
        var lastRemoteEntityTag = isSameProvider ? NormalizeEntityTag(settings.LastRemoteEntityTag) : null;
        var lastSyncedRemoteModifiedAt = isSameProvider ? settings.LastSyncedRemoteModifiedAt : null;
        var lastSyncedRemoteEntityTag = isSameProvider ? NormalizeEntityTag(settings.LastSyncedRemoteEntityTag) : null;
        var lastSyncedAccountFingerprints = isSameProvider
            ? NormalizeAccountFingerprints(settings.LastSyncedAccountFingerprints)
            : null;
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
            LastRemoteModifiedAt = lastRemoteModifiedAt,
            LastRemoteEntityTag = lastRemoteEntityTag,
            LastSyncedRemoteModifiedAt = lastSyncedRemoteModifiedAt,
            LastSyncedRemoteEntityTag = lastSyncedRemoteEntityTag,
            LastSyncedAccountFingerprints = lastSyncedAccountFingerprints
        };
    }

    public static StatisticsSyncSettings CreateDefaultSettings()
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

    public static bool IsS3Provider(StatisticsSyncSettings settings)
    {
        return string.Equals(settings.ProviderKind, StatisticsSyncProviderKinds.S3, StringComparison.OrdinalIgnoreCase);
    }

    public static StatisticsSyncProviderOption ResolveProvider(string? providerId)
    {
        return ProviderOptions.FirstOrDefault(provider =>
            string.Equals(provider.Id, providerId, StringComparison.OrdinalIgnoreCase)) ?? ProviderOptions[0];
    }

    public static string NormalizeRemotePath(string path)
    {
        path = path.Replace('\\', '/').Trim();
        var endsWithSlash = path.EndsWith("/", StringComparison.Ordinal);
        path = string.Join("/", path.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        return endsWithSlash && path.Length > 0 ? $"{path}/" : path;
    }

    public static string[] SplitRemotePath(string path)
    {
        var normalizedPath = NormalizeRemotePath(path);
        return normalizedPath.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    public static bool HasRemoteChangedSinceLastSync(
        StatisticsSyncSettings settings,
        StatisticsSyncRemoteInfo remoteInfo)
    {
        if (!remoteInfo.Exists)
        {
            return false;
        }

        var syncedEntityTag = NormalizeEntityTag(settings.LastSyncedRemoteEntityTag);
        var remoteEntityTag = NormalizeEntityTag(remoteInfo.EntityTag);
        if (!string.IsNullOrWhiteSpace(syncedEntityTag)
            && !string.IsNullOrWhiteSpace(remoteEntityTag))
        {
            return !string.Equals(syncedEntityTag, remoteEntityTag, StringComparison.Ordinal);
        }

        var syncedModifiedAt = settings.LastSyncedRemoteModifiedAt ?? ResolveLegacySyncedRemoteModifiedAt(settings);
        if (syncedModifiedAt is null || remoteInfo.LastModifiedAt is null)
        {
            return true;
        }

        return !AreSameRemoteTimestamp(syncedModifiedAt.Value, remoteInfo.LastModifiedAt.Value);
    }

    public static bool HasRecordedSyncVersion(StatisticsSyncSettings settings)
    {
        return !string.IsNullOrWhiteSpace(settings.LastSyncedRemoteEntityTag)
            || settings.LastSyncedRemoteModifiedAt is not null
            || settings.LastUploadedAt is not null
            || settings.LastDownloadedAt is not null;
    }

    public static DateTimeOffset? ResolveLegacySyncedRemoteModifiedAt(StatisticsSyncSettings settings)
    {
        var lastSyncAt = Max(settings.LastUploadedAt, settings.LastDownloadedAt);
        if (lastSyncAt is null || settings.LastRemoteModifiedAt is null)
        {
            return null;
        }

        return settings.LastRemoteCheckedAt is null
            || settings.LastRemoteCheckedAt.Value.ToUniversalTime() <= lastSyncAt.Value.ToUniversalTime().AddSeconds(1)
            ? settings.LastRemoteModifiedAt
            : null;
    }

    public static DateTimeOffset? Max(DateTimeOffset? left, DateTimeOffset? right)
    {
        if (left is null)
        {
            return right;
        }

        if (right is null)
        {
            return left;
        }

        return left >= right ? left : right;
    }

    public static bool AreSameRemoteTimestamp(DateTimeOffset left, DateTimeOffset right)
    {
        return Math.Abs((left.ToUniversalTime() - right.ToUniversalTime()).TotalSeconds) <= 1;
    }

    public static string? NormalizeEntityTag(string? entityTag)
    {
        entityTag = entityTag?.Trim();
        if (string.IsNullOrWhiteSpace(entityTag))
        {
            return null;
        }

        if (entityTag.StartsWith("W/", StringComparison.OrdinalIgnoreCase))
        {
            entityTag = entityTag[2..].Trim();
        }

        return entityTag.Length >= 2
            && entityTag.StartsWith('"')
            && entityTag.EndsWith('"')
                ? entityTag[1..^1]
                : entityTag;
    }

    public static StatisticsSyncProviderOption CloneProvider(StatisticsSyncProviderOption provider)
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

    public static StatisticsSyncSettings CloneSettings(StatisticsSyncSettings settings)
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
            LastRemoteModifiedAt = settings.LastRemoteModifiedAt,
            LastRemoteEntityTag = settings.LastRemoteEntityTag,
            LastSyncedRemoteModifiedAt = settings.LastSyncedRemoteModifiedAt,
            LastSyncedRemoteEntityTag = settings.LastSyncedRemoteEntityTag,
            LastSyncedAccountFingerprints = NormalizeAccountFingerprints(settings.LastSyncedAccountFingerprints)
        };
    }

    public static StatisticsSyncStatus CloneStatus(StatisticsSyncStatus status)
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
            LastRemoteCheckedAt = status.LastRemoteCheckedAt,
            RemoteEntityTag = status.RemoteEntityTag
        };
    }
}
