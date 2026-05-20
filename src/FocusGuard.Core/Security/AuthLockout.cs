namespace FocusGuard.Core.Security;

/// <summary>
/// Sliding brute-force defense for password-gated IPC. After <c>threshold</c> consecutive
/// failures the next call is locked out for <c>cooldown</c>; once the cooldown elapses, the
/// counter resets to zero. <see cref="RecordSuccess"/> also resets the counter.
/// </summary>
public sealed class AuthLockout
{
    private readonly IClock _clock;
    private readonly int _threshold;
    private readonly TimeSpan _cooldown;

    private int _failures;
    private DateTimeOffset _lockedUntilUtc = DateTimeOffset.MinValue;

    public AuthLockout(IClock clock, int threshold = 5, TimeSpan? cooldown = null)
    {
        _clock = clock;
        _threshold = threshold;
        _cooldown = cooldown ?? TimeSpan.FromSeconds(30);
    }

    public bool IsLockedOut()
    {
        if (_clock.UtcNow >= _lockedUntilUtc)
        {
            // Cooldown elapsed: reset the counter so the user gets a clean slate.
            if (_lockedUntilUtc != DateTimeOffset.MinValue)
            {
                _failures = 0;
                _lockedUntilUtc = DateTimeOffset.MinValue;
            }
            return false;
        }
        return true;
    }

    public TimeSpan TimeRemaining()
    {
        var remaining = _lockedUntilUtc - _clock.UtcNow;
        return remaining < TimeSpan.Zero ? TimeSpan.Zero : remaining;
    }

    public void RecordFailure()
    {
        _failures++;
        if (_failures >= _threshold)
        {
            _lockedUntilUtc = _clock.UtcNow + _cooldown;
        }
    }

    public void RecordSuccess()
    {
        _failures = 0;
        _lockedUntilUtc = DateTimeOffset.MinValue;
    }
}
