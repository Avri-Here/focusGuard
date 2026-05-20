using System.Text.Json;
using FocusGuard.Core;
using FocusGuard.Core.Audit;
using FocusGuard.Core.Ipc;

namespace FocusGuard.Service.Tests;

public class AdminCommandsTests
{
    private const string CorrectPwd = "correct horse battery";
    private const string WrongPwd = "wrong";

    private static IpcRequestEnvelope Req(string cmd, object payload) =>
        new(cmd, JsonSerializer.SerializeToElement(payload, IpcJson.Options));

    private static FocusGuardConfig WithPassword(WorkerHarness harness, string password)
    {
        var cfg = new FocusGuardConfig { PasswordHash = harness.Hasher.Hash(password) };
        harness.ConfigStore.Save(cfg);
        return cfg;
    }

    // ---- SetPassword ----

    [Fact]
    public async Task SetPassword_first_time_setup_succeeds_when_no_password_is_configured()
    {
        var harness = WorkerHarness.Build();
        await harness.StartAsync();

        var resp = await harness.Worker.HandleAsync(
            Req(IpcCommands.SetPassword, new SetPasswordRequest(OldPassword: "", NewPassword: "swordfish42")),
            CancellationToken.None);

        Assert.True(resp.Success);
        var cfg = harness.ConfigStore.Load()!;
        Assert.True(harness.Hasher.Verify("swordfish42", cfg.PasswordHash!));
        Assert.Contains(harness.Audit.Entries, e => e.Category == AuditCategory.AdminAction);
    }

    [Fact]
    public async Task SetPassword_rotation_requires_correct_old_password()
    {
        var harness = WorkerHarness.Build();
        WithPassword(harness, CorrectPwd);
        await harness.StartAsync();

        var resp = await harness.Worker.HandleAsync(
            Req(IpcCommands.SetPassword, new SetPasswordRequest(OldPassword: WrongPwd, NewPassword: "new")),
            CancellationToken.None);

        Assert.False(resp.Success);
        Assert.Contains(harness.Audit.Entries, e => e.Category == AuditCategory.AuthFailure);
    }

    [Fact]
    public async Task SetPassword_rotation_with_correct_old_password_replaces_hash()
    {
        var harness = WorkerHarness.Build();
        WithPassword(harness, CorrectPwd);
        await harness.StartAsync();

        var resp = await harness.Worker.HandleAsync(
            Req(IpcCommands.SetPassword, new SetPasswordRequest(OldPassword: CorrectPwd, NewPassword: "newer")),
            CancellationToken.None);

        Assert.True(resp.Success);
        var cfg = harness.ConfigStore.Load()!;
        Assert.True(harness.Hasher.Verify("newer", cfg.PasswordHash!));
        Assert.False(harness.Hasher.Verify(CorrectPwd, cfg.PasswordHash!));
    }

    // ---- AdminPause / AdminEndPause ----

    [Fact]
    public async Task AdminPause_with_correct_password_transitions_to_Paused_and_opens_posture()
    {
        var harness = WorkerHarness.Build();
        WithPassword(harness, CorrectPwd);
        await harness.StartAsync();

        var resp = await harness.Worker.HandleAsync(
            Req(IpcCommands.AdminPause, new AdminPauseRequest(Password: CorrectPwd, DurationMinutes: 15)),
            CancellationToken.None);

        Assert.True(resp.Success);
        var snapshot = await harness.Worker.SnapshotForTestsAsync();
        Assert.Equal(FocusState.Paused, snapshot.State);
        Assert.Equal(NetworkPosture.Open, harness.Firewall.LastPosture);
        Assert.NotNull(snapshot.PauseEndAt);
        Assert.True(snapshot.PauseEndAt!.Value > harness.Clock.UtcNow);
    }

    [Fact]
    public async Task AdminPause_with_wrong_password_does_not_transition_and_logs_AuthFailure()
    {
        var harness = WorkerHarness.Build();
        WithPassword(harness, CorrectPwd);
        await harness.StartAsync();

        var resp = await harness.Worker.HandleAsync(
            Req(IpcCommands.AdminPause, new AdminPauseRequest(Password: WrongPwd, DurationMinutes: 15)),
            CancellationToken.None);

        Assert.False(resp.Success);
        var snapshot = await harness.Worker.SnapshotForTestsAsync();
        Assert.Equal(FocusState.Blocked, snapshot.State);
        Assert.Contains(harness.Audit.Entries, e => e.Category == AuditCategory.AuthFailure);
    }

