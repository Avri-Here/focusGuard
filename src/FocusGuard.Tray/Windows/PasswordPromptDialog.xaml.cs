using System.Runtime.Versioning;
using System.Windows;
using FocusGuard.Core.Ipc;
using FocusGuard.Tray.Services;

namespace FocusGuard.Tray.Windows;

/// <summary>
/// Verifies the admin password before the AdminWindow opens. We use
/// <see cref="IpcCommands.AdminEndPause"/> as a cheap probe: if the pipe responds with
/// success OR with the specific "not currently paused" error the password was correct.
/// Any other error means wrong password / lockout / transport failure.
/// </summary>
[SupportedOSPlatform("windows")]
public partial class PasswordPromptDialog : Window
{
    private readonly TrayClient _client;

    public string? VerifiedPassword { get; private set; }

    public PasswordPromptDialog(TrayClient client)
    {
        InitializeComponent();
        _client = client;
        Loaded += (_, _) => PwdBox.Focus();
    }

    private void OnCancelClick(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private async void OnOkClick(object sender, RoutedEventArgs e)
    {
        ErrorText.Text = string.Empty;
        var pwd = PwdBox.Password ?? string.Empty;
        if (string.IsNullOrEmpty(pwd))
        {
            ErrorText.Text = "Password required.";
            return;
        }

        OkButton.IsEnabled = false;
        try
        {
            var resp = await _client.SendAsync(IpcCommands.AdminEndPause, new AdminEndPauseRequest(Password: pwd));

            if (resp.Success || IsBenignError(resp.Error))
            {
                VerifiedPassword = pwd;
                DialogResult = true;
                Close();
                return;
            }

            ErrorText.Text = resp.Error ?? "Failed.";
        }
        finally
        {
            OkButton.IsEnabled = true;
        }
    }

    /// <summary>
    /// "not currently paused" means the password matched but there was nothing to end.
    /// Anything else (incorrect password / locked out / transport failure) is an actual rejection.
    /// </summary>
    private static bool IsBenignError(string? error)
    {
        if (string.IsNullOrEmpty(error)) return false;
        return error.Contains("not currently paused", StringComparison.OrdinalIgnoreCase);
    }
}
