using Microsoft.Extensions.Logging;
using RocoPilot.Models.Capture;
using RocoPilot.Models.Hotkeys;
using RocoPilot.Models.Recognition;
using RocoPilot.Models.Runtime;
using RocoPilot.Services.Recognition;

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
            case HotkeyAction.TakeScreenshot:
                _ = Task.Run(TakeScreenshotAsync);
                break;
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

    private async Task TakeScreenshotAsync()
    {
        var state = CurrentState;
        CaptureTargetWindow? window = null;
        var method = state?.Options.CaptureMethod ?? CaptureMethod.WindowsGraphicsCapture;
        try
        {
            window = _gameWindowService.FindGameWindow()
                ?? throw new InvalidOperationException("未找到游戏窗口。");
            using var frame = _screenCaptureService.Capture(window, method)
                ?? throw new InvalidOperationException("未能捕获游戏画面。");
            RecognitionRegionImageHelper.TryGetClientAreaInCapturedFrame(frame, window,
                out var x, out var y, out var width, out var height);
            var bytes = await RecognitionRegionImageHelper.EncodePngAsync(frame,
                new RecognitionRegion { X = x, Y = y, Width = width, Height = height }, CancellationToken.None);
            var directory = Path.Combine(AppContext.BaseDirectory, "Screenshots");
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, $"RocoPilot_{frame.CapturedAt:yyyyMMdd_HHmmss_fffffff}.png");
            using (var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write))
                await file.WriteAsync(bytes);
            _logger.LogInformation("截图已保存：{Path}", path);
            ShowIndependentTaskResult("截图已保存", path);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "游戏截图保存失败。");
            ShowIndependentTaskResult("截图保存失败", ex.Message);
        }
        finally
        {
            if (state is null && window is not null) _screenCaptureService.Release(window, method);
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
