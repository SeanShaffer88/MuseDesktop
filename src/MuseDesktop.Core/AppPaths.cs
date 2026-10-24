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

    /// <summary>Privacy report counters + recent permission denials (v0.11.0, S7 #62/#67) — counts and host names only, beside the settings.</summary>
    public static string PrivacyStatsPath => Path.Combine(RootDir, "privacy-stats.json");

    /// <summary>Automatic rotating backups of settings + download history (v0.12.0, S8 #79).</summary>
    public static string BackupsDir => Path.Combine(RootDir, "backups");

    /// <summary>The last launches' startup timings (v0.12.0, S8 #73) — measurements only, no content.</summary>
    public static string StartupTimingsPath => Path.Combine(RootDir, "startup-timings.json");

    public static string LogDir => Path.Combine(RootDir, "logs");
    public static string LogPath => Path.Combine(LogDir, "musedesktop.log");

    /// <summary>WebView2 profile (cookies / local storage — what keeps you signed in) for the default profile; additional profiles get sibling folders (see Profiles.FolderName).</summary>
    public static string WebViewDataDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "MuseDesktop", "WebView2");

    /// <summary>The WebView2 data folder for any profile id (v0.3.0).</summary>
    public static string ProfileDir(string profileId) => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "MuseDesktop", Profiles.FolderName(profileId));

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
