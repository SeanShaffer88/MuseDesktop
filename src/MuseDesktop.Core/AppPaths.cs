namespace MuseDesktop.Core;

/// <summary>
/// Where the app's files live. Everything is per-user and portable:
/// settings and logs under %APPDATA%\MuseDesktop, the WebView2 profile
/// (cookies / local storage — what keeps you signed in) under
/// %LOCALAPPDATA%\MuseDesktop\WebView2.
/// Tests can redirect the root via the MUSEDESKTOP_HOME environment variable.
/// </summary>
public static class AppPaths
{
    public static string RootDir
    {
        get
        {
            var overrideDir = Environment.GetEnvironmentVariable("MUSEDESKTOP_HOME");
            if (!string.IsNullOrWhiteSpace(overrideDir))
                return overrideDir;
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "MuseDesktop");
        }
    }

    public static string SettingsPath => Path.Combine(RootDir, "settings.json");

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
