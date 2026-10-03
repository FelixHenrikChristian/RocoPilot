namespace RocoPilot.Models.Overlay;

public enum InfoOverlayScene { Unknown, World, Battle, Suspended }

public sealed record InfoOverlaySnapshot(
    string StatusText,
    IReadOnlyList<InfoOverlayCounter> Counters,
    DateTimeOffset UpdatedAt,
    int? MagicPointCount = null,
    int MagicPointMaximum = 6,
    InfoOverlayPendingShinyCapture? PendingShinyCapture = null,
    InfoOverlayActivity? Activity = null,
    bool IsShinyProtectionActive = false,
    long Revision = 0,
    DateTimeOffset? SessionStartedAt = null,
    InfoOverlayScene Scene = InfoOverlayScene.Unknown,
    bool IsAutoBattleEnabled = false,
    string BattleCreatureName = "")
{
    public string MainStatusText => IsShinyProtectionActive ? "异色保护" : Scene switch
    {
        InfoOverlayScene.World => "大世界",
        InfoOverlayScene.Battle => IsAutoBattleEnabled ? "自动战斗中" : "战斗中",
        InfoOverlayScene.Suspended => "任务已挂起",
        _ => StatusText == "未识别" ? "状态未识别"
            : StatusText.StartsWith("未找到 ", StringComparison.Ordinal) || StatusText == "魔力区域不在截图内"
                ? "识别异常" : "状态待识别"
    };

    public DateTimeOffset? LatestRecordUpdatedAt => Counters.Count == 0
        ? null
        : Counters.Max(counter => counter.LastCountedAt);

    public static InfoOverlaySnapshot CreateInitial(DateTimeOffset startedAt)
    {
        return new InfoOverlaySnapshot(
            "状态待识别",
            [],
            startedAt);
    }
}

public sealed record InfoOverlayPendingShinyCapture(
    string CreatureName,
    string Season,
    DateTimeOffset DetectedAt);

public sealed record InfoOverlayNotice(
    string Title,
    string Message);
