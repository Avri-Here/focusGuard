using System.Runtime.Versioning;
using System.Windows;
using FocusGuard.Core.Ipc;

namespace FocusGuard.Tray.Windows;

/// <summary>
/// Floating MM:SS card pinned to the bottom-right of the primary screen. Topmost, transparent
/// chrome. Displayed only while the service reports <see cref="FocusState.Browsing"/>; the
/// caller (App) drives <see cref="UpdateFromStatus"/> from its existing status poll.
/// </summary>
[SupportedOSPlatform("windows")]
public partial class CountdownWindow : Window
{
    public CountdownWindow()
    {
        InitializeComponent();
        Loaded += (_, _) => PositionToBottomRight();
    }

    private void PositionToBottomRight()
    {
        var work = SystemParameters.WorkArea;
        Left = work.Right - ActualWidth - 24;
        Top = work.Bottom - ActualHeight - 24;
    }

    public void UpdateFromStatus(StatusResponse status)
    {
        // Render minutes+seconds from the Service's authoritative MinutesRemaining. We don't
        // try to interpolate between polls — at 2s cadence the visible drift is tolerable.
        var totalSeconds = (int)Math.Floor(status.MinutesRemaining * 60);
        if (totalSeconds < 0) totalSeconds = 0;
        var mm = totalSeconds / 60;
        var ss = totalSeconds % 60;
        CountdownText.Text = $"{mm:00}:{ss:00}";
    }
}
