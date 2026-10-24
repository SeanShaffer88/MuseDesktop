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

    /// <summary>Keep the window above other windows (v0.5.0, S1 #5). Off by default.</summary>
    public bool AlwaysOnTop { get; set; }

    /// <summary>
    /// Window placement per profile (v0.5.0, S1 #8). The legacy global
    /// Window* fields above remain the fallback/default-profile source and
    /// are migrated into this map for the default profile on first load;
    /// the shell reads/writes the active profile's placement from here.
    /// </summary>
    public Dictionary<string, WindowPlacement> WindowPlacementByProfile { get; set; } = new();

    /// <summary>The active profile's placement, or the legacy global placement when none is stored.</summary>
    public WindowPlacement ActivePlacement
    {
        get
        {
            if (WindowPlacementByProfile.TryGetValue(ActiveProfileId, out var p) && p is not null)
                return p;
            return new WindowPlacement
            {
                Left = WindowLeft,
                Top = WindowTop,
                Width = WindowWidth,
                Height = WindowHeight,
                Maximized = WindowMaximized,
            };
        }
    }

    /// <summary>Store the active profile's placement (and mirror it to the legacy global fields for downgrade compat).</summary>
    public void SetActivePlacement(WindowPlacement placement)
    {
        WindowPlacementByProfile[ActiveProfileId] = placement;
        WindowLeft = placement.Left;
        WindowTop = placement.Top;
        WindowWidth = placement.Width;
        WindowHeight = placement.Height;
        WindowMaximized = placement.Maximized;
    }

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

    /// <summary>Show a tray balloon when a download completes while the window is hidden (S2 #18). Off by default — no nagging, no sound.</summary>
    public bool NotifyWhenDownloadCompletes { get; set; }

    /// <summary>How many entries the download history keeps: 50, 200, or 1000 (S2 #16). Sanitized to one of those.</summary>
    public int DownloadHistoryCap { get; set; } = DownloadHistoryStore.MaxEntries;

    /// <summary>What to do when a download's target file already exists (S2 #21).</summary>
    public DownloadCollisionPolicy CollisionPolicy { get; set; } = DownloadCollisionPolicy.AutoRename;

    /// <summary>
    /// The folder this launch's downloads land in (S2 #20): the active
    /// profile's override wins over the shared <see cref="DownloadsFolder"/>;
    /// null means the system Downloads folder (WebView2's default).
    /// </summary>
    public string? EffectiveDownloadsFolder() =>
        DownloadFolderResolver.Resolve(
            Profiles.FirstOrDefault(p => p.Id == ActiveProfileId)?.DownloadsFolder,
            DownloadsFolder);

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

    /// <summary>Legacy shared last-page (pre-v0.3.1). Migrated into LastUrlByProfile for the default profile on load, then cleared.</summary>
    public string? LastUrl { get; set; }

    /// <summary>
    /// Last in-app page per profile (v0.3.1). Session restore is per
    /// profile on purpose: Muse chat URLs are account-scoped, so one
    /// profile must never reopen another profile's last page.
    /// </summary>
    public Dictionary<string, string> LastUrlByProfile { get; set; } = new();

    /// <summary>The active profile's last page, or null.</summary>
    public string? ActiveLastUrl =>
        LastUrlByProfile.TryGetValue(ActiveProfileId, out var url) ? url : null;

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
        DownloadHistoryCap = DownloadHistoryStore.SanitizeCap(DownloadHistoryCap);
        if (!Enum.IsDefined(CollisionPolicy))
            CollisionPolicy = DownloadCollisionPolicy.AutoRename;
        LastUrlByProfile ??= new Dictionary<string, string>();
        // Migrate the pre-v0.3.1 shared last page into the default profile.
        if (LastUrl is not null)
        {
            if (IsRestorableUrl(LastUrl) && !LastUrlByProfile.ContainsKey(global::MuseDesktop.Core.Profiles.DefaultId))
                LastUrlByProfile[global::MuseDesktop.Core.Profiles.DefaultId] = LastUrl;
            LastUrl = null;
        }
        // Drop per-profile entries that are not restorable Muse pages.
        foreach (var key in LastUrlByProfile.Keys.ToList())
        {
            if (!IsRestorableUrl(LastUrlByProfile[key]))
                LastUrlByProfile.Remove(key);
        }
        // Per-profile window placement (v0.5.0, S1 #8): migrate the legacy
        // global placement into the default profile's entry on first load,
        // drop entries for profiles that no longer exist, and clamp sizes.
        WindowPlacementByProfile ??= new Dictionary<string, WindowPlacement>();
        if (!WindowPlacementByProfile.ContainsKey(global::MuseDesktop.Core.Profiles.DefaultId))
        {
            WindowPlacementByProfile[global::MuseDesktop.Core.Profiles.DefaultId] = new WindowPlacement
            {
                Left = WindowLeft,
                Top = WindowTop,
                Width = WindowWidth,
                Height = WindowHeight,
                Maximized = WindowMaximized,
            };
        }
        foreach (var key in WindowPlacementByProfile.Keys.ToList())
        {
            var p = WindowPlacementByProfile[key];
            if (p is null || Profiles.All(pr => pr.Id != key))
            {
                WindowPlacementByProfile.Remove(key);
                continue;
            }
            p.Width = p.Width is >= 480 and <= 10000 ? p.Width : 1280;
            p.Height = p.Height is >= 320 and <= 10000 ? p.Height : 860;
        }
        return this;
    }

    private static bool IsRestorableUrl(string? url) =>
        url is not null &&
        UrlPolicy.ClassifyNavigation(url) == LinkDisposition.InApp &&
        Uri.TryCreate(url, UriKind.Absolute, out var u) &&
        UrlPolicy.IsInAppHost(u.Host);

    /// <summary>
    /// The page to open at launch: the Muse home page when the active
    /// profile asks for it (v0.4.0), else the active profile's restored
    /// page when valid, else home.
    /// </summary>
    public string StartupUrl
    {
        get
        {
            var active = Profiles.FirstOrDefault(p => p.Id == ActiveProfileId);
            if (active?.StartAtHome == true)
                return UrlPolicy.HomeUrl;
            return ActiveLastUrl ?? UrlPolicy.HomeUrl;
        }
    }

    /// <summary>Deep copy (JSON round-trip) — the Settings window edits a clone so Cancel can discard cleanly.</summary>
    public AppSettings Clone() =>
        JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(this)) ?? new AppSettings();
}

