using CommunityToolkit.Mvvm.ComponentModel;
using RocoPilot.Helpers;
using RocoPilot.Models.Statistics;

namespace RocoPilot.ViewModels;

public sealed class PendingShinyCaptureEditor : ObservableObject
{
    private AccountStatisticsData? _account;
    private PendingShinyCaptureItem? _capture;
    private string _name = string.Empty;
    private double _encounterCount;
    private bool _hasManualCount;

    public string Name
    {
        get => _name;
        set
        {
            if (SetProperty(ref _name, value))
            {
                _hasManualCount = false;
                UpdateSuggestedCount();
            }
        }
    }

    public double EncounterCount
    {
        get => _encounterCount;
        set
        {
            var count = double.IsNaN(value) ? 0 : Math.Clamp(value, 0, int.MaxValue);
            if (SetProperty(ref _encounterCount, count))
            {
                _hasManualCount = true;
            }
        }
    }

    internal void Update(AccountStatisticsData? account, PendingShinyCaptureItem? capture)
    {
        var sameCapture = capture is not null
            && string.Equals(_account?.Uid, account?.Uid, StringComparison.OrdinalIgnoreCase)
            && string.Equals(_capture?.Id, capture.Id, StringComparison.OrdinalIgnoreCase);
        _account = account;
        _capture = capture;

        if (!sameCapture)
        {
            _hasManualCount = false;
            SetProperty(ref _name, capture?.Name ?? string.Empty, nameof(Name));
        }

        if (!_hasManualCount)
        {
            UpdateSuggestedCount();
        }
    }

    private void UpdateSuggestedCount()
    {
        var count = _capture is null ? 0 : StatisticsProjection.FindEncounterCount(
            _account,
            _capture.Season,
            TextMatchingHelper.NormalizeSpiritNameInput(Name));
        SetProperty(ref _encounterCount, count, nameof(EncounterCount));
    }
}
