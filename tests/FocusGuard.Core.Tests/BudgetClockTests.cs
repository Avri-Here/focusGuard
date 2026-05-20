using FocusGuard.Core;
using FocusGuard.Core.Ipc;

namespace FocusGuard.Core.Tests;

public class BudgetClockTests
{
    // Use a fixed local offset so tests are TZ-independent.
    private static readonly TimeSpan LocalOffset = TimeSpan.FromHours(0);

    private static (FakeClock clock, BudgetClock bc) Build(DateTimeOffset start, double budget = 60.0)
    {
        var clock = new FakeClock(start);
        var bc = new BudgetClock(clock, DateOnly.FromDateTime(start.DateTime), budget);
        return (clock, bc);
    }

    private static DateTimeOffset Local(int year, int month, int day, int hour = 8, int minute = 0)
        => new(new DateTime(year, month, day, hour, minute, 0, DateTimeKind.Unspecified), LocalOffset);

    [Fact]
    public void Browsing_Tick_OneSecond_Decrements_OneSixtieth()
    {
        var (clock, bc) = Build(Local(2026, 5, 20));
        clock.Advance(TimeSpan.FromSeconds(1));
        var r = bc.Tick(FocusState.Browsing, null);
        Assert.Equal(60.0 - 1.0 / 60.0, r.MinutesRemaining, precision: 5);
        Assert.Equal(BudgetTickEvent.None, r.Events);
    }

    [Fact]
    public void Browsing_DrainingFullBudget_TriggersExhausted()
    {
        var (clock, bc) = Build(Local(2026, 5, 20));
        clock.Advance(TimeSpan.FromMinutes(60));
        var r = bc.Tick(FocusState.Browsing, null);
        Assert.Equal(0.0, r.MinutesRemaining, precision: 5);
        Assert.True(r.Has(BudgetTickEvent.BudgetExhausted));
    }

    [Fact]
    public void Browsing_SplitSessions_TotalSixtyMinutesExhausts()
    {
        var (clock, bc) = Build(Local(2026, 5, 20));

        // Session 1: 25 minutes browsing.
        clock.Advance(TimeSpan.FromMinutes(25));
        var r1 = bc.Tick(FocusState.Browsing, null);
        Assert.Equal(35.0, r1.MinutesRemaining, precision: 4);
        Assert.False(r1.Has(BudgetTickEvent.BudgetExhausted));

        // Idle for 10 minutes (state = Blocked) → no consumption.
        clock.Advance(TimeSpan.FromMinutes(10));
        var rIdle = bc.Tick(FocusState.Blocked, null);
        Assert.Equal(35.0, rIdle.MinutesRemaining, precision: 4);

        // Session 2: 35 more minutes → exhausts budget.
        clock.Advance(TimeSpan.FromMinutes(35));
        var r2 = bc.Tick(FocusState.Browsing, null);
        Assert.Equal(0.0, r2.MinutesRemaining, precision: 4);
        Assert.True(r2.Has(BudgetTickEvent.BudgetExhausted));
    }

    [Fact]
    public void Rollover_At7amLocal_RefillsBudget()
    {
        var (clock, bc) = Build(Local(2026, 5, 20, hour: 8), budget: 5.0);

        // Jump to next day 06:59 — no rollover yet.
        clock.Advance(new DateTime(2026, 5, 21, 6, 59, 0, DateTimeKind.Unspecified) -
                      new DateTime(2026, 5, 20, 8, 0, 0, DateTimeKind.Unspecified));
        var rBefore = bc.Tick(FocusState.Blocked, null);
        Assert.False(rBefore.Has(BudgetTickEvent.DailyRollover));
        Assert.Equal(5.0, rBefore.MinutesRemaining, precision: 4);

        // 07:00 — rollover fires, budget refills to 60.
        clock.Advance(TimeSpan.FromMinutes(1));
        var rAt = bc.Tick(FocusState.Blocked, null);
        Assert.True(rAt.Has(BudgetTickEvent.DailyRollover));
        Assert.Equal(60.0, rAt.MinutesRemaining, precision: 4);
        Assert.Equal(new DateOnly(2026, 5, 21), bc.CycleStartDate);
    }