    [Fact]
    public async Task AdminPause_uses_config_default_when_DurationMinutes_is_zero_or_negative()
    {
        var harness = WorkerHarness.Build();
        var cfg = WithPassword(harness, CorrectPwd);
        cfg.AdminPauseDefaultMinutes = 30;
        harness.ConfigStore.Save(cfg);
        await harness.StartAsync();

        var resp = await harness.Worker.HandleAsync(
            Req(IpcCommands.AdminPause, new AdminPauseRequest(Password: CorrectPwd, DurationMinutes: 0)),
            CancellationToken.None);

        Assert.True(resp.Success);
        var snapshot = await harness.Worker.SnapshotForTestsAsync();
        var expectedUntil = harness.Clock.UtcNow + TimeSpan.FromMinutes(30);
        Assert.True((snapshot.PauseEndAt!.Value - expectedUntil).Duration() < TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task AdminEndPause_with_correct_password_returns_to_Blocked()
    {
        var harness = WorkerHarness.Build();
        WithPassword(harness, CorrectPwd);
        await harness.StartAsync();

        await harness.Worker.HandleAsync(
            Req(IpcCommands.AdminPause, new AdminPauseRequest(Password: CorrectPwd, DurationMinutes: 15)),
            CancellationToken.None);

        var resp = await harness.Worker.HandleAsync(
            Req(IpcCommands.AdminEndPause, new AdminEndPauseRequest(Password: CorrectPwd)),
            CancellationToken.None);

        Assert.True(resp.Success);
        var snapshot = await harness.Worker.SnapshotForTestsAsync();
        Assert.Equal(FocusState.Blocked, snapshot.State);
    }

    // ---- Whitelist ----

    [Fact]
    public async Task AddWhitelist_normalizes_to_lowercase_and_strips_trailing_dot()
    {
        var harness = WorkerHarness.Build();
        WithPassword(harness, CorrectPwd);
        await harness.StartAsync();

        var resp = await harness.Worker.HandleAsync(
            Req(IpcCommands.AddWhitelist, new AddWhitelistRequest(Password: CorrectPwd, Domain: "Example.COM.")),
            CancellationToken.None);

        Assert.True(resp.Success);
        var cfg = harness.ConfigStore.Load()!;
        Assert.Contains("example.com", cfg.Whitelist);
        Assert.DoesNotContain("Example.COM.", cfg.Whitelist);
    }

    [Theory]
    [InlineData("https://example.com")] // scheme
    [InlineData("example.com/path")]    // slash
    [InlineData("example com")]          // space
    [InlineData("nodot")]                // no dot
    [InlineData("")]                      // empty
    public async Task AddWhitelist_rejects_garbage_input(string raw)
    {
        var harness = WorkerHarness.Build();
        WithPassword(harness, CorrectPwd);
        await harness.StartAsync();

        var resp = await harness.Worker.HandleAsync(
            Req(IpcCommands.AddWhitelist, new AddWhitelistRequest(Password: CorrectPwd, Domain: raw)),
            CancellationToken.None);

        Assert.False(resp.Success);
        var cfg = harness.ConfigStore.Load()!;
        Assert.Empty(cfg.Whitelist);
    }

    [Fact]
    public async Task AddWhitelist_does_not_duplicate_existing_entries()
    {
        var harness = WorkerHarness.Build();
        var cfg = WithPassword(harness, CorrectPwd);
        cfg.Whitelist.Add("example.com");
        harness.ConfigStore.Save(cfg);
        await harness.StartAsync();

        var resp = await harness.Worker.HandleAsync(
            Req(IpcCommands.AddWhitelist, new AddWhitelistRequest(Password: CorrectPwd, Domain: "EXAMPLE.com")),
            CancellationToken.None);

        Assert.True(resp.Success);
        var loaded = harness.ConfigStore.Load()!;
        Assert.Single(loaded.Whitelist, "example.com");
    }

    [Fact]
    public async Task RemoveWhitelist_strips_normalized_entry()
    {
        var harness = WorkerHarness.Build();
        var cfg = WithPassword(harness, CorrectPwd);
        cfg.Whitelist.Add("example.com");
        cfg.Whitelist.Add("other.org");
        harness.ConfigStore.Save(cfg);
        await harness.StartAsync();

        var resp = await harness.Worker.HandleAsync(
            Req(IpcCommands.RemoveWhitelist, new RemoveWhitelistRequest(Password: CorrectPwd, Domain: "Example.COM.")),
            CancellationToken.None);

        Assert.True(resp.Success);
        var loaded = harness.ConfigStore.Load()!;
        Assert.DoesNotContain("example.com", loaded.Whitelist);
        Assert.Contains("other.org", loaded.Whitelist);
    }

    // ---- Disable / Enable ----

    [Fact]
    public async Task Disable_with_correct_password_transitions_to_Disabled_and_restores_adapters()
    {
        var harness = WorkerHarness.Build();
        WithPassword(harness, CorrectPwd);
        await harness.StartAsync();

        var resp = await harness.Worker.HandleAsync(
            Req(IpcCommands.Disable, new DisableRequest(Password: CorrectPwd)),
            CancellationToken.None);

        Assert.True(resp.Success);
        var snapshot = await harness.Worker.SnapshotForTestsAsync();
        Assert.Equal(FocusState.Disabled, snapshot.State);
        Assert.True(harness.Adapters.RestoreCalls > 0);
    }

    [Fact]
    public async Task Disable_refuses_when_no_password_is_configured()
    {
        var harness = WorkerHarness.Build();
        await harness.StartAsync();

        var resp = await harness.Worker.HandleAsync(
            Req(IpcCommands.Disable, new DisableRequest(Password: "")),
            CancellationToken.None);

        Assert.False(resp.Success);
        var snapshot = await harness.Worker.SnapshotForTestsAsync();
        Assert.Equal(FocusState.Blocked, snapshot.State);
    }

    [Fact]
    public async Task Enable_from_Disabled_with_correct_password_returns_to_Blocked()
    {
        var harness = WorkerHarness.Build();
        WithPassword(harness, CorrectPwd);
        await harness.StartAsync();

        await harness.Worker.HandleAsync(
            Req(IpcCommands.Disable, new DisableRequest(Password: CorrectPwd)),
            CancellationToken.None);
        var beforeOverride = harness.Adapters.OverrideCalls;

        var resp = await harness.Worker.HandleAsync(
            Req(IpcCommands.Enable, new EnableRequest(Password: CorrectPwd)),
            CancellationToken.None);

        Assert.True(resp.Success);
        var snapshot = await harness.Worker.SnapshotForTestsAsync();
        Assert.Equal(FocusState.Blocked, snapshot.State);
        Assert.True(harness.Adapters.OverrideCalls > beforeOverride);
    }

    // ---- Lockout ----

    [Fact]
    public async Task Five_wrong_passwords_lock_out_subsequent_attempts()
    {
        var harness = WorkerHarness.Build();
        WithPassword(harness, CorrectPwd);
        await harness.StartAsync();

        for (var i = 0; i < 5; i++)
        {
            await harness.Worker.HandleAsync(
                Req(IpcCommands.AdminPause, new AdminPauseRequest(Password: WrongPwd, DurationMinutes: 15)),
                CancellationToken.None);
        }

        // 6th attempt — even with the correct password — must be rejected with a lockout error.
        var resp = await harness.Worker.HandleAsync(
            Req(IpcCommands.AdminPause, new AdminPauseRequest(Password: CorrectPwd, DurationMinutes: 15)),
            CancellationToken.None);

        Assert.False(resp.Success);
        Assert.Contains("locked", resp.Error, StringComparison.OrdinalIgnoreCase);
        var snapshot = await harness.Worker.SnapshotForTestsAsync();
        Assert.Equal(FocusState.Blocked, snapshot.State);
    }

    [Fact]
    public async Task Lockout_clears_after_cooldown_and_correct_password_then_works()
    {
        var harness = WorkerHarness.Build();
        WithPassword(harness, CorrectPwd);
        await harness.StartAsync();

        for (var i = 0; i < 5; i++)
        {
            await harness.Worker.HandleAsync(
                Req(IpcCommands.AdminPause, new AdminPauseRequest(Password: WrongPwd, DurationMinutes: 15)),
                CancellationToken.None);
        }

        harness.Clock.Advance(TimeSpan.FromSeconds(31));

        var resp = await harness.Worker.HandleAsync(
            Req(IpcCommands.AdminPause, new AdminPauseRequest(Password: CorrectPwd, DurationMinutes: 15)),
            CancellationToken.None);

        Assert.True(resp.Success);
        var snapshot = await harness.Worker.SnapshotForTestsAsync();
        Assert.Equal(FocusState.Paused, snapshot.State);
    }

    [Fact]
    public async Task Successful_command_resets_failure_counter()
    {
        var harness = WorkerHarness.Build();
        WithPassword(harness, CorrectPwd);
        await harness.StartAsync();

        // 4 failures (one short of lockout).
        for (var i = 0; i < 4; i++)
        {
            await harness.Worker.HandleAsync(
                Req(IpcCommands.AdminPause, new AdminPauseRequest(Password: WrongPwd, DurationMinutes: 15)),
                CancellationToken.None);
        }
        // One success resets the counter.
        await harness.Worker.HandleAsync(
            Req(IpcCommands.AdminPause, new AdminPauseRequest(Password: CorrectPwd, DurationMinutes: 15)),
            CancellationToken.None);
        // End the pause so the next AdminPause is allowed.
        await harness.Worker.HandleAsync(
            Req(IpcCommands.AdminEndPause, new AdminEndPauseRequest(Password: CorrectPwd)),
            CancellationToken.None);

        // Four more failures must NOT lock us out (counter was reset).
        for (var i = 0; i < 4; i++)
        {
            await harness.Worker.HandleAsync(
                Req(IpcCommands.AdminPause, new AdminPauseRequest(Password: WrongPwd, DurationMinutes: 15)),
                CancellationToken.None);
        }

        var resp = await harness.Worker.HandleAsync(
            Req(IpcCommands.AdminPause, new AdminPauseRequest(Password: CorrectPwd, DurationMinutes: 15)),
            CancellationToken.None);
        Assert.True(resp.Success);
    }
}
