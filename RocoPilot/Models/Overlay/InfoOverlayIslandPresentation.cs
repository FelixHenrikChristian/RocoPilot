namespace RocoPilot.Models.Overlay;

public sealed record InfoOverlayIslandPresentation(string Category, string Title, string Description,
    string CreatureName = "", bool IsWarning = false, bool IsError = false)
{
    public static InfoOverlayIslandPresentation? Resolve(InfoOverlaySnapshot snapshot, InfoOverlayNotice? uidNotice, DateTimeOffset now)
    {
        if (snapshot.IsShinyProtectionActive)
        {
            var protectedCreatureName = snapshot.PendingShinyCapture?.CreatureName ?? snapshot.BattleCreatureName;
            return new("异色保护", "自动操作已暂停",
                uidNotice is not null ? $"{uidNotice.Title}：{uidNotice.Message}"
                    : snapshot.PendingShinyCapture is { } detected ? $"{detected.CreatureName} · 请在统计页面确认，本场暂停自动操作"
                    : string.IsNullOrWhiteSpace(protectedCreatureName) ? "本场战斗停止自动按键，退出战斗后恢复"
                    : $"精灵：{protectedCreatureName} · 本场停止自动按键，退出战斗后恢复",
                protectedCreatureName, IsWarning: true);
        }
        if (uidNotice is not null)
            return new("统计账号", uidNotice.Title, uidNotice.Message, IsWarning: true);
        // 未确认记录由顶部标记常驻提醒，不占用下一场战斗的操作详情。
        if (snapshot.Activity is not { } activity || activity.ExpiresAt <= now)
        {
            if (snapshot.Scene == InfoOverlayScene.Unknown && snapshot.MainStatusText == "识别异常")
                return new("状态识别", "识别异常", snapshot.StatusText, IsError: true);
            // 主要场景常驻顶部，小流程留在展开区域，不把“技能选择”当作主标题。
            var flow = snapshot.Scene == InfoOverlayScene.Battle ? snapshot.StatusText switch
            {
                "战斗中 - 技能选择" => "技能选择",
                "战斗中 - 切换精灵" => "切换精灵",
                _ => null
            } : null;
            var creatureName = snapshot.BattleCreatureName;
            return flow is null ? null : new("战斗流程", flow,
                string.IsNullOrWhiteSpace(creatureName) ? string.Empty : $"精灵：{creatureName}", creatureName);
        }
        var category = activity.Kind switch
        {
            InfoOverlayActivityKind.Spirit => "精灵识别", InfoOverlayActivityKind.Record => "统计记录",
            InfoOverlayActivityKind.Error => "操作未完成",
            InfoOverlayActivityKind.Skill or InfoOverlayActivityKind.EnergyRecovery => "技能选择",
            InfoOverlayActivityKind.PetSwitch => "战斗流程",
            InfoOverlayActivityKind.Capture => "战斗操作", _ => "当前流程"
        };
        var description = string.IsNullOrWhiteSpace(activity.CreatureName)
            || activity.Kind is InfoOverlayActivityKind.Spirit or InfoOverlayActivityKind.Record
            ? activity.Description
            : string.Join(" · ", new[] { $"精灵：{activity.CreatureName}", activity.Description }
                .Where(text => !string.IsNullOrWhiteSpace(text)));
        return new(category, activity.Title, description, activity.CreatureName, IsError: activity.Kind == InfoOverlayActivityKind.Error);
    }
}
