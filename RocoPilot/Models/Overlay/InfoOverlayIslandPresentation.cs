namespace RocoPilot.Models.Overlay;

public sealed record InfoOverlayIslandPresentation(string Category, string Title, string Description,
    string CreatureName = "", bool IsWarning = false, bool IsError = false)
{
    public static string BattleInputTitle(AutoBattlePlan plan) => plan.Action switch
    {
        AutoBattleAction.Capture => "捕捉",
        AutoBattleAction.EnergyRecovery => "回能",
        AutoBattleAction.Skill when plan.FallbackSequence is null => "公共序列",
        _ => plan.DisplayKey is "1" or "2" or "3" or "4" ? $"技能 {plan.DisplayKey}"
            : plan.DisplayKey == "X" ? "回能" : "公共序列"
    };

    public static InfoOverlayIslandPresentation? Resolve(InfoOverlaySnapshot snapshot, InfoOverlayNotice? uidNotice, DateTimeOffset now)
    {
        // 展开区三行语义固定：Category=当前步骤（小字），Title=具体内容（大字），Description=对象 · 结果（小字）。
        if (!string.IsNullOrWhiteSpace(snapshot.IndependentTaskName) && snapshot.IndependentTaskProgress is { } progress)
            return progress.IsBattleWaiting ? null
                // 顶栏已显示任务名，步骤行不再重复。
                : new(progress.Stage == snapshot.IndependentTaskName ? "当前步骤" : progress.Stage,
                    progress.Operation, progress.Recognition, progress.CreatureName);
        if (snapshot.IsShinyProtectionActive)
        {
            var protectedCreatureName = snapshot.PendingShinyCapture?.CreatureName ?? snapshot.BattleCreatureName;
            return new("异色保护", "自动操作已暂停",
                uidNotice is not null ? $"{uidNotice.Title}：{uidNotice.Message}"
                    : snapshot.PendingShinyCapture is { } detected ? $"{detected.CreatureName} · 请在统计页面确认"
                    : Join(protectedCreatureName, "退出战斗后恢复"),
                protectedCreatureName, IsWarning: true);
        }
        if (uidNotice is not null)
            return new("统计账号", uidNotice.Title, uidNotice.Message, IsWarning: true);
        // 未确认记录由顶部标记常驻提醒，不占用下一场战斗的操作详情。
        // 没有实际动作（仅识别出名称、空闲流程）时保持收起。
        if (snapshot.Activity is not { } activity || activity.ExpiresAt <= now
            || activity.Kind == InfoOverlayActivityKind.Spirit)
            return snapshot.Scene == InfoOverlayScene.Unknown && snapshot.MainStatusText == "识别异常"
                ? new("状态识别", "识别异常", snapshot.StatusText, IsError: true) : null;
        var isError = activity.Kind == InfoOverlayActivityKind.Error;
        var isSwitch = activity.Kind == InfoOverlayActivityKind.PetSwitch;
        var step = activity.Kind switch
        {
            InfoOverlayActivityKind.Skill => "释放技能",
            InfoOverlayActivityKind.EnergyRecovery or InfoOverlayActivityKind.Capture => "战斗操作",
            InfoOverlayActivityKind.PetSwitch => "切换精灵",
            InfoOverlayActivityKind.Record => "奇遇记录",
            // 任务完成、任务停止和截图热键都走这一类，步骤行不预设成败。
            InfoOverlayActivityKind.Task => "操作结果",
            InfoOverlayActivityKind.Error => "操作失败",
            _ => "当前操作"
        };
        var result = activity.Kind is InfoOverlayActivityKind.Skill or InfoOverlayActivityKind.EnergyRecovery
            or InfoOverlayActivityKind.Capture ? activity.ExpiresAt.HasValue ? "已发送" : "执行中" : string.Empty;
        // 对手名称与己方换宠、报错原因无关，不放进这两类的结果行。
        var creatureName = isError || isSwitch ? string.Empty : activity.CreatureName;
        // 战斗中的动作行附带已识别的血脉；未识别（入场过渡）时快照不填血脉，不占位。
        var bloodline = snapshot.Scene == InfoOverlayScene.Battle && !string.IsNullOrWhiteSpace(snapshot.BattleBloodline)
            ? $"血脉：{snapshot.BattleBloodline}"
            : string.Empty;
        return new(step, activity.Title, Join(creatureName, bloodline, activity.Description, result), creatureName, IsError: isError);
    }

    private static string Join(params string?[] parts) => string.Join(" · ", parts.Where(text => !string.IsNullOrWhiteSpace(text)));
}
