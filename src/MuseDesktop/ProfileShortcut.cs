using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MuseDesktop.Core;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using ColorConverter = System.Windows.Media.ColorConverter;
using FlowDirection = System.Windows.FlowDirection;
using FontFamily = System.Windows.Media.FontFamily;
using Point = System.Windows.Point;

namespace MuseDesktop;

/// <summary>
/// Per-profile desktop shortcuts (S3 #23): a .lnk on the Desktop that
/// launches the app with --profile &lt;id&gt;. Created through the
/// documented WScript.Shell COM object (the same late-bound COM pattern
/// TaskbarProgress uses for its coclass) — no extra dependencies, and
/// every failure degrades to a plain error for the Settings window.
/// </summary>
public static class ProfileShortcut
{
    /// <summary>
    /// Write the shortcut for <paramref name="profile"/> to the Desktop.
    /// Returns null on success, or a plain-language error message.
    /// </summary>
    public static string? CreateOnDesktop(AccountProfile profile, DiagnosticLog log)
    {
        object? shell = null;
        object? link = null;
        try
        {
            var exe = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exe))
                return "Couldn't find the MuseDesktop program file.";

            var shellType = Type.GetTypeFromProgID("WScript.Shell")
                            ?? throw new InvalidOperationException("WScript.Shell is not available on this PC.");
            shell = Activator.CreateInstance(shellType)
                    ?? throw new InvalidOperationException("WScript.Shell could not be started.");

            var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            var path = Path.Combine(desktop, Profiles.ShortcutFileName(profile));

            dynamic wsh = shell;
            link = wsh.CreateShortcut(path);
            dynamic lnk = link!;
            lnk.TargetPath = exe;
            lnk.Arguments = $"--profile {profile.Id}";
            lnk.WorkingDirectory = Path.GetDirectoryName(exe) ?? "";
            // R3 #121: the shortcut wears the profile's avatar icon,
            // regenerated from its current color + glyph every time
            // the shortcut is created; a drawing failure falls back
            // to the app icon, never to no shortcut.
            var iconPath = ProfileShortcutIcon.Regenerate(profile, log);
            lnk.IconLocation = iconPath is not null ? $"{iconPath},0" : $"{exe},0";
            lnk.Description = $"MuseDesktop — {profile.Name}";
            lnk.Save();
            log.Info($"Desktop shortcut created for profile {profile.Id}");
            return null;
        }
        catch (Exception ex)
        {
            log.Warn($"Shortcut creation failed for profile {profile.Id}: {ex.Message}");
            return $"Couldn't create the shortcut ({ex.Message}).";
        }
        finally
        {
            if (link is not null) Marshal.FinalReleaseComObject(link);
            if (shell is not null) Marshal.FinalReleaseComObject(shell);
        }
    }
}

/// <summary>
/// The profile shortcut's icon (R3 #121): the avatar drawn fresh on
/// every shortcut creation — the profile's color (or the app red)
/// behind its glyph (or its name's first letter), Core's
/// <see cref="ProfileAvatar.IconSpec"/> made pixels — saved as a
/// PNG-in-ICO file under the app's data folder. Regenerating on
/// creation is the deal: change the avatar, re-create the shortcut,
/// and the icon follows. Drawing is best-effort; a failure logs and
/// the shortcut keeps the app icon.
/// </summary>
public static class ProfileShortcutIcon
{
    public static string IconDirectory => Path.Combine(AppPaths.RootDir, "shortcut-icons");

    /// <summary>Draw (or redraw) the icon for a profile; returns its path, or null when drawing failed.</summary>
    public static string? Regenerate(AccountProfile profile, DiagnosticLog log)
    {
        try
        {
            var (hex, mark) = ProfileAvatar.IconSpec(profile);
            var color = (Color)ColorConverter.ConvertFromString(hex);
            const int size = 256;
            var visual = new DrawingVisual();
            using (var dc = visual.RenderOpen())
            {
                dc.DrawRoundedRectangle(
                    new SolidColorBrush(color), null,
                    new Rect(8, 8, size - 16, size - 16), 48, 48);
                var text = new FormattedText(
                    mark,
                    CultureInfo.InvariantCulture,
                    FlowDirection.LeftToRight,
                    new Typeface(new FontFamily("Segoe UI Symbol"),
                        FontStyles.Normal, FontWeights.Bold, FontStretches.Normal),
                    150, Brushes.White, 1.0);
                dc.DrawText(text, new Point((size - text.Width) / 2, (size - text.Height) / 2));
            }
            var bitmap = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(visual);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var ms = new MemoryStream();
            encoder.Save(ms);
            var png = ms.ToArray();

            Directory.CreateDirectory(IconDirectory);
            var path = Path.Combine(IconDirectory, profile.Id + ".ico");
            File.WriteAllBytes(path, WrapPngAsIco(png));
            return path;
        }
        catch (Exception ex)
        {
            log.Warn($"Shortcut icon for profile {profile.Id} couldn't be drawn ({ex.Message}) — the app icon is used instead");
            return null;
        }
    }

    /// <summary>A single-image .ico container around a PNG (the Vista-and-later form Explorer reads).</summary>
    private static byte[] WrapPngAsIco(byte[] png)
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write((ushort)0);   // reserved
        w.Write((ushort)1);   // type: icon
        w.Write((ushort)1);   // one image
        w.Write((byte)0);     // width: 0 means 256
        w.Write((byte)0);     // height: 0 means 256
        w.Write((byte)0);     // palette colors: none
        w.Write((byte)0);     // reserved
        w.Write((ushort)1);   // color planes
        w.Write((ushort)32);  // bits per pixel
        w.Write(png.Length);
        w.Write(6 + 16);      // image data offset (header + one entry)
        w.Write(png);
        return ms.ToArray();
    }
}
