using System.Globalization;
using System.Runtime.Versioning;
using System.Windows;
using System.Windows.Threading;
using FocusGuard.Core.Ipc;
using FocusGuard.Tray.Services;

namespace FocusGuard.Tray.Windows;

/// <summary>
/// Admin-only window. The caller (App) must verify the password through
/// <see cref="PasswordPromptDialog"/> before opening this window; the verified password is
/// passed in and cached for the lifetime of the window. A 1.5s status poller refreshes
/// the footer + whitelist box + Disable/Enable button label.
/// </summary>
[SupportedOSPlatform("windows")]
public partial class AdminWindow : Window
{
    private readonly TrayClient _client;
    private string _password;
    private readonly DispatcherTimer _poller;
    private FocusState _lastState = FocusState.Blocked;

    public AdminWindow(TrayClient client, string password)
    {
        InitializeComponent();
        _client = client;
        _password = password;

        _poller = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1500) };
        _poller.Tick += async (_, _) => await RefreshAsync();
        Loaded += async (_, _) =>
        {
            await RefreshAsync();
            _poller.Start();
        };
        Closed += (_, _) => _poller.Stop();
    }

    private async Task RefreshAsync()
    {
        var status = await _client.TryGetStatusAsync();
        if (status is null)
        {
            StatusFooter.Text = "Service unavailable";
            return;
        }

        _lastState = status.State;
        StatusFooter.Text = $"Status: {status.State} — {status.MinutesRemaining:F1} minutes remaining today" +
            (status.PauseEndAt is { } end ? $" (pause until {end.ToLocalTime():HH:mm})" : string.Empty);

        // Whitelist box: only repopulate when the contents actually change so user selection sticks.
        if (!WhitelistMatches(status.Whitelist))
        {
            var selected = WhitelistBox.SelectedItem as string;
            WhitelistBox.Items.Clear();
            foreach (var d in status.Whitelist) WhitelistBox.Items.Add(d);
            if (selected is not null && status.Whitelist.Contains(selected))
                WhitelistBox.SelectedItem = selected;
        }

        ToggleDisableButton.Content = status.State == FocusState.Disabled
            ? "Enable FocusGuard"
            : "Disable FocusGuard";
    }

    private bool WhitelistMatches(IReadOnlyList<string> incoming)
    {
        if (WhitelistBox.Items.Count != incoming.Count) return false;
        for (var i = 0; i < incoming.Count; i++)
        {
            if (!string.Equals(WhitelistBox.Items[i] as string, incoming[i], StringComparison.Ordinal))
                return false;
        }
        return true;
    }

    // ---- Pause tab ----

    private async void OnPauseClick(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(PauseMinutesBox.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var minutes) || minutes <= 0)
        {
            ShowDialog("Enter a positive number of minutes.");
            return;
        }
        var resp = await _client.SendAsync(IpcCommands.AdminPause,
            new AdminPauseRequest(Password: _password, DurationMinutes: minutes));
        ReportIfFailure(resp);
        await RefreshAsync();
    }

    private async void OnEndPauseClick(object sender, RoutedEventArgs e)
    {
        var resp = await _client.SendAsync(IpcCommands.AdminEndPause,
            new AdminEndPauseRequest(Password: _password));
        ReportIfFailure(resp);
        await RefreshAsync();
    }

    // ---- Whitelist tab ----

    private async void OnAddWhitelistClick(object sender, RoutedEventArgs e)
    {
        var domain = (WhitelistInputBox.Text ?? string.Empty).Trim();
        if (string.IsNullOrEmpty(domain)) return;

        var resp = await _client.SendAsync(IpcCommands.AddWhitelist,
            new AddWhitelistRequest(Password: _password, Domain: domain));
        if (resp.Success) WhitelistInputBox.Text = string.Empty;
        ReportIfFailure(resp);
        await RefreshAsync();
    }

    private async void OnRemoveWhitelistClick(object sender, RoutedEventArgs e)
    {
        if (WhitelistBox.SelectedItem is not string domain) return;
        var resp = await _client.SendAsync(IpcCommands.RemoveWhitelist,
            new RemoveWhitelistRequest(Password: _password, Domain: domain));
        ReportIfFailure(resp);
        await RefreshAsync();
    }

    // ---- Disable/Enable tab ----

    private async void OnToggleDisableClick(object sender, RoutedEventArgs e)
    {
        IpcResponseEnvelope resp;
        if (_lastState == FocusState.Disabled)
        {
            resp = await _client.SendAsync(IpcCommands.Enable, new EnableRequest(Password: _password));
        }
        else
        {
            resp = await _client.SendAsync(IpcCommands.Disable, new DisableRequest(Password: _password));
        }
        ReportIfFailure(resp);
        await RefreshAsync();
    }

    // ---- Change password tab ----

    private async void OnChangePasswordClick(object sender, RoutedEventArgs e)
    {
        ChangePwdMessage.Text = string.Empty;
        ChangePwdMessage.Foreground = System.Windows.Media.Brushes.Black;

        var oldPwd = OldPwdBox.Password ?? string.Empty;
        var newPwd = NewPwdBox.Password ?? string.Empty;
        var confirm = ConfirmPwdBox.Password ?? string.Empty;
        if (string.IsNullOrEmpty(oldPwd) || string.IsNullOrEmpty(newPwd))
        {
            ChangePwdMessage.Foreground = System.Windows.Media.Brushes.Crimson;
            ChangePwdMessage.Text = "All fields required.";
            return;
        }
        if (newPwd != confirm)
        {
            ChangePwdMessage.Foreground = System.Windows.Media.Brushes.Crimson;
            ChangePwdMessage.Text = "New passwords don't match.";
            return;
        }

        var resp = await _client.SendAsync(IpcCommands.SetPassword,
            new SetPasswordRequest(OldPassword: oldPwd, NewPassword: newPwd));
        if (!resp.Success)
        {
            ChangePwdMessage.Foreground = System.Windows.Media.Brushes.Crimson;
            ChangePwdMessage.Text = resp.Error ?? "Failed.";
            return;
        }

        // Cache the new password for subsequent admin operations in this window.
        _password = newPwd;
        OldPwdBox.Clear();
        NewPwdBox.Clear();
        ConfirmPwdBox.Clear();
        ChangePwdMessage.Foreground = System.Windows.Media.Brushes.SeaGreen;
        ChangePwdMessage.Text = "Password updated.";
    }

    private void ReportIfFailure(IpcResponseEnvelope resp)
    {
        if (resp.Success) return;
        ShowDialog(resp.Error ?? "Operation failed.");
    }

    private void ShowDialog(string message)
    {
        MessageBox.Show(this, message, "FocusGuard", MessageBoxButton.OK, MessageBoxImage.Information);
    }
}