/// <summary>One profile's remembered window placement (v0.5.0, S1 #8).</summary>
public sealed class WindowPlacement
{
    public double? Left { get; set; }
    public double? Top { get; set; }
    public double Width { get; set; } = 1280;
    public double Height { get; set; } = 860;
    public bool Maximized { get; set; }
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

    // Settings export / import (v0.4.0): the same JSON the app itself
    // writes, so a backup made by any build loads in any other. Import
    // goes through Sanitized like every other load — a hand-edited or
    // damaged file can never produce an invalid settings object.

    /// <summary>Serialize settings for the Settings window's Export button.</summary>
    public static string Export(AppSettings settings) =>
        JsonSerializer.Serialize(settings, Options);

    /// <summary>Parse an imported settings file. On failure, <paramref name="error"/> says why in plain words.</summary>
    public static bool TryImport(string json, out AppSettings? settings, out string? error)
    {
        settings = null;
        error = null;
        try
        {
            var parsed = JsonSerializer.Deserialize<AppSettings>(json, Options);
            if (parsed is null)
            {
                error = "That file doesn't contain MuseDesktop settings.";
                return false;
            }
            settings = parsed.Sanitized();
            return true;
        }
        catch (Exception)
        {
            error = "That file couldn't be read as MuseDesktop settings (it may be damaged or from another app).";
            return false;
        }
    }
}