    [Fact]
    public void Rollover_MidBrowsingSession_FiresAlongsideConsumption()
    {
        // The cycle started yesterday (5-19) at 7am, so the next rollover boundary is 5-20 7:00.
        var clock = new FakeClock(Local(2026, 5, 20, hour: 6, minute: 59));
        var bc = new BudgetClock(clock, new DateOnly(2026, 5, 19), 60.0);

        // Advance 2 minutes -> crosses 7am while in Browsing.
        clock.Advance(TimeSpan.FromMinutes(2));
        var r = bc.Tick(FocusState.Browsing, null);
        // Rollover refills to 60. Browsing also consumed 2 minutes -> 58.
        Assert.True(r.Has(BudgetTickEvent.DailyRollover));
        Assert.Equal(58.0, r.MinutesRemaining, precision: 3);
    }

    [Fact]
    public void MultiDayCatchup_AfterLongSleep_RefillsOnce()
    {
        var (clock, bc) = Build(Local(2026, 5, 20, hour: 8), budget: 12.0);
        // Sleep 5 days. Budget should refill but not multiply.
        clock.Advance(TimeSpan.FromDays(5));
        var r = bc.Tick(FocusState.Blocked, null);
        Assert.True(r.Has(BudgetTickEvent.DailyRollover));
        Assert.Equal(60.0, r.MinutesRemaining, precision: 4);
    }

    [Fact]
    public void PauseExpiry_FiresWhenWallReachesPauseEnd()
    {
        var (clock, bc) = Build(Local(2026, 5, 20));
        var pauseEnd = clock.UtcNow.AddMinutes(15);

        clock.Advance(TimeSpan.FromMinutes(14));
        var r1 = bc.Tick(FocusState.Paused, pauseEnd);
        Assert.False(r1.Has(BudgetTickEvent.PauseExpired));

        clock.Advance(TimeSpan.FromMinutes(2));
        var r2 = bc.Tick(FocusState.Paused, pauseEnd);
        Assert.True(r2.Has(BudgetTickEvent.PauseExpired));
    }

    [Fact]
    public void ClockTamper_BackwardJump_FiresAndDoesNotConsume()
    {
        var (clock, bc) = Build(Local(2026, 5, 20));

        // Browsing for 10 minutes legitimately.
        clock.Advance(TimeSpan.FromMinutes(10));
        bc.Tick(FocusState.Browsing, null);
        Assert.Equal(50.0, bc.MinutesRemaining, precision: 3);

        // Wall jumps backward by 30 minutes; monotonic does not.
        clock.AdvanceWallOnly(TimeSpan.FromMinutes(-30));
        var r = bc.Tick(FocusState.Browsing, null);
        Assert.True(r.Has(BudgetTickEvent.ClockTamperDetected));
        // Budget unchanged for the tampered tick.
        Assert.Equal(50.0, bc.MinutesRemaining, precision: 3);
    }

    [Fact]
    public void SmallBackwardJump_BelowThreshold_DoesNotFire()
    {
        var (clock, bc) = Build(Local(2026, 5, 20));
        clock.Advance(TimeSpan.FromMinutes(1));
        bc.Tick(FocusState.Browsing, null);

        // Wall jumps back by 1 minute — below the 5-minute tamper threshold (NTP correction etc.)
        clock.AdvanceWallOnly(TimeSpan.FromMinutes(-1));
        var r = bc.Tick(FocusState.Browsing, null);
        Assert.False(r.Has(BudgetTickEvent.ClockTamperDetected));
    }

    [Fact]
    public void ForwardWallJump_DoesNotDrainBudget()
    {
        var (clock, bc) = Build(Local(2026, 5, 20));
        // Wall leaps forward 30 minutes but monotonic only reflects 1 second.
        clock.AdvanceMonotonicOnly(TimeSpan.FromSeconds(1));
        clock.AdvanceWallOnly(TimeSpan.FromMinutes(30));
        var r = bc.Tick(FocusState.Browsing, null);
        // We should have only consumed up to monotonic elapsed: 1 second ≈ 1/60 minute.
        Assert.InRange(60.0 - r.MinutesRemaining, 0.01, 0.02);
    }

    [Fact]
    public void Snapshot_RoundTripsViaState()
    {
        var (clock, bc) = Build(Local(2026, 5, 20), budget: 42.0);
        clock.Advance(TimeSpan.FromMinutes(10));
        bc.Tick(FocusState.Browsing, null);

        var snap = bc.Snapshot(FocusState.Browsing, pauseEndAt: null);
        Assert.Equal(FocusState.Browsing, snap.State);
        Assert.InRange(snap.MinutesRemaining, 31.99, 32.01);
        Assert.Equal(new DateOnly(2026, 5, 20), snap.CycleStartDate);
    }
}
