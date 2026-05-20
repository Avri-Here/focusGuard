using System.Runtime.Versioning;
using System.Windows;
using FocusGuard.Core.Ipc;
using FocusGuard.Tray.Services;

namespace FocusGuard.Tray.Windows;

/// <summary>
/// First-launch wizard. Sends <see cref="IpcCommands.SetPassword"/> with empty old password.
/// Stays open until the service confirms success or the user cancels.
/// </summary>
[SupportedOSPlatform("windows")]
public partial class SetPasswordWindow : Window
{
    private readonly TrayClient _client;

    public string? Password { get; private set; }

    public SetPasswordWindow(TrayClient client)
    {
        InitializeComponent();
        _client = client;
    }

    private async void OnSubmitClick(object sender, RoutedEventArgs e)
    {
        ErrorText.Text = string.Empty;
        var pwd = NewPwdBox.Password ?? string.Empty;
        var confirm = ConfirmPwdBox.Password ?? string.Empty;

        if (string.IsNullOrEmpty(pwd))
        {
            ErrorText.Text = "Password must not be empty.";
            return;
        }
        if (pwd.Length < 4)
        {
            ErrorText.Text = "Use at least 4 characters.";
            return;
        }
        if (pwd != confirm)
        {
            ErrorText.Text = "Passwords don't match.";
            return;
        }

        SubmitButton.IsEnabled = false;
        try
        {
            var resp = await _client.SendAsync(IpcCommands.SetPassword,
                new SetPasswordRequest(OldPassword: string.Empty, NewPassword: pwd));
            if (!resp.Success)
            {
                ErrorText.Text = resp.Error ?? "Failed to set password.";
                return;
            }
            Password = pwd;
            DialogResult = true;
            Close();
        }
        finally
        {
            SubmitButton.IsEnabled = true;
        }
    }
}
