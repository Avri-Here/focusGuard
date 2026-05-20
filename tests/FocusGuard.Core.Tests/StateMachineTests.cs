using FocusGuard.Core;
using FocusGuard.Core.Ipc;

namespace FocusGuard.Core.Tests;

public class StateMachineTests
{
    private static readonly DateTimeOffset PauseUntil = DateTimeOffset.UtcNow.AddMinutes(15);

    // ---- BLOCKED ----

    [Fact]
    public void Blocked_StartBudget_WithMinutes_GoesBrowsing()
    {
        var sm = new StateMachine();
        var t = sm.Apply(new StateInput.StartBudget(60));
        Assert.Equal(FocusState.Browsing, t.To);
        Assert.Equal(NetworkPosture.Open, t.Posture);
        Assert.Null(t.PauseEndAt);
    }

    [Fact]
    public void Blocked_StartBudget_WithZeroMinutes_Throws()
    {
        var sm = new StateMachine();
        Assert.Throws<IllegalTransitionException>(() => sm.Apply(new StateInput.StartBudget(0)));
        Assert.Equal(FocusState.Blocked, sm.Current);
    }

    [Fact]
    public void Blocked_AdminPause_GoesPausedWithEndAt()
    {
        var sm = new StateMachine();
        var t = sm.Apply(new StateInput.AdminPause(PauseUntil));
        Assert.Equal(FocusState.Paused, t.To);
        Assert.Equal(PauseUntil, t.PauseEndAt);
        Assert.Equal(NetworkPosture.Open, t.Posture);
    }

    [Fact]
    public void Blocked_AdminDisable_GoesDisabled()
    {
        var sm = new StateMachine();
        var t = sm.Apply(new StateInput.AdminDisable());
        Assert.Equal(FocusState.Disabled, t.To);
        Assert.Equal(NetworkPosture.Open, t.Posture);
    }

    [Fact]
    public void Blocked_StopBudget_IsIllegal()
    {
        var sm = new StateMachine();
        Assert.Throws<IllegalTransitionException>(() => sm.Apply(new StateInput.StopBudget()));
    }

    [Fact]
    public void Blocked_DailyRollover_IsNoOp()
    {
        var sm = new StateMachine();
        var t = sm.Apply(new StateInput.DailyRollover());
        Assert.Equal(FocusState.Blocked, t.To);
    }

    // ---- BROWSING ----

    [Fact]
    public void Browsing_StopBudget_GoesBlocked()
    {
        var sm = new StateMachine(FocusState.Browsing);
        var t = sm.Apply(new StateInput.StopBudget());
        Assert.Equal(FocusState.Blocked, t.To);
        Assert.Equal(NetworkPosture.Closed, t.Posture);
    }

    [Fact]
    public void Browsing_BudgetExhausted_GoesBlocked()
    {
        var sm = new StateMachine(FocusState.Browsing);
        var t = sm.Apply(new StateInput.BudgetExhausted());
        Assert.Equal(FocusState.Blocked, t.To);
    }

    [Fact]
    public void Browsing_DailyRollover_EndsSession()
    {
        var sm = new StateMachine(FocusState.Browsing);
        var t = sm.Apply(new StateInput.DailyRollover());
        Assert.Equal(FocusState.Blocked, t.To);
    }

    [Fact]
    public void Browsing_ClockTamper_EndsSession()
    {
        var sm = new StateMachine(FocusState.Browsing);
        var t = sm.Apply(new StateInput.ClockTamperDetected());
        Assert.Equal(FocusState.Blocked, t.To);
    }

    [Fact]
    public void Browsing_AdminPause_GoesPaused()
    {
        var sm = new StateMachine(FocusState.Browsing);
        var t = sm.Apply(new StateInput.AdminPause(PauseUntil));
        Assert.Equal(FocusState.Paused, t.To);
        Assert.Equal(PauseUntil, t.PauseEndAt);
    }

    [Fact]
    public void Browsing_StartBudget_IsIllegal()
    {
        var sm = new StateMachine(FocusState.Browsing);
        Assert.Throws<IllegalTransitionException>(() => sm.Apply(new StateInput.StartBudget(5)));
    }

    // ---- PAUSED ----

    [Fact]
    public void Paused_AdminEndPause_GoesBlocked()
    {
        var sm = new StateMachine(FocusState.Paused, PauseUntil);
        var t = sm.Apply(new StateInput.AdminEndPause());
        Assert.Equal(FocusState.Blocked, t.To);
        Assert.Null(t.PauseEndAt);
    }

    [Fact]
    public void Paused_PauseExpired_GoesBlocked()
    {
        var sm = new StateMachine(FocusState.Paused, PauseUntil);
        var t = sm.Apply(new StateInput.PauseExpired());
        Assert.Equal(FocusState.Blocked, t.To);
        Assert.Null(t.PauseEndAt);
    }

    [Fact]
    public void Paused_AdminDisable_GoesDisabled()
    {
        var sm = new StateMachine(FocusState.Paused, PauseUntil);
        var t = sm.Apply(new StateInput.AdminDisable());
        Assert.Equal(FocusState.Disabled, t.To);
        Assert.Null(t.PauseEndAt);
    }

    [Fact]
    public void Paused_DailyRollover_StaysPaused()
    {
        var sm = new StateMachine(FocusState.Paused, PauseUntil);
        var t = sm.Apply(new StateInput.DailyRollover());
        Assert.Equal(FocusState.Paused, t.To);
        Assert.Equal(PauseUntil, t.PauseEndAt);
    }

    [Fact]
    public void Paused_StartBudget_IsIllegal()
    {
        var sm = new StateMachine(FocusState.Paused, PauseUntil);
        Assert.Throws<IllegalTransitionException>(() => sm.Apply(new StateInput.StartBudget(60)));
    }

    // ---- DISABLED ----

    [Fact]
    public void Disabled_AdminEnable_GoesBlocked()
    {
        var sm = new StateMachine(FocusState.Disabled);
        var t = sm.Apply(new StateInput.AdminEnable());
        Assert.Equal(FocusState.Blocked, t.To);
    }

    [Fact]
    public void Disabled_StartBudget_IsIllegal()
    {
        var sm = new StateMachine(FocusState.Disabled);
        Assert.Throws<IllegalTransitionException>(() => sm.Apply(new StateInput.StartBudget(60)));
    }

    [Fact]
    public void Disabled_DailyRollover_IsNoOp()
    {
        var sm = new StateMachine(FocusState.Disabled);
        var t = sm.Apply(new StateInput.DailyRollover());
        Assert.Equal(FocusState.Disabled, t.To);
    }

    [Fact]
    public void Disabled_AdminEndPause_IsIllegal()
    {
        var sm = new StateMachine(FocusState.Disabled);
        Assert.Throws<IllegalTransitionException>(() => sm.Apply(new StateInput.AdminEndPause()));
    }

    // ---- Posture mapping ----

    [Theory]
    [InlineData(FocusState.Blocked, NetworkPosture.Closed)]
    [InlineData(FocusState.Browsing, NetworkPosture.Open)]
    [InlineData(FocusState.Paused, NetworkPosture.Open)]
    [InlineData(FocusState.Disabled, NetworkPosture.Open)]
    public void PostureFor_MatchesPlan(FocusState state, NetworkPosture expected)
    {
        Assert.Equal(expected, StateMachine.PostureFor(state));
    }
}
