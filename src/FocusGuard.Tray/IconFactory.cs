using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Runtime.Versioning;

namespace FocusGuard.Tray;

/// <summary>
/// Generates a small tray icon at runtime so we don't need to ship an .ico asset. Renders
/// a coloured circle with a stroke to make the tray icon legible against light/dark themes.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class IconFactory
{
    public static Icon Create(Color fill, Color stroke)
    {
        const int size = 32;
        using var bmp = new Bitmap(size, size);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            using var fillBrush = new SolidBrush(fill);
            g.FillEllipse(fillBrush, 2, 2, size - 4, size - 4);
            using var strokePen = new Pen(stroke, 2f);
            g.DrawEllipse(strokePen, 2, 2, size - 4, size - 4);
        }

        var hicon = bmp.GetHicon();
        try
        {
            using var fromH = Icon.FromHandle(hicon);
            // Round-trip via memory so the resulting Icon owns its native data.
            using var ms = new MemoryStream();
            fromH.Save(ms);
            ms.Position = 0;
            return new Icon(ms);
        }
        finally
        {
            DestroyIcon(hicon);
        }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    [System.Runtime.InteropServices.DefaultDllImportSearchPaths(System.Runtime.InteropServices.DllImportSearchPath.System32)]
    private static extern bool DestroyIcon(IntPtr handle);
}
