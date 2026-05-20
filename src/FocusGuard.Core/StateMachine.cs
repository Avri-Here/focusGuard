using FocusGuard.Core.Ipc;

namespace FocusGuard.Core;

/// <summary>
/// Inputs that can trigger a state transition. Time-based inputs (Tick) carry context the
/// machine needs to make decisions; the machine itself remains pure (no clock access).
/// </summary>
public abstract record StateInput
{
    public sealed record StartBudget(double MinutesRemaining) : StateInput;
    public sealed record StopBudget : StateInput;
    public sealed record BudgetExhausted : StateInput;
    public sealed record AdminPause(DateTimeOffset Until) : StateInput;
    public sealed record AdminEndPause : StateInput;
    public sealed record AdminDisable : StateInput;
    public sealed record AdminEnable : StateInput;
    public sealed record DailyRollover : StateInput;
    public sealed record ClockTamperDetected : StateInput;
    public sealed record PauseExpired : StateInput;
}

/// <summary>Outputs the host should react to (firewall posture changes, audit log, etc.).</summary>
public enum NetworkPosture { Closed, Open }

public sealed record StateTransition(
    FocusState From,
    FocusState To,
    NetworkPosture Posture,
    DateTimeOffset? PauseEndAt,
    string Reason);

public sealed class IllegalTransitionException(FocusState from, StateInput input)
    : InvalidOperationException($"Illegal transition from {from} on input {input.GetType().Name}");

/// <summary>
/// Pure state machine. Wraps <see cref="FocusState"/> with the rules for legal transitions.
/// Caller persists the resulting state separately; this class never touches I/O or the clock.
/// </summary>
public sealed class StateMachine
{
    public FocusState Current { get; private set; }
    public DateTimeOffset? PauseEndAt { get; private set; }

    public StateMachine(FocusState initial = FocusState.Blocked, DateTimeOffset? pauseEndAt = null)
    {
        Current = initial;
        PauseEndAt = initial == FocusState.Paused ? pauseEndAt : null;
    }

    public StateTransition Apply(StateInput input)
    {
        var from = Current;
        FocusState to;
        DateTimeOffset? newPauseEnd = PauseEndAt;
        string reason;

        switch (Current, input)
        {
            // ---- BLOCKED ----
            case (FocusState.Blocked, StateInput.StartBudget sb):
                if (sb.MinutesRemaining <= 0)
                    throw new IllegalTransitionException(from, input);
                to = FocusState.Browsing;
                newPauseEnd = null;
                reason = "user started browsing budget";
                break;
            case (FocusState.Blocked, StateInput.AdminPause ap):
                to = FocusState.Paused;
                newPauseEnd = ap.Until;
                reason = "admin paused blocking";
                break;
            case (FocusState.Blocked, StateInput.AdminDisable):
                to = FocusState.Disabled;
                newPauseEnd = null;
                reason = "admin disabled";
                break;
            case (FocusState.Blocked, StateInput.DailyRollover):
                to = FocusState.Blocked;
                reason = "daily rollover (no-op)";
                break;

            // ---- BROWSING ----
            case (FocusState.Browsing, StateInput.StopBudget):
                to = FocusState.Blocked;
                reason = "user stopped browsing";
                break;
            case (FocusState.Browsing, StateInput.BudgetExhausted):
                to = FocusState.Blocked;
                reason = "budget exhausted";
                break;
            case (FocusState.Browsing, StateInput.DailyRollover):
                // 7am rollover ends current session and refills budget; caller refills.
                to = FocusState.Blocked;
                reason = "daily rollover ended browsing session";
                break;
            case (FocusState.Browsing, StateInput.ClockTamperDetected):
                to = FocusState.Blocked;
                reason = "clock tamper detected; ending session";
                break;
            case (FocusState.Browsing, StateInput.AdminPause ap2):
                to = FocusState.Paused;
                newPauseEnd = ap2.Until;
                reason = "admin paused while browsing";
                break;
            case (FocusState.Browsing, StateInput.AdminDisable):
                to = FocusState.Disabled;
                newPauseEnd = null;
                reason = "admin disabled while browsing";
                break;

            // ---- PAUSED ----
            case (FocusState.Paused, StateInput.AdminEndPause):
            case (FocusState.Paused, StateInput.PauseExpired):
                to = FocusState.Blocked;
                newPauseEnd = null;
                reason = "pause ended";
                break;
            case (FocusState.Paused, StateInput.AdminDisable):
                to = FocusState.Disabled;
                newPauseEnd = null;
                reason = "admin disabled while paused";
                break;
            case (FocusState.Paused, StateInput.DailyRollover):
                // Rollover during pause is a no-op (budget refill handled by caller).
                to = FocusState.Paused;
                reason = "daily rollover during pause (no state change)";
                break;

            // ---- DISABLED ----
            case (FocusState.Disabled, StateInput.AdminEnable):
                to = FocusState.Blocked;
                newPauseEnd = null;
                reason = "admin re-enabled";
                break;
            case (FocusState.Disabled, StateInput.DailyRollover):
                to = FocusState.Disabled;
                reason = "daily rollover while disabled (no-op)";
                break;

            default:
                throw new IllegalTransitionException(from, input);
        }

        Current = to;
        PauseEndAt = newPauseEnd;
        return new StateTransition(from, to, PostureFor(to), newPauseEnd, reason);
    }

    public NetworkPosture CurrentPosture => PostureFor(Current);

    public static NetworkPosture PostureFor(FocusState state) => state switch
    {
        FocusState.Blocked => NetworkPosture.Closed,
        FocusState.Browsing => NetworkPosture.Open,
        FocusState.Paused => NetworkPosture.Open,
        FocusState.Disabled => NetworkPosture.Open,
        _ => NetworkPosture.Closed,
    };
}
