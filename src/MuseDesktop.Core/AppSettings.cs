using System.Text.Json;

namespace MuseDesktop.Core;

/// <summary>
/// Everything the app persists about itself. Versioned JSON; unknown fields
/// are ignored and missing fields fall back to defaults, so a settings file
/// written by an older or newer build still loads.
/// </summary>
public sealed class AppSettings
{
    public int SchemaVersion { get; set; } = 1;

    // Window placement (physical pixels, restored via WPF RestoreBounds semantics).
    public double? WindowLeft { get; set; }
    public double? WindowTop { get; set; }
    public double WindowWidth { get; set; } = 1280;
    public double WindowHeight { get; set; } = 860;
    public bool WindowMaximized { get; set; }

    public double ZoomFactor { get; set; } = 1.0;

    /// <summary>When true, closing the window hides to the tray instead of exiting. Default since v0.1.3 (SEAN's direction after field verification); existing installs keep their saved value.</summary>
    public bool CloseToTray { get; set; } = true;

    /// <summary>Start with Windows (HKCU Run). Off by default; user opt-in only.</summary>
    public bool StartWithWindows { get; set; }

    // Downloads (v0.2.0).
    /// <summary>Folder downloads land in. Null/empty = the Windows Downloads folder.</summary>
    public string? DownloadsFolder { get; set; }

    /// <summary>Ask where to save each download (Save-As dialog per file).</summary>
    public bool AskWhereToSave { get; set; }

    // Updates (v0.2.0).
    /// <summary>Check GitHub Releases for a newer version once per launch. On by default; the check is silent on failure (offline-first).</summary>
    public bool CheckForUpdates { get; set; } = true;

    // Resources (v0.2.0).
    /// <summary>Suspend the WebView2 renderer after the window has been hidden in the tray for a while, to free memory/CPU. On by default.</summary>
    public bool SuspendWhenInTray { get; set; } = true;

    // Account profiles (v0.3.0).
    /// <summary>Sign-in profiles; each has its own WebView2 data folder. Always contains at least the default profile (see Profiles.EnsureValid).</summary>
    public List<AccountProfile> Profiles { get; set; } = new() { new AccountProfile() };

    /// <summary>The profile this launch uses. Switching profiles restarts the app with the other folder.</summary>
    public string ActiveProfileId { get; set; } = global::MuseDesktop.Core.Profiles.DefaultId;

    /// <summary>Last in-app page, restored on launch when it is still a Muse page.</summary>
    public string? LastUrl { get; set; }

    /// <summary>Clamp/repair values that cannot be trusted (hand-edited or corrupt files).</summary>
    public AppSettings Sanitized()
    {
        WindowWidth = WindowWidth is >= 480 and <= 10000 ? WindowWidth : 1280;
        WindowHeight = WindowHeight is >= 320 and <= 10000 ? WindowHeight : 860;
        ZoomFactor = ZoomMath.Clamp(ZoomFactor);
        global::MuseDesktop.Core.Profiles.EnsureValid(this);
        if (string.IsNullOrWhiteSpace(DownloadsFolder))
            DownloadsFolder = null;
        else
            DownloadsFolder = DownloadsFolder.Trim();
        if (LastUrl is not null &&
            (UrlPolicy.ClassifyNavigation(LastUrl) != LinkDisposition.InApp ||
             !UrlPolicy.IsInAppHost(new Uri(LastUrl).Host)))
        {
            LastUrl = null;
        }
        return this;
    }

    /// <summary>The page to open at launch: the restored page when valid, else home.</summary>
    public string StartupUrl => LastUrl ?? UrlPolicy.HomeUrl;

    /// <summary>Deep copy (JSON round-trip) — the Settings window edits a clone so Cancel can discard cleanly.</summary>
    public AppSettings Clone() =>
        JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(this)) ?? new AppSettings();
}

/// <summary>
/// Loads and saves <see cref="AppSettings"/> as JSON.
/// Saves are atomic (write to a temp file, then replace) so a crash or
/// power loss mid-save cannot leave a torn file. A file that fails to
/// parse is preserved next to the original as <c>.corrupt-*</c> and
/// defaults are used — the user's data is never silently discarded.
/// </summary>
public static class SettingsStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public static AppSettings Load(string path)
    {
        try
        {
            if (!File.Exists(path))
                return new AppSettings();
            var json = File.ReadAllText(path);
            var settings = JsonSerializer.Deserialize<AppSettings>(json, Options);
            return (settings ?? new AppSettings()).Sanitized();
        }
        catch (Exception)
        {
            try
            {
                var backup = $"{path}.corrupt-{DateTime.Now:yyyyMMdd-HHmmss}";
                File.Copy(path, backup, overwrite: false);
            }
            catch
            {
                // Best effort only — returning defaults is the contract.
            }
            return new AppSettings();
        }
    }

    public static void Save(string path, AppSettings settings)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(settings, Options));
        File.Move(tmp, path, overwrite: true);
    }
}
