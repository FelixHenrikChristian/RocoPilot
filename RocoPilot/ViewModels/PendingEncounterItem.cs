namespace RocoPilot.ViewModels;

public sealed record PendingEncounterItem(string AccountUid, string Id, string RawText, string Season, DateTimeOffset DetectedAt)
{
    public string DisplayName => $"{Season} · {DetectedAt.ToLocalTime():MM-dd HH:mm:ss} · {RawTextDisplay}";
    public string RawTextDisplay => string.IsNullOrWhiteSpace(RawText) ? "未读取到文字" : RawText;
    public string ContextDisplay => $"账号 {AccountUid} · {Season} · {DetectedAt.ToLocalTime():yyyy-MM-dd HH:mm:ss}";
}
