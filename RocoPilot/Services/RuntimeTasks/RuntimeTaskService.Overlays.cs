namespace RocoPilot.Services;

public sealed partial class RuntimeTaskService
{
    public void SetRecognitionOverlayEnabled(bool isEnabled)
    {
        var state = CurrentState;
        if (state is null)
        {
            return;
        }

        if (state.Options.RecognitionOverlayEnabled == isEnabled)
        {
            return;
        }

        state.Options.RecognitionOverlayEnabled = isEnabled;
        if (isEnabled)
        {
            _recognitionOverlayService.Show(state);
            NotifySettingsChanged();
            return;
        }

        _recognitionOverlayService.Hide();
        NotifySettingsChanged();
    }

    public void SetInfoOverlayEnabled(bool isEnabled)
    {
        var state = CurrentState;
        if (state is null)
        {
            return;
        }

        if (state.Options.InfoOverlayEnabled == isEnabled)
        {
            return;
        }

        state.Options.InfoOverlayEnabled = isEnabled;
        if (isEnabled)
        {
            _infoOverlayService.Show(state);
            UpdateInfoOverlayTaskIndicators();
            NotifySettingsChanged();
            return;
        }

        _infoOverlayService.Hide();
        NotifySettingsChanged();
    }

    public void SetInfoOverlayLocked(bool isLocked)
    {
        var changed = false;
        if (CurrentState is { } state && state.Options.InfoOverlayLocked != isLocked)
        {
            state.Options.InfoOverlayLocked = isLocked;
            changed = true;
        }

        _infoOverlayService.SetLocked(isLocked);
        if (changed)
        {
            NotifySettingsChanged();
        }
    }

    private void UpdateInfoOverlayTaskIndicators()
    {
        _infoOverlayService.UpdateTaskIndicators(EncounterStatisticsEnabled, _autoBattleSettings.IsEnabled);
    }
}
