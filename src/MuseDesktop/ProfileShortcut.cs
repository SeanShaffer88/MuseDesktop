using System.IO;
using System.Runtime.InteropServices;
using MuseDesktop.Core;

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
            lnk.IconLocation = $"{exe},0";
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
