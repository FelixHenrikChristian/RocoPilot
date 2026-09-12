using RocoPilot.Services.Encounters;

namespace RocoPilot.ViewModels;

public sealed record PendingEncounterItem(string AccountUid, string Id, string RawText, string Season, DateTimeOffset DetectedAt, string? Name = null)
{
    public bool IsSeasonPending => Season == EncounterSeasonTimeline.PendingSeasonId;
    public string SeasonDisplay => IsSeasonPending ? EncounterSeasonTimeline.PendingSeasonName : Season;
    public string NameDisplay => string.IsNullOrWhiteSpace(Name) ? RawTextDisplay : Name;
    public string DisplayName => $"{SeasonDisplay} · {DetectedAt:MM-dd HH:mm:ss} · {NameDisplay}";
    public string RawTextDisplay => string.IsNullOrWhiteSpace(RawText) ? "未读取到文字" : RawText;
    public string ContextDisplay => $"账号 {AccountUid} · {SeasonDisplay} · {DetectedAt:yyyy-MM-dd HH:mm:ss}";
}
