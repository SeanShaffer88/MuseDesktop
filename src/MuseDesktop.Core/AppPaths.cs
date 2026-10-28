namespace MuseDesktop.Core;

/// <summary>
/// Where the app's files live. Everything is per-user: settings and logs
/// under %APPDATA%\MuseDesktop, the WebView2 profile (cookies / local
/// storage — what keeps you signed in) under
/// %LOCALAPPDATA%\MuseDesktop\WebView2.
/// Portable mode (v0.9.0, S5 #49): a <c>portable.txt</c> marker beside
/// the exe moves the settings root (settings, download history, logs)
/// to a <c>data</c> folder beside the exe — see
/// <see cref="SettingsLocation"/>. The WebView2 profile does NOT move.
/// Tests can redirect the root via the MUSEDESKTOP_HOME environment
/// variable, which outranks the portable marker (it is a test hook).
/// </summary>
public static class AppPaths
{
    private static SettingsLocation? _location;

    /// <summary>The resolved settings location (portable or installed), computed once per process.</summary>
    public static SettingsLocation Location => _location ??= ResolveLocation();

    /// <summary>True when the portable marker beside the exe put settings in the exe's data folder (S5 #49).</summary>
    public static bool IsPortable => Location.IsPortable;

    private static SettingsLocation ResolveLocation()
    {
        var overrideDir = Environment.GetEnvironmentVariable("MUSEDESKTOP_HOME");
        if (!string.IsNullOrWhiteSpace(overrideDir))
            return SettingsLocation.Installed(overrideDir);
        var exeDir = AppContext.BaseDirectory;
        var installedRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "MuseDesktop");
        return SettingsLocation.Resolve(
            exeDir, installedRoot,
            File.Exists(Path.Combine(exeDir, SettingsLocation.MarkerFileName)));
    }

    public static string RootDir => Location.RootDir;

    public static string SettingsPath => Location.SettingsPath;

    /// <summary>Finished-download history for the Downloads window (v0.4.0).</summary>
    public static string DownloadsHistoryPath => Location.DownloadsHistoryPath;

    /// <summary>The download sort journal (R4 #131): applied sort/archive batches, kept for Undo.</summary>
    public static string DownloadSortJournalPath => Path.Combine(RootDir, "download-sort-journal.json");

    /// <summary>The command palette's action history (R1 #106): built-in action ids only, beside the settings, so portable mode carries it too.</summary>
    public static string PaletteHistoryPath => Path.Combine(RootDir, "palette-history.json");

    /// <summary>The user's one-off session snapshot (R2 #114): profile + app route + placement per window — never full page URLs.</summary>
    public static string SessionSnapshotPath => Path.Combine(RootDir, "session-snapshot.json");

    /// <summary>The session journal (R2 #117/#118): the running app's window list + clean-exit marker, the next launch's crash-restore evidence. Routes only.</summary>
    public static string SessionJournalPath => Path.Combine(RootDir, "session-journal.json");

    /// <summary>Privacy report counters + recent permission denials (v0.11.0, S7 #62/#67) — counts and host names only, beside the settings.</summary>
    public static string PrivacyStatsPath => Path.Combine(RootDir, "privacy-stats.json");

    /// <summary>Automatic rotating backups of settings + download history (v0.12.0, S8 #79).</summary>
    public static string BackupsDir => Path.Combine(RootDir, "backups");

    /// <summary>Profile launch tallies (R3 #129): local-only counts in their own file — never part of settings, so they never ride a settings export/import or a profile bundle.</summary>
    public static string ProfileUsagePath => Path.Combine(RootDir, "profile-usage.json");

    /// <summary>The reading lists (R5 #143): per profile, titles + app routes only — local-only, in their own file like the usage tallies.</summary>
    public static string ReadingListPath => Path.Combine(RootDir, "reading-list.json");

    /// <summary>The quick notes (R5 #148): per profile, local-only, in their own file — a user's words never ride a settings export.</summary>
    public static string NotesPath => Path.Combine(RootDir, "notes.json");

    /// <summary>The weekly stats (R5 #150): per-profile per-week counts (launches, downloads, focus minutes) — local-only, clearable from the dashboard, in their own file.</summary>
    public static string WeeklyStatsPath => Path.Combine(RootDir, "weekly-stats.json");

    /// <summary>The message bookmarks (R5B #202): per profile — nickname + conversation route + message snippet. Local-only by design (SEAN's): bookmarks are revisited from inside the app and are never exported, so they live in their own file, never in settings.</summary>
    public static string BookmarksPath => Path.Combine(RootDir, "bookmarks.json");

    /// <summary>The composer drafts (R5B #204): per profile, per conversation route — the user's unsent words, kept only to hand them back. Own file, never in settings, never logged.</summary>
    public static string DraftsPath => Path.Combine(RootDir, "drafts.json");

    /// <summary>The prompt snippets (R5B #206): per profile — the user's own reusable prompt text. Own file, never in settings, never logged.</summary>
    public static string SnippetsPath => Path.Combine(RootDir, "snippets.json");

    /// <summary>Installers the in-app updater downloads + the kept current/previous slots (v0.13.0, S9 #81/#84). Beside the settings, so portable mode carries them too.</summary>
    public static string UpdatesDir => Path.Combine(RootDir, "updates");

    /// <summary>The last launches' startup timings (v0.12.0, S8 #73) — measurements only, no content.</summary>
    public static string StartupTimingsPath => Path.Combine(RootDir, "startup-timings.json");

    /// <summary>The pending launch intent (v1.0.0, S11 #99/#100): a second launch writes the action it asked for here, then fires the wake event; the running instance takes it on waking. Transient by design — deleted on take.</summary>
    public static string PendingIntentPath => Path.Combine(RootDir, "pending-intent.json");

    /// <summary>Imported language packs (R7 #165): one validated pack-<c>.json per language, written by Settings import.</summary>
    public static string LanguagePacksDir => Path.Combine(RootDir, "language-packs");

    /// <summary>The announcement history (R7 #169): the bounded record Diagnostics reads.</summary>
    public static string AnnouncementHistoryPath => Path.Combine(RootDir, "announcements.json");

    public static string LogDir => Path.Combine(RootDir, "logs");
    public static string LogPath => Path.Combine(LogDir, "musedesktop.log");

    /// <summary>
    /// The per-user data root holding every profile's WebView2 folder:
    /// %LOCALAPPDATA%\MuseDesktop, with one subfolder per profile
    /// (WebView2, WebView2-&lt;id&gt;, … — see Profiles.FolderName).
    /// The installer's saved-password wipe and the startup sweep
    /// (R7F) both enumerate THIS folder's profile subfolders.
    /// </summary>
    public static string ProfilesDataRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "MuseDesktop");

    /// <summary>WebView2 profile (cookies / local storage — what keeps you signed in) for the default profile; additional profiles get sibling folders (see Profiles.FolderName).</summary>
    public static string WebViewDataDir => Path.Combine(ProfilesDataRoot, "WebView2");

    /// <summary>The WebView2 data folder for any profile id (v0.3.0).</summary>
    public static string ProfileDir(string profileId) => Path.Combine(
        ProfilesDataRoot, Profiles.FolderName(profileId));

    public static string DownloadsHint =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
}

