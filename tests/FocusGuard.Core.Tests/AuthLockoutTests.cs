using FocusGuard.Core.Security;

namespace FocusGuard.Core.Tests;

public class AuthLockoutTests
{
    [Fact]
    public void First_four_failures_do_not_lock_out()
    {
        var clock = new FakeClock(new DateTimeOffset(2026, 5, 20, 12, 0, 0, TimeSpan.FromHours(3)));
        var lockout = new AuthLockout(clock, threshold: 5, cooldown: TimeSpan.FromSeconds(30));

        for (var i = 0; i < 4; i++)
        {
            Assert.False(lockout.IsLockedOut());
            lockout.RecordFailure();
        }
        Assert.False(lockout.IsLockedOut());
    }

    [Fact]
    public void Fifth_consecutive_failure_triggers_lockout()
    {
        var clock = new FakeClock(new DateTimeOffset(2026, 5, 20, 12, 0, 0, TimeSpan.FromHours(3)));
        var lockout = new AuthLockout(clock, threshold: 5, cooldown: TimeSpan.FromSeconds(30));

        for (var i = 0; i < 5; i++) lockout.RecordFailure();

        Assert.True(lockout.IsLockedOut());
    }

    [Fact]
    public void Lockout_clears_after_cooldown_passes()
    {
        var clock = new FakeClock(new DateTimeOffset(2026, 5, 20, 12, 0, 0, TimeSpan.FromHours(3)));
        var lockout = new AuthLockout(clock, threshold: 5, cooldown: TimeSpan.FromSeconds(30));

        for (var i = 0; i < 5; i++) lockout.RecordFailure();
        Assert.True(lockout.IsLockedOut());

        clock.Advance(TimeSpan.FromSeconds(31));
        Assert.False(lockout.IsLockedOut());
    }

    [Fact]
    public void Lockout_remains_active_until_cooldown_passes()
    {
        var clock = new FakeClock(new DateTimeOffset(2026, 5, 20, 12, 0, 0, TimeSpan.FromHours(3)));
        var lockout = new AuthLockout(clock, threshold: 5, cooldown: TimeSpan.FromSeconds(30));

        for (var i = 0; i < 5; i++) lockout.RecordFailure();

        clock.Advance(TimeSpan.FromSeconds(15));
        Assert.True(lockout.IsLockedOut());
    }

    [Fact]
    public void RecordSuccess_resets_failure_counter()
    {
        var clock = new FakeClock(new DateTimeOffset(2026, 5, 20, 12, 0, 0, TimeSpan.FromHours(3)));
        var lockout = new AuthLockout(clock, threshold: 5, cooldown: TimeSpan.FromSeconds(30));

        for (var i = 0; i < 4; i++) lockout.RecordFailure();
        lockout.RecordSuccess();
        // After success, four more failures should not lock out (counter was reset).
        for (var i = 0; i < 4; i++) lockout.RecordFailure();

        Assert.False(lockout.IsLockedOut());
    }

    [Fact]
    public void After_cooldown_failures_resume_at_zero()
    {
        var clock = new FakeClock(new DateTimeOffset(2026, 5, 20, 12, 0, 0, TimeSpan.FromHours(3)));
        var lockout = new AuthLockout(clock, threshold: 5, cooldown: TimeSpan.FromSeconds(30));

        for (var i = 0; i < 5; i++) lockout.RecordFailure();
        clock.Advance(TimeSpan.FromSeconds(31));
        Assert.False(lockout.IsLockedOut());

        // Two more failures right after cooldown should NOT relock (only 2 since cooldown).
        lockout.RecordFailure();
        lockout.RecordFailure();
        Assert.False(lockout.IsLockedOut());
    }
}
