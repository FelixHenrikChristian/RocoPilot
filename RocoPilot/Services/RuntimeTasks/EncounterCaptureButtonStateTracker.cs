namespace RocoPilot.Services;

internal enum EncounterCaptureButtonState
{
    Unknown,
    Disabled,
    Enabled
}

internal static class EncounterCaptureButtonRecognition
{
    public const double MinimumVisibilityScore = 0.88;
    public const double DisabledMarkerPresentScore = 0.88;
    public const double DisabledMarkerAbsentScore = 0.82;

    public static EncounterCaptureButtonState Classify(
        double enabledScore,
        double disabledScore,
        double disabledMarkerScore)
    {
        if (Math.Max(enabledScore, disabledScore) < MinimumVisibilityScore)
        {
            return EncounterCaptureButtonState.Unknown;
        }

        if (disabledMarkerScore >= DisabledMarkerPresentScore)
        {
            return EncounterCaptureButtonState.Disabled;
        }

        return disabledMarkerScore <= DisabledMarkerAbsentScore
            ? EncounterCaptureButtonState.Enabled
            : EncounterCaptureButtonState.Unknown;
    }
}

internal sealed class EncounterCaptureButtonStateTracker
{
    public const int RequiredEnabledConfirmationCount = 2;

    private readonly object _syncRoot = new();
    private bool _hasSeenDisabled;
    private bool _isRelieved;
    private int _consecutiveEnabledCount;
    private EncounterCaptureButtonState _currentState;

    public EncounterCaptureButtonState CurrentState
    {
        get
        {
            lock (_syncRoot)
            {
                return _currentState;
            }
        }
    }

    public bool HasSeenDisabled
    {
        get
        {
            lock (_syncRoot)
            {
                return _hasSeenDisabled;
            }
        }
    }

    public bool IsRelieved
    {
        get
        {
            lock (_syncRoot)
            {
                return _isRelieved;
            }
        }
    }

    public int EnabledConfirmationCount
    {
        get
        {
            lock (_syncRoot)
            {
                return _consecutiveEnabledCount;
            }
        }
    }

    public bool ShouldHoldAttackForUnconfirmedRelief
    {
        get
        {
            lock (_syncRoot)
            {
                return _hasSeenDisabled
                    && !_isRelieved
                    && _currentState != EncounterCaptureButtonState.Disabled;
            }
        }
    }

    public bool Observe(EncounterCaptureButtonState state)
    {
        lock (_syncRoot)
        {
            _currentState = state;
            if (_isRelieved)
            {
                return false;
            }

            if (state == EncounterCaptureButtonState.Disabled)
            {
                _hasSeenDisabled = true;
                _consecutiveEnabledCount = 0;
                return false;
            }

            if (state != EncounterCaptureButtonState.Enabled || !_hasSeenDisabled)
            {
                _consecutiveEnabledCount = 0;
                return false;
            }

            _consecutiveEnabledCount++;
            if (_consecutiveEnabledCount < RequiredEnabledConfirmationCount)
            {
                return false;
            }

            _isRelieved = true;
            return true;
        }
    }

    public void Reset()
    {
        lock (_syncRoot)
        {
            _hasSeenDisabled = false;
            _isRelieved = false;
            _consecutiveEnabledCount = 0;
            _currentState = EncounterCaptureButtonState.Unknown;
        }
    }
}
