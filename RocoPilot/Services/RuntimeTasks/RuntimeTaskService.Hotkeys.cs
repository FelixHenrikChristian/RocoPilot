using Microsoft.Extensions.Logging;

using RocoPilot.Models.Hotkeys;
using RocoPilot.Models.Runtime;

namespace RocoPilot.Services;

public sealed partial class RuntimeTaskService
{
    private void HotkeyService_HotkeyTriggered(object? sender, HotkeyTriggeredEventArgs e)
    {
        ApplyHotkeyAction(e.Action);
    }

    private void ApplyHotkeyAction(HotkeyAction action)
    {
        switch (action)
        {
            case HotkeyAction.ToggleInfoOverlay:
                ToggleInfoOverlayByHotkey();
                break;
            case HotkeyAction.ToggleEncounterStatistics:
                ToggleEncounterStatisticsByHotkey();
                break;
            case HotkeyAction.ToggleAutoBattle:
                ToggleAutoBattleByHotkey();
                break;
        }
    }

    private void ToggleInfoOverlayByHotkey()
    {
        if (CurrentState is not { } state)
        {
            _logger.LogInformation("热键：实时任务未运行，信息遮罩窗口开关已忽略。");
            return;
        }

        var nextValue = !state.Options.InfoOverlayEnabled;
        SetInfoOverlayEnabled(nextValue);
        _logger.LogInformation("热键：{State}信息遮罩窗口。", FormatEnabledState(nextValue));
    }

    private void ToggleEncounterStatisticsByHotkey()
    {
        var nextValue = !EncounterStatisticsEnabled;
        SetEncounterStatisticsEnabled(nextValue);
        _logger.LogInformation("热键：{State}奇遇统计。", FormatEnabledState(nextValue));
    }

    private void ToggleAutoBattleByHotkey()
    {
        var settings = _autoBattleSettings.Clone();
        settings.IsEnabled = !settings.IsEnabled;
        SetAutoBattleSettings(settings);
        _logger.LogInformation("热键：{State}自动战斗。", FormatEnabledState(settings.IsEnabled));
    }

    private static string FormatEnabledState(bool isEnabled)
    {
        return isEnabled ? "已开启" : "已关闭";
    }
}
