using System.Text.Json.Serialization;

namespace RocoPilot.Models;

public sealed class UpdateSourceOptions
{
    public string ReleaseMetadataUrl { get; set; } = string.Empty;
}

public sealed class GitHubRelease
{
    [JsonPropertyName("tag_name")]
    public string TagName { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("body")]
    public string Body { get; set; } = string.Empty;

    [JsonPropertyName("published_at")]
    public DateTime PublishedAt { get; set; }

    [JsonPropertyName("html_url")]
    public string HtmlUrl { get; set; } = string.Empty;
}

public enum UpdateTrigger
{
    Auto,
    Manual,
}

public sealed class UpdateOption
{
    public UpdateTrigger Trigger { get; set; }
}

public enum UpdateCheckStatus
{
    UpToDate,
    UpdateAvailable,
    Failed,
}

public sealed class UpdateCheckResult
{
    public UpdateCheckStatus Status { get; init; }

    public GitHubRelease? Release { get; init; }

    public string Message { get; init; } = string.Empty;

    public static UpdateCheckResult UpToDate(string message) => new()
    {
        Status = UpdateCheckStatus.UpToDate,
        Message = message,
    };

    public static UpdateCheckResult UpdateAvailable(GitHubRelease release, string message) => new()
    {
        Status = UpdateCheckStatus.UpdateAvailable,
        Release = release,
        Message = message,
    };

    public static UpdateCheckResult Failed(string message) => new()
    {
        Status = UpdateCheckStatus.Failed,
        Message = message,
    };
}
