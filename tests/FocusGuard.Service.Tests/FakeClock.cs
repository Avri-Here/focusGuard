using FocusGuard.Core;

namespace FocusGuard.Service.Tests;

/// <summary>
/// Manually advanced clock for service-level tests; mirrors the Core test fake but lives here
/// because the Core copy is internal to that test project.
/// </summary>
internal sealed class FakeClock : IClock
{
    private DateTimeOffset _local;
    private long _monoMs;

    public FakeClock(DateTimeOffset local, long monoMs = 0)
    {
        _local = local;
        _monoMs = monoMs;
    }

    public DateTimeOffset UtcNow => _local.ToUniversalTime();
    public DateTimeOffset LocalNow => _local;
    public long MonotonicMillis => _monoMs;

    public void Advance(TimeSpan ts)
    {
        _local += ts;
        _monoMs += (long)ts.TotalMilliseconds;
    }

    /// <summary>Move only the wall clock; monotonic stays put. Use to simulate a system-clock
    /// change (the tamper trigger is the divergence between the two).</summary>
    public void AdvanceWallOnly(TimeSpan ts) => _local += ts;
}
