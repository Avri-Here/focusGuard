using FocusGuard.Core.Ipc;

namespace FocusGuard.Core;

/// <summary>
/// Outcomes a single tick can produce. The host translates these into state-machine inputs
/// (<see cref="StateInput.BudgetExhausted"/>, <see cref="StateInput.DailyRollover"/>,
/// <see cref="StateInput.PauseExpired"/>, <see cref="StateInput.ClockTamperDetected"/>).
/// </summary>
[Flags]
public enum BudgetTickEvent
{
    None = 0,
    BudgetExhausted = 1 << 0,
    DailyRollover = 1 << 1,
    PauseExpired = 1 << 2,
    ClockTamperDetected = 1 << 3,
}

public readonly record struct BudgetTickResult(double MinutesRemaining, BudgetTickEvent Events)
{
    public bool Has(BudgetTickEvent e) => (Events & e) == e;
}

/// <summary>
/// Drives daily budget bookkeeping, 7am rollover, sleep/resume catch-up, and clock-tamper
/// detection. Pure logic over an injected <see cref="IClock"/>; no timers of its own.
/// </summary>
public sealed class BudgetClock
{
    public const double DailyBudgetMinutes = 60.0;
    public const int RolloverHourLocal = 7;
    public static readonly TimeSpan ClockTamperBackwardThreshold = TimeSpan.FromMinutes(5);

    private readonly IClock _clock;

    /// <summary>Date whose 7am started the current cycle (in local time).</summary>
    public DateOnly CycleStartDate { get; private set; }
    public double MinutesRemaining { get; private set; }

    private DateTimeOffset _lastWallUtc;
    private long _lastMonotonicMillis;

    public BudgetClock(IClock clock, DateOnly cycleStartDate, double minutesRemaining)
    {
        _clock = clock;
        CycleStartDate = cycleStartDate;
        MinutesRemaining = minutesRemaining;
        _lastWallUtc = clock.UtcNow;
        _lastMonotonicMillis = clock.MonotonicMillis;
    }

    /// <summary>
    /// Initialise from the persisted state. If the state was written long ago and the device
    /// slept/restarted, the constructor still uses the live clock as the baseline; the next
    /// call to <see cref="Tick"/> handles any rollover that happened while we were off.
    /// </summary>
    public static BudgetClock Restore(IClock clock, FocusGuardState state)
    {
        var bc = new BudgetClock(clock, state.CycleStartDate, state.MinutesRemaining);
        // If a cycle has elapsed while powered off, refill on first tick.
        return bc;
    }

    public FocusGuardState Snapshot(FocusState state, DateTimeOffset? pauseEndAt) => new()
    {
        CycleStartDate = CycleStartDate,
        MinutesRemaining = MinutesRemaining,
        State = state,
        PauseEndAt = pauseEndAt,
        MonotonicAnchorUtc = _lastWallUtc,
        MonotonicAnchorTicks = _lastMonotonicMillis,
    };

    /// <summary>
    /// Advance the clock. Call once per second. <paramref name="state"/> drives whether budget
    /// is consumed and whether pause expiry can fire. <paramref name="pauseEndAt"/> only matters
    /// when in <see cref="FocusState.Paused"/>.
    /// </summary>
    public BudgetTickResult Tick(FocusState state, DateTimeOffset? pauseEndAt)
    {
        var nowUtc = _clock.UtcNow;
        var nowMono = _clock.MonotonicMillis;
        var events = BudgetTickEvent.None;

        var monotonicElapsed = TimeSpan.FromMilliseconds(Math.Max(0, nowMono - _lastMonotonicMillis));
        var wallElapsed = nowUtc - _lastWallUtc;

        // ---- Tamper detection: wall clock moved backward materially compared to monotonic ----
        // If wall jumped back by more than the threshold relative to monotonic, treat as tamper.
        if (wallElapsed < TimeSpan.Zero &&
            (monotonicElapsed - wallElapsed) > ClockTamperBackwardThreshold)
        {
            events |= BudgetTickEvent.ClockTamperDetected;
            // Re-anchor; do not consume budget for this tick.
            _lastWallUtc = nowUtc;
            _lastMonotonicMillis = nowMono;
            return new BudgetTickResult(MinutesRemaining, events);
        }

        // ---- 7am rollover (local time) ----
        // Walk forward in 1-day steps until cycleStartDate's 7am next-day boundary is in the future.
        // Loops at most once per real-world day, so cheap; `while` covers multi-day catch-up
        // after suspend/resume.
        while (HasCrossedRolloverBoundary(_clock.LocalNow))
        {
            CycleStartDate = CycleStartDate.AddDays(1);
            MinutesRemaining = DailyBudgetMinutes;
            events |= BudgetTickEvent.DailyRollover;
        }

        // ---- Pause expiry ----
        if (state == FocusState.Paused && pauseEndAt is { } end && nowUtc >= end)
        {
            events |= BudgetTickEvent.PauseExpired;
        }

        // ---- Budget consumption while browsing ----
        if (state == FocusState.Browsing && wallElapsed > TimeSpan.Zero)
        {
            // Cap consumption at monotonic elapsed so a forward wall jump can't drain the budget.
            var consumeSeconds = Math.Min(wallElapsed.TotalSeconds, monotonicElapsed.TotalSeconds);
            MinutesRemaining = Math.Max(0, MinutesRemaining - consumeSeconds / 60.0);
            if (MinutesRemaining <= 0)
            {
                events |= BudgetTickEvent.BudgetExhausted;
            }
        }

        _lastWallUtc = nowUtc;
        _lastMonotonicMillis = nowMono;
        return new BudgetTickResult(MinutesRemaining, events);
    }

    /// <summary>True if local-now is at-or-past the 7am boundary that follows <see cref="CycleStartDate"/>.</summary>
    private bool HasCrossedRolloverBoundary(DateTimeOffset localNow)
    {
        // Compare in the offset the caller provided (DateTimeOffset.DateTime), so behaviour does
        // not depend on the machine's currently-configured time zone.
        var nextBoundaryDate = CycleStartDate.AddDays(1);
        var nextBoundary = new DateTime(nextBoundaryDate.Year, nextBoundaryDate.Month, nextBoundaryDate.Day, RolloverHourLocal, 0, 0, DateTimeKind.Unspecified);
        return localNow.DateTime >= nextBoundary;
    }

    /// <summary>
    /// Compute the next absolute local time at which the cycle resets. Used to populate the
    /// <see cref="StatusResponse.CycleResetAt"/> field for the tray.
    /// </summary>
    public DateTimeOffset NextCycleResetLocal(TimeSpan localOffset)
    {
        var d = CycleStartDate.AddDays(1);
        var dt = new DateTime(d.Year, d.Month, d.Day, RolloverHourLocal, 0, 0, DateTimeKind.Unspecified);
        return new DateTimeOffset(dt, localOffset);
    }
}
