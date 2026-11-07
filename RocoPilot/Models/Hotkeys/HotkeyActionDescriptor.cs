namespace RocoPilot.Models.Hotkeys;

public sealed record HotkeyActionDescriptor(
    HotkeyAction Action,
    string Name,
    string Description,
    string GlyphResourceKey)
{
    public static IReadOnlyList<HotkeyActionDescriptor> CreateDefault()
    {
        return
        [
            new(
                HotkeyAction.TakeScreenshot,
                "截图",
                "截取游戏画面，保存到安装目录的 Screenshots 文件夹。",
                "ScreenshotGlyph"),
            new(
                HotkeyAction.ToggleInfoOverlay,
                "信息遮罩窗口",
                "切换运行状态、计数和识别结果遮罩。",
                "OverlayInfoGlyph"),
            new(
                HotkeyAction.ToggleEncounterStatistics,
                "奇遇统计",
                "切换当前赛季奇遇统计识别。",
                "EncounterCountGlyph"),
            new(
                HotkeyAction.ToggleAutoBattle,
                "自动战斗",
                "切换战斗技能自动释放。",
                "AutoBattleGlyph")
        ];
    }
}
