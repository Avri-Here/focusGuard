namespace FocusGuard.Core;

/// <summary>
/// Wall + monotonic clock abstraction. Exposed so tests can swap in a controllable fake.
/// </summary>
public interface IClock
{
    DateTimeOffset UtcNow { get; }
    /// <summary>Local time (with offset) used for 7am rollover.</summary>
    DateTimeOffset LocalNow { get; }
    /// <summary>Monotonically increasing tick count in milliseconds. Source: <c>Environment.TickCount64</c>.</summary>
    long MonotonicMillis { get; }
}

public sealed class SystemClock : IClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
    public DateTimeOffset LocalNow => DateTimeOffset.Now;
    public long MonotonicMillis => Environment.TickCount64;
}
