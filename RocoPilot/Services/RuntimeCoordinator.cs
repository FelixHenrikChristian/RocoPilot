using RocoPilot.Contracts.Services;
using RocoPilot.Models.Runtime;

namespace RocoPilot.Services;

/// <summary>应用运行入口：统一编排 UID 准备、实时任务与依赖会话的独立任务。</summary>
public sealed class RuntimeCoordinator(
    IRuntimeTaskService runtime,
    IIndependentTaskService independentTasks,
    StatisticsUidCoordinatorService uidCoordinator) : IRuntimeTaskService
{
    private readonly SemaphoreSlim _lifecycle = new(1, 1);

    public event EventHandler? SettingsChanged
    {
        add => runtime.SettingsChanged += value;
        remove => runtime.SettingsChanged -= value;
    }

    public bool IsRunning => runtime.IsRunning;
    public bool IsSuspended => runtime.IsSuspended;
    public RuntimeTaskState? CurrentState => runtime.CurrentState;
    public bool EncounterStatisticsEnabled => runtime.EncounterStatisticsEnabled;
    public AutoBattleSettings AutoBattleSettings => runtime.AutoBattleSettings;
    public RuntimeRecognitionSettings RuntimeRecognitionSettings => runtime.RuntimeRecognitionSettings;

    public async Task<RuntimeTaskStartResult> StartAsync(RuntimeTaskStartOptions options, CancellationToken cancellationToken = default)
    {
        await _lifecycle.WaitAsync(cancellationToken);
        try
        {
            if (runtime.IsRunning) return await runtime.StartAsync(options, cancellationToken);
            var preparation = await uidCoordinator.PrepareStartAsync(options, cancellationToken);
            var result = await runtime.StartAsync(options, cancellationToken);
            if (!result.Success || result.State is null)
            {
                uidCoordinator.Clear();
                return result;
            }
            var message = uidCoordinator.CompleteStart(preparation);
            return RuntimeTaskStartResult.Started(result.State,
                string.IsNullOrWhiteSpace(message) ? result.Message : $"{result.Message} {message}");
        }
        catch (OperationCanceledException)
        {
            uidCoordinator.Clear();
            return RuntimeTaskStartResult.Failed("启动任务已取消。");
        }
        finally { _lifecycle.Release(); }
    }

    public async Task StopAsync()
    {
        await _lifecycle.WaitAsync();
        try
        {
            try { await independentTasks.StopAsync(); }
            finally
            {
                uidCoordinator.Clear();
                await runtime.StopAsync();
            }
        }
        finally { _lifecycle.Release(); }
    }

    public Task LoadSettingsAsync(CancellationToken cancellationToken = default) => runtime.LoadSettingsAsync(cancellationToken);
    public void SetEncounterStatisticsEnabled(bool isEnabled)
    {
        runtime.SetEncounterStatisticsEnabled(isEnabled);
        if (!isEnabled) uidCoordinator.Clear();
    }
    public void SetRecognitionOverlayEnabled(bool isEnabled) => runtime.SetRecognitionOverlayEnabled(isEnabled);
    public void SetInfoOverlayEnabled(bool isEnabled) => runtime.SetInfoOverlayEnabled(isEnabled);
    public void SetInfoOverlayLocked(bool isLocked) => runtime.SetInfoOverlayLocked(isLocked);
    public void SetAutoBattleSettings(AutoBattleSettings settings) => runtime.SetAutoBattleSettings(settings);
    public void SetRuntimeRecognitionSettings(RuntimeRecognitionSettings settings) => runtime.SetRuntimeRecognitionSettings(settings);
    public void Suspend(string reason) => runtime.Suspend(reason);
    public void Resume() => runtime.Resume();
}
