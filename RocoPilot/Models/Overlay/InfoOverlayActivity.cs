namespace RocoPilot.Models.Overlay;

public enum InfoOverlayActivityKind { Spirit, Skill, Capture, EnergyRecovery, PetSwitch, Waiting, Record, Error }

public sealed record InfoOverlayActivity(long Id, InfoOverlayActivityKind Kind, string Title,
    string Description, string CreatureName, DateTimeOffset CreatedAt, DateTimeOffset? ExpiresAt,
    bool IsBattleBound = true);

// 只维护展示事件，不参与战斗策略；战斗/回合标识挡住迟到的 OCR 与按键回调。
public sealed class InfoOverlayActivityTracker
{
    private readonly object _gate = new();
    private readonly Dictionary<string, InfoOverlayActivity> _seen = new();
    private long _battleId, _turnId, _nextId;
    private string _creatureName = string.Empty;
    private InfoOverlayActivity? _current;
    public InfoOverlayActivity? Current { get { lock (_gate) return _current; } }
    public string CreatureName { get { lock (_gate) return _creatureName; } }

    public void ResetBattle(long battleId)
    {
        lock (_gate)
        {
            _battleId = battleId; _turnId = 0; _creatureName = string.Empty; _seen.Clear();
            if (_current?.IsBattleBound == true) _current = null;
        }
    }

    public void BeginTurn(long turnId) { lock (_gate) _turnId = turnId; }
    public void Clear() { lock (_gate) { _seen.Clear(); _current = null; _creatureName = string.Empty; } }

    public InfoOverlayActivity? RecognizeSpirit(long battleId, long? turnId, string name, DateTimeOffset now)
    {
        lock (_gate)
        {
            if (battleId != _battleId || (turnId.HasValue && turnId != _turnId) || string.IsNullOrWhiteSpace(name)) return null;
            _creatureName = name;
            // 动作提示同时带上名称和头像，低优先级的名称 OCR 不覆盖正在执行的操作。
            if (_current is { IsBattleBound: true } current && current.Kind != InfoOverlayActivityKind.Spirit
                && (!current.ExpiresAt.HasValue || current.ExpiresAt > now))
            {
                _current = current with { CreatureName = name };
                return _current;
            }
            return Publish(battleId, turnId, $"spirit:{name}", InfoOverlayActivityKind.Spirit,
                $"识别到{name}", string.Empty, now, completed: true);
        }
    }

    public InfoOverlayActivity? Publish(long battleId, long? turnId, string key, InfoOverlayActivityKind kind,
        string title, string description, DateTimeOffset now, bool completed)
    {
        lock (_gate)
        {
            if (battleId != _battleId || (turnId.HasValue && turnId != _turnId)) return null;
            if (_seen.TryGetValue(key, out var existing))
            {
                // 已结束的同一动作重试不重新展开；正在执行的动作可以原位更新正文。
                if (existing.ExpiresAt.HasValue || _current?.Id != existing.Id) return null;
                _current = existing with { Title = title, Description = description, CreatureName = _creatureName,
                    ExpiresAt = completed ? now.AddSeconds(4) : null };
            }
            else
                _current = new(++_nextId, kind, title, description, _creatureName, now, completed ? now.AddSeconds(4) : null);
            _seen[key] = _current;
            return _current;
        }
    }

    public InfoOverlayActivity Record(string name, int? count, DateTimeOffset recordedAt, DateTimeOffset publishedAt)
    {
        lock (_gate)
        {
            // 后台保存只更新计数，不打断尚未完成的按键序列提示。
            if (_current is { IsBattleBound: true, ExpiresAt: null } ongoing) return ongoing;
            return _current = new(++_nextId, InfoOverlayActivityKind.Record,
                string.IsNullOrWhiteSpace(name) ? "奇遇记录已暂存" : count.HasValue ? $"{name} · 奇遇 {count} 次" : $"{name} · 奇遇已暂存",
                count.HasValue ? $"记录更新于 {recordedAt.ToLocalTime():HH:mm:ss}" : "等待补齐赛季或精灵名称后归档",
                name, publishedAt, publishedAt.AddSeconds(4), IsBattleBound: false);
        }
    }
}
