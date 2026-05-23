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

        state.Options.RecognitionOverlayEnabled = isEnabled;
        if (isEnabled)
        {
            _recognitionOverlayService.Show(state);
            return;
        }

        _recognitionOverlayService.Hide();
    }

    public void SetInfoOverlayEnabled(bool isEnabled)
    {
        var state = CurrentState;
        if (state is null)
        {
            return;
        }

        state.Options.InfoOverlayEnabled = isEnabled;
        if (isEnabled)
        {
            _infoOverlayService.Show(state);
            return;
        }

        _infoOverlayService.Hide();
    }

    public void SetInfoOverlayLocked(bool isLocked)
    {
        if (CurrentState is { } state)
        {
            state.Options.InfoOverlayLocked = isLocked;
        }

        _infoOverlayService.SetLocked(isLocked);
    }
}
