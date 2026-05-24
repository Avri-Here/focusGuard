using System.Drawing;
using System.IO;
using System.Runtime.Versioning;
using System.Windows;

namespace FocusGuard.Tray;

/// <summary>
/// Loads the app icon (assets/Speedtest.ico, embedded as a WPF Resource).
///
/// We expose icon BYTES rather than <see cref="Icon"/> instances because H.NotifyIcon's
/// <c>Icon</c> dependency-property setter disposes the previously-assigned Icon when a new
/// one is set. Caching <c>Icon</c> instances and re-assigning them across state transitions
/// produces an <c>ObjectDisposedException</c> on the next <c>Icon.get_Handle()</c>. Callers
/// must build a fresh <c>Icon</c> from these bytes for every assignment.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class IconFactory
{
    private const string ResourceUri = "pack://application:,,,/Resources/AppIcon.ico";

    public static byte[] LoadAppIconBytes()
    {
        var resource = Application.GetResourceStream(new Uri(ResourceUri, UriKind.Absolute))
            ?? throw new InvalidOperationException("App icon resource not found.");
        using var stream = resource.Stream;
        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        return ms.ToArray();
    }

    public static Icon FromBytes(byte[] bytes)
    {
        var ms = new MemoryStream(bytes, writable: false);
        return new Icon(ms);
    }
}