/// <summary>
/// A tiny local-only diagnostic log: timestamped lines, no telemetry, and a
/// single rollover file so it can never grow without bound. Callers pass
/// already-safe text; URLs must go through <see cref="UrlPolicy.RedactForLog"/>.
/// </summary>
public sealed class DiagnosticLog
{
    private const long MaxBytes = 1_000_000;
    private readonly string _path;
    private readonly object _gate = new();

    public DiagnosticLog(string path) => _path = path;

    public string Path => _path;

    public void Info(string message) => Write("INFO", message);
    public void Warn(string message) => Write("WARN", message);
    public void Error(string message) => Write("ERROR", message);

    private void Write(string level, string message)
    {
        try
        {
            lock (_gate)
            {
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(_path)!);
                RollIfNeeded();
                var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} [{level}] {Sanitize(message)}\n";
                File.AppendAllText(_path, line);
            }
        }
        catch
        {
            // Logging must never take the app down.
        }
    }

    private void RollIfNeeded()
    {
        var info = new FileInfo(_path);
        if (info.Exists && info.Length > MaxBytes)
            File.Move(_path, _path + ".old", overwrite: true);
    }

    /// <summary>One event, one line, bounded length — log text cannot smuggle newlines.</summary>
    public static string Sanitize(string message)
    {
        var clean = message.Replace('\r', ' ').Replace('\n', ' ');
        return clean.Length <= 500 ? clean : clean[..500];
    }
}
