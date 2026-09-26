using RocoPilot.Contracts.Services.Statistics;
using RocoPilot.Models.Encounters;

namespace RocoPilot.Services.RuntimeTasks;

// 一场战斗使用同一个事件 ID；先落盘异色提示，再补名称，保存失败可以安全重试。
internal sealed class RuntimePendingShinyCapture(long battleId, string accountUid,
    EncounterSeasonDefinition season, DateTimeOffset detectedAt)
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly EncounterSeasonDefinition _season = new()
    {
        Id = season.Id, Name = season.Name, DateRange = season.DateRange, EncounterTypeName = season.EncounterTypeName
    };
    private bool _saved;
    private string _rawText = string.Empty;
    private string _name = string.Empty;

    public string Id { get; } = Guid.NewGuid().ToString("N");
    public long BattleId { get; } = battleId;
    public bool HasName => !string.IsNullOrWhiteSpace(_name);

    public async Task SaveAsync(IStatisticsService statistics, string? rawText = null, string? name = null)
    {
        await _gate.WaitAsync();
        try
        {
            var nextRaw = string.IsNullOrWhiteSpace(rawText) ? _rawText : rawText.Trim();
            var nextName = string.IsNullOrWhiteSpace(name) ? _name : name.Trim();
            if (_saved && nextRaw == _rawText && nextName == _name) return;
            await statistics.AddPendingShinyCaptureAsync(_season, nextName, detectedAt, Id, nextRaw, accountUid);
            _rawText = nextRaw;
            _name = nextName;
            _saved = true;
        }
        finally
        {
            _gate.Release();
        }
    }
}
