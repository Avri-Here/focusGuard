using System.Drawing;
using System.Runtime.Versioning;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using FocusGuard.Core.Ipc;
using FocusGuard.Tray.Services;
using FocusGuard.Tray.Windows;
using H.NotifyIcon;

namespace FocusGuard.Tray;

/// <summary>
/// Tray host. Owns:
/// - the single-instance mutex (per session)
/// - the H.NotifyIcon TaskbarIcon and its context menu
/// - the 2 Hz status poller
///
/// The remaining-time display is the tray icon's tooltip (e.g.
/// "Browsing — 41.3 min left"); we deliberately do NOT show a floating
/// countdown window. Past versions did; it was removed because users
/// described it as nagging. The tooltip is "show on hover" by design.
/// </summary>
[SupportedOSPlatform("windows7.0")]
public partial class App : Application
{
    private const string MutexName = @"Global\FocusGuard.Tray";

    private Mutex? _instanceMutex;
    private bool _ownsMutex;
    private TaskbarIcon? _trayIcon;
    private DispatcherTimer? _poller;
    private TrayClient _client = new();
    private MenuItem? _startItem;
    private MenuItem? _stopItem;
    private MenuItem? _adminItem;
    private MenuItem? _statusItem;
    // Cache icon BYTES, not Icon instances. H.NotifyIcon's Icon setter disposes
    // the previously-assigned Icon, so a cached instance becomes invalid after
    // the first state change. See IconFactory for the full rationale.
    private byte[]? _appIconBytes;
    private FocusState? _lastState;
    private bool _passwordSetupShown;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _instanceMutex = new Mutex(initiallyOwned: true, MutexName, out _ownsMutex);
        if (!_ownsMutex)
        {
            // Another tray instance already running in this session — quit silently.
            Shutdown();
            return;
        }

        try
        {
            _appIconBytes = IconFactory.LoadAppIconBytes();
        }
        catch (Exception)
        {
            // If we can't load the icon we still continue without one. The tray-icon component
            // tolerates a null Icon.
        }

        BuildTrayIcon();

        // First-launch wizard: poll once before letting the tray be interactive.
        await EnsurePasswordSetupAsync();

