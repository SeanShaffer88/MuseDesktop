namespace MuseDesktop.Core;

/// <summary>
/// The app's command-line surface (S3 #23; completed by S11 #100):
/// the flags the app itself launches with. Unknown arguments are
/// tolerated (collected, never fatal) so a stray argument from a
/// shortcut or a future flag can never stop the app from starting.
/// Two flags are process-level information, not actions:
/// <see cref="ShowHelp"/> and <see cref="ShowVersion"/> are answered
/// by the launching process itself, before single-instance — they
/// never wake or disturb a running instance. Everything actionable
/// becomes a <see cref="LaunchIntent"/> (see
/// <see cref="LaunchIntent.FromCommandLine"/>), the one model the
/// running instance applies, whether the launch was a shortcut, a
/// Jump List task, a <c>musedesktop://</c> link, or a script.
/// </summary>
public sealed class CommandLineOptions
{
    /// <summary>Launch quietly in the tray (the Start-with-Windows entry uses this).</summary>
    public bool Background { get; init; }

    /// <summary>CI mode: construct the real window, prove WebView2 initializes, exit.</summary>
    public bool SmokeTest { get; init; }

    /// <summary>Open the Settings window once the main window is up (S4 #33 — the taskbar Jump List's Settings task uses this).</summary>
    public bool OpenSettings { get; init; }

    /// <summary>Safe mode (S8 #72): start at the Muse home page (no last-page restore) and skip the update check. The app lock still applies. Holding Shift at launch is the other route in — the shell checks the keyboard state itself.</summary>
    public bool SafeMode { get; init; }

    /// <summary>The --profile value as given, or null when absent/valueless. Validated against the profile list by the caller (Profiles.ResolveLaunchProfile).</summary>
    public string? ProfileId { get; init; }

    /// <summary>Go to the Muse home page (S11 #100): on a cold start the launch opens at Home; on a running instance it navigates there.</summary>
    public bool OpenHome { get; init; }

    /// <summary>Open the Downloads window (S11 #100; the Jump List's Downloads-adjacent routes and scripts use this).</summary>
    public bool OpenDownloads { get; init; }

    /// <summary>Quick dictate (S11 #97): show the window and focus the Muse page, ready for Muse's own voice button. The tray item and the Jump List task ride this flag.</summary>
    public bool QuickDictate { get; init; }

    /// <summary>Show the command-line usage and exit (S11 #100). Answered in the launching process, before single-instance.</summary>
    public bool ShowHelp { get; init; }

    /// <summary>Show the app version and exit (S11 #100). Answered in the launching process, before single-instance.</summary>
    public bool ShowVersion { get; init; }

    /// <summary>A <c>musedesktop://</c> link handed to this launch (S11 #99), raw as received. Parsed and validated by <see cref="LaunchIntent.ParseProtocolUri"/> against the profile list — never opened as a web address.</summary>
    public string? ProtocolUri { get; init; }

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
        var safeMode = false;
        var openHome = false;
        var openDownloads = false;
        var quickDictate = false;
        var showHelp = false;
        var showVersion = false;
        string? profileId = null;
        string? protocolUri = null;
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
            else if (arg.Equals("--safe-mode", StringComparison.OrdinalIgnoreCase))
            {
                safeMode = true;
            }
            else if (arg.Equals("--home", StringComparison.OrdinalIgnoreCase))
            {
                openHome = true;
            }
            else if (arg.Equals("--downloads", StringComparison.OrdinalIgnoreCase))
            {
                openDownloads = true;
            }
            else if (arg.Equals("--dictate", StringComparison.OrdinalIgnoreCase))
            {
                quickDictate = true;
            }
            else if (arg.Equals("--help", StringComparison.OrdinalIgnoreCase))
            {
                showHelp = true;
            }
            else if (arg.Equals("--version", StringComparison.OrdinalIgnoreCase))
            {
                showVersion = true;
            }
            else if (arg.StartsWith(ProtocolLinks.Scheme + ":", StringComparison.OrdinalIgnoreCase))
            {
                // A musedesktop:// link (S11 #99). The first one wins;
                // a second link in the same launch is ignored, not an
                // error. Validation happens in ParseProtocolUri.
                protocolUri ??= arg.Trim();
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
            SafeMode = safeMode,
            ProfileId = profileId,
            OpenHome = openHome,
            OpenDownloads = openDownloads,
            QuickDictate = quickDictate,
            ShowHelp = showHelp,
            ShowVersion = showVersion,
            ProtocolUri = protocolUri,
            Unknown = unknown,
        };
    }
}

/// <summary>
/// The documented command-line surface (S11 #100), shown by
/// <c>--help</c> and mirrored in the README's "Command line &amp;
/// links" section. Kept in Core so a test pins that every supported
/// flag and link route is actually documented here.
/// </summary>
public static class CommandLineUsage
{
    public static string Text(string version) =>
        $"MuseDesktop {version} — command line\n" +
        "\n" +
        "  MuseDesktop.exe [options] [musedesktop:// link]\n" +
        "\n" +
        "Options:\n" +
        "  --profile <id>   Start in (or switch to) the named profile.\n" +
        "                   On a running instance the switch asks first, like the menu.\n" +
        "  --background     Start quietly in the tray (used by Start with Windows).\n" +
        "  --home           Open at the Muse home page.\n" +
        "  --settings       Open the Settings window.\n" +
        "  --downloads      Open the Downloads window.\n" +
        "  --dictate        Open MuseDesktop ready for voice input (Quick dictate).\n" +
        "  --safe-mode      Start at the home page and skip the update check.\n" +
        "                   The app lock still applies. Launch-time only: it has\n" +
        "                   no effect on an instance that is already running.\n" +
        "  --help           Show this text and exit.\n" +
        "  --version        Show the MuseDesktop version and exit.\n" +
        "\n" +
        "Links (registered for the current Windows user):\n" +
        "  musedesktop://open            Show the window\n" +
        "  musedesktop://home            Go to the Muse home page\n" +
        "  musedesktop://settings        Open Settings\n" +
        "  musedesktop://downloads       Open Downloads\n" +
        "  musedesktop://help            Open Help & troubleshooting\n" +
        "  musedesktop://profile/<id>    Start in (or switch to) a profile\n" +
        "\n" +
        "Links can only name these actions — a musedesktop:// link can never\n" +
        "carry a web address or open an arbitrary page. When MuseDesktop is\n" +
        "already running, options and links are handed to the running instance;\n" +
        "while it is locked, they wait and apply after you unlock.";
}
