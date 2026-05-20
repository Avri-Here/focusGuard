using FocusGuard.Core;

namespace FocusGuard.Core.Tests;

/// <summary>
/// Manually advanced clock for unit tests. Tests construct it with a "local-style" DateTimeOffset
/// that has a baked-in offset; UtcNow and LocalNow advance together unless explicitly desynced.
/// This keeps tests independent of the machine's actual time zone.
/// </summary>
internal sealed class FakeClock : IClock
{
    private DateTimeOffset _local;
    private TimeSpan _wallSkew = TimeSpan.Zero;
    private long _monoMs;

    public FakeClock(DateTimeOffset local, long monoMs = 0)
    {
        _local = local;
        _monoMs = monoMs;
    }

    public DateTimeOffset UtcNow => (_local + _wallSkew).ToUniversalTime();
    public DateTimeOffset LocalNow => _local + _wallSkew;
    public long MonotonicMillis => _monoMs;

    public void Advance(TimeSpan ts)
    {
        _local += ts;
        _monoMs += (long)ts.TotalMilliseconds;
    }

    /// <summary>Move wall clock without moving monotonic — simulates the user resetting the system clock.</summary>
    public void AdvanceWallOnly(TimeSpan ts) => _wallSkew += ts;

    /// <summary>Move monotonic without moving wall — simulates real time elapsing while wall stands still.</summary>
    public void AdvanceMonotonicOnly(TimeSpan ts) => _monoMs += (long)ts.TotalMilliseconds;
}