        StartPoller();
    }

    private void BuildTrayIcon()
    {
        var menu = new ContextMenu();

        _startItem = new MenuItem { Header = "Start my time" };
        _startItem.Click += async (_, _) => await OnStartClickAsync();
        menu.Items.Add(_startItem);

        _stopItem = new MenuItem { Header = "Stop" };
        _stopItem.Click += async (_, _) => await OnStopClickAsync();
        menu.Items.Add(_stopItem);

        menu.Items.Add(new Separator());

        _adminItem = new MenuItem { Header = "Admin…" };
        _adminItem.Click += async (_, _) => await OnAdminClickAsync();
        menu.Items.Add(_adminItem);

        _statusItem = new MenuItem { Header = "Status: connecting…", IsEnabled = false };
        menu.Items.Add(_statusItem);

        // Intentionally NO "Exit" item — tamper resistance. The watchdog
        // would respawn the tray within ~5s anyway, but exposing a quit
        // affordance invites the user to fight it. Disable from the
        // Admin window (password-gated) is the supported way to silence
        // FocusGuard.

        _trayIcon = new TaskbarIcon
        {
            ToolTipText = "FocusGuard",
            Icon = MakeIcon(_appIconBytes),
            ContextMenu = menu,
        };
        _trayIcon.ForceCreate(enablesEfficiencyMode: false);
    }

    private static Icon? MakeIcon(byte[]? bytes)
    {
        if (bytes is null) return null;
        try { return IconFactory.FromBytes(bytes); }
        catch { return null; }
    }

    private async Task EnsurePasswordSetupAsync()
    {
        if (_passwordSetupShown) return;
        var status = await _client.TryGetStatusAsync();
        if (status is null) return; // service unavailable — poller will retry; admin gate covers if user clicks
        if (!status.RequiresPasswordSetup) return;

        _passwordSetupShown = true;
        var w = new SetPasswordWindow(_client);
        w.ShowDialog();
        // Whether they completed or cancelled is acceptable here — the cancel button is "X" only,
        // and cancellation just means the next poll will detect setup still required.
    }

    private void StartPoller()
    {
        _poller = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _poller.Tick += async (_, _) => await PollOnceAsync();
        _poller.Start();
        _ = PollOnceAsync();
    }

    private async Task PollOnceAsync()
    {
        var status = await _client.TryGetStatusAsync();
        ApplyStatus(status);
    }

    private void ApplyStatus(StatusResponse? status)
    {
        if (_trayIcon is null) return;

        if (status is null)
        {
            _trayIcon.ToolTipText = "FocusGuard — service unavailable";
            if (_statusItem is not null) _statusItem.Header = "Status: service unavailable";
            SetMenuEnabled(start: false, stop: false, admin: false);
            _trayIcon.Icon = MakeIcon(_appIconBytes);
            _lastState = null;
            return;
        }

        if (status.RequiresPasswordSetup)
        {
            _trayIcon.ToolTipText = "FocusGuard — set password to begin";
            if (_statusItem is not null) _statusItem.Header = "Status: password not set";
            SetMenuEnabled(start: false, stop: false, admin: false);
            _trayIcon.Icon = MakeIcon(_appIconBytes);
            // Re-prompt only if we haven't shown it yet this session.
            if (!_passwordSetupShown) _ = EnsurePasswordSetupAsync();
            _lastState = null;
            return;
        }

        var label = status.State switch
        {
            FocusState.Blocked => $"Blocked — {status.MinutesRemaining:F1} min left today",
            FocusState.Browsing => $"Browsing — {status.MinutesRemaining:F1} min left",
            FocusState.Paused => "Paused" + (status.PauseEndAt is { } end ? $" until {end.ToLocalTime():HH:mm}" : string.Empty),
            FocusState.Disabled => "Disabled",
            _ => status.State.ToString(),
        };
        _trayIcon.ToolTipText = "FocusGuard — " + label;
        if (_statusItem is not null) _statusItem.Header = "Status: " + label;

        SetMenuEnabled(
            start: status.State == FocusState.Blocked && status.MinutesRemaining > 0,
            stop: status.State == FocusState.Browsing,
            admin: true);

        // Single app icon for all states — state is conveyed via the tooltip text
        // (e.g. "FocusGuard — Browsing — 41.3 min left").
        _trayIcon.Icon = MakeIcon(_appIconBytes);

        _lastState = status.State;
    }

    private void SetMenuEnabled(bool start, bool stop, bool admin)
    {
        if (_startItem is not null) _startItem.IsEnabled = start;
        if (_stopItem is not null) _stopItem.IsEnabled = stop;
        if (_adminItem is not null) _adminItem.IsEnabled = admin;
    }

    private async Task OnStartClickAsync()
    {
        var resp = await _client.SendAsync(IpcCommands.StartBudget, new StartBudgetRequest());
        if (!resp.Success && resp.Error is not null)
            ShowMessage(resp.Error);
        await PollOnceAsync();
    }

    private async Task OnStopClickAsync()
    {
        var resp = await _client.SendAsync(IpcCommands.StopBudget, new StopBudgetRequest());
        if (!resp.Success && resp.Error is not null)
            ShowMessage(resp.Error);
        await PollOnceAsync();
    }

    private async Task OnAdminClickAsync()
    {
        // Make sure the service is even reachable before bothering the user with a prompt.
        var status = await _client.TryGetStatusAsync();
        if (status is null)
        {
            ShowMessage("Service unavailable.");
            return;
        }
        if (status.RequiresPasswordSetup)
        {
            await EnsurePasswordSetupAsync();
            return;
        }

        var prompt = new PasswordPromptDialog(_client);
        if (prompt.ShowDialog() != true || prompt.VerifiedPassword is null) return;

        var admin = new AdminWindow(_client, prompt.VerifiedPassword);
        admin.Show();
    }

    private static void ShowMessage(string text)
    {
        MessageBox.Show(text, "FocusGuard", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try
        {
            _poller?.Stop();
            _trayIcon?.Dispose();
            // Icon instances are owned by H.NotifyIcon (it disposes on assignment),
            // so we have nothing to free here beyond the tray itself.
        }
        catch { /* best effort */ }

        if (_ownsMutex && _instanceMutex is not null)
        {
            try { _instanceMutex.ReleaseMutex(); } catch { /* best effort */ }
            _instanceMutex.Dispose();
            _instanceMutex = null;
        }

        base.OnExit(e);
    }
}
