namespace RocoPilot.Models.Hotkeys;

public sealed record HotkeyActionDescriptor(
    HotkeyAction Action,
    string Name,
    string Description,
    string Glyph)
{
    public static IReadOnlyList<HotkeyActionDescriptor> CreateDefault()
    {
        return
        [
            new(
                HotkeyAction.ToggleInfoOverlay,
                "信息遮罩窗口",
                "切换运行状态、计数和识别结果遮罩。",
                "\uE946"),
            new(
                HotkeyAction.ToggleRecognitionOverlay,
                "识别区域遮罩",
                "切换截图识别区域框遮罩。",
                "\uE890"),
            new(
                HotkeyAction.ToggleEncounterStatistics,
                "奇遇统计",
                "切换当前赛季奇遇统计识别。",
                "\uE9A3"),
            new(
                HotkeyAction.ToggleAutoBattle,
                "自动战斗",
                "切换战斗技能自动释放。",
                "\uF272")
        ];
    }
}
