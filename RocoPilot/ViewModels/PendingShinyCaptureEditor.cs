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
    private bool _hasManualName;

    public int? EncounterCountOverride => _hasManualCount ? (int)Math.Round(EncounterCount) : null;

    public string Name
    {
        get => _name;
        set
        {
            if (SetProperty(ref _name, value))
            {
                _hasManualName = true;
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
            _hasManualName = false;
            SetProperty(ref _name, capture?.Name ?? string.Empty, nameof(Name));
        }
        else if (!_hasManualName)
        {
            // 图鉴同步为当前待确认事件补齐名称时，更新自动填入的内容，保留用户草稿。
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
