using FocusGuard.Core.Ipc;

namespace FocusGuard.Core;

/// <summary>Persisted as DPAPI-encrypted JSON in <c>config.dat</c>.</summary>
public sealed class FocusGuardConfig
{
    public string? PasswordHash { get; set; }
    public List<string> Whitelist { get; set; } = new();
    public int AdminPauseDefaultMinutes { get; set; } = 30;
}

/// <summary>Persisted as DPAPI-encrypted JSON in <c>state.dat</c>.</summary>
public sealed class FocusGuardState
{
    public DateOnly CycleStartDate { get; set; }
    public double MinutesRemaining { get; set; } = 60.0;
    public FocusState State { get; set; } = FocusState.Blocked;
    public DateTimeOffset? PauseEndAt { get; set; }
    /// <summary>UTC anchor written next to a monotonic-tick anchor for clock-tamper detection.</summary>
    public DateTimeOffset MonotonicAnchorUtc { get; set; }
    public long MonotonicAnchorTicks { get; set; }
}
