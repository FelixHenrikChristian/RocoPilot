namespace RocoPilot.Services.Logging;

/// <summary>
/// 循环里反复出现的同一种故障，只在第一次和之后每隔一段时间放行一条日志，避免每一帧都写一条。
/// 故障消失后调用 <see cref="Reset"/>，可据返回值记录一条“已恢复”。
/// </summary>
internal sealed class RepeatedFailureGate(TimeSpan interval, TimeProvider? timeProvider = null)
{
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;
    private readonly object _gate = new();
    private long _lastLoggedAt;
    private int _total;
    private int _skipped;

    /// <summary>登记一次故障。返回 true 时由调用方写日志，<paramref name="skipped"/> 是上次放行后没有记录的次数。</summary>
    public bool TryEnter(out int skipped)
    {
        lock (_gate)
        {
            var now = _time.GetTimestamp();
            if (_total++ > 0 && _time.GetElapsedTime(_lastLoggedAt, now) < interval)
            {
                _skipped++;
                skipped = 0;
                return false;
            }

            _lastLoggedAt = now;
            skipped = _skipped;
            _skipped = 0;
            return true;
        }
    }

    /// <summary>接在日志消息末尾的说明：上一条之后还有几次相同故障没有记录；没有则为空。</summary>
    public static string Describe(int skipped) =>
        skipped > 0 ? $"（此前另有 {skipped} 次相同故障未记录）" : string.Empty;

    /// <summary>故障已消失。返回这一轮故障出现的总次数，0 表示此前没有故障。</summary>
    public int Reset()
    {
        lock (_gate)
        {
            var total = _total;
            _total = 0;
            _skipped = 0;
            return total;
        }
    }
}
