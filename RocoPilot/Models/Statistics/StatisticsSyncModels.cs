namespace RocoPilot.Models.Statistics;

public static class StatisticsSyncProviderKinds
{
    public const string S3 = "s3";
}

public sealed class StatisticsSyncProviderOption
{
    public string Id { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public string Kind { get; set; } = StatisticsSyncProviderKinds.S3;

    public string DefaultEndpoint { get; set; } = string.Empty;

    public string DefaultRemotePath { get; set; } = string.Empty;
}

public sealed class StatisticsSyncSettings
{
    public bool IsEnabled { get; set; }

    public string ProviderId { get; set; } = string.Empty;

    public string ProviderKind { get; set; } = StatisticsSyncProviderKinds.S3;

    public string Endpoint { get; set; } = string.Empty;

    public string RemotePath { get; set; } = string.Empty;

    public string BucketName { get; set; } = string.Empty;

    public string UserName { get; set; } = string.Empty;

    public DateTimeOffset? LastUploadedAt { get; set; }

    public DateTimeOffset? LastDownloadedAt { get; set; }

    public DateTimeOffset? LastRemoteCheckedAt { get; set; }

    public DateTimeOffset? LastRemoteModifiedAt { get; set; }
}

public sealed class StatisticsSyncStatus
{
    public bool IsConfigured { get; set; }

    public bool IsEnabled { get; set; }

    public bool IsBusy { get; set; }

    public string ProviderId { get; set; } = string.Empty;

    public string ProviderName { get; set; } = "未配置";

    public string Message { get; set; } = "未配置云同步";

    public DateTimeOffset? RemoteLastModifiedAt { get; set; }

    public DateTimeOffset? LastUploadedAt { get; set; }

    public DateTimeOffset? LastDownloadedAt { get; set; }

    public DateTimeOffset? LastRemoteCheckedAt { get; set; }
}

public sealed class StatisticsSyncRemoteInfo
{
    public bool Exists { get; set; }

    public DateTimeOffset? LastModifiedAt { get; set; }

    public long? ContentLength { get; set; }

    public DateTimeOffset CheckedAt { get; set; } = DateTimeOffset.Now;
}

public sealed class StatisticsSyncResult
{
    public DateTimeOffset CompletedAt { get; set; } = DateTimeOffset.Now;

    public DateTimeOffset? RemoteLastModifiedAt { get; set; }

    public long? ContentLength { get; set; }
}
