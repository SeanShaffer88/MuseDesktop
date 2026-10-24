namespace MuseDesktop.Core;

/// <summary>
/// The app's command-line surface (S3 #23, ahead of the #100 CLI item):
/// the flags the app itself launches with. Unknown arguments are
/// tolerated (collected, never fatal) so a stray argument from a
/// shortcut or a future flag can never stop the app from starting.
/// </summary>
public sealed class CommandLineOptions
{
    /// <summary>Launch quietly in the tray (the Start-with-Windows entry uses this).</summary>
    public bool Background { get; init; }

    /// <summary>CI mode: construct the real window, prove WebView2 initializes, exit.</summary>
    public bool SmokeTest { get; init; }

    /// <summary>Open the Settings window once the main window is up (S4 #33 — the taskbar Jump List's Settings task uses this).</summary>
    public bool OpenSettings { get; init; }

    /// <summary>The --profile value as given, or null when absent/valueless. Validated against the profile list by the caller (Profiles.ResolveLaunchProfile).</summary>
    public string? ProfileId { get; init; }

    /// <summary>Everything that wasn't a recognized flag — ignored by the app, kept for logging/tests.</summary>
    public IReadOnlyList<string> Unknown { get; init; } = Array.Empty<string>();
}

public static class CommandLine
{
    public static CommandLineOptions Parse(IEnumerable<string> args)
    {
        var background = false;
        var smokeTest = false;
        var openSettings = false;
        string? profileId = null;
        var unknown = new List<string>();

        var list = args as IList<string> ?? args.ToList();
        for (var i = 0; i < list.Count; i++)
        {
            var arg = list[i];
            if (arg.Equals("--background", StringComparison.OrdinalIgnoreCase))
            {
                background = true;
            }
            else if (arg.Equals("--smoke-test", StringComparison.OrdinalIgnoreCase))
            {
                smokeTest = true;
            }
            else if (arg.Equals("--settings", StringComparison.OrdinalIgnoreCase))
            {
                openSettings = true;
            }
            else if (arg.Equals("--profile", StringComparison.OrdinalIgnoreCase))
            {
                // The value is the next argument — unless it's another
                // flag or there is none, in which case the launch just
                // uses the saved active profile.
                if (i + 1 < list.Count && !list[i + 1].StartsWith("--", StringComparison.Ordinal))
                {
                    profileId = list[++i].Trim();
                }
            }
            else if (arg.StartsWith("--profile=", StringComparison.OrdinalIgnoreCase))
            {
                var value = arg["--profile=".Length..].Trim();
                profileId = value.Length > 0 ? value : null;
            }
            else
            {
                unknown.Add(arg);
            }
        }

        return new CommandLineOptions
        {
            Background = background,
            SmokeTest = smokeTest,
            OpenSettings = openSettings,
            ProfileId = profileId,
            Unknown = unknown,
        };
    }
}
