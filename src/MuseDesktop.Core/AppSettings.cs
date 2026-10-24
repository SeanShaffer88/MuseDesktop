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

    // Tray, notifications & presence (v0.8.0, S4).
    /// <summary>
    /// Start-in-tray for background (autostart-style) launches (S4 #40):
    /// separate from StartWithWindows itself. On (default, and the
    /// behavior autostart launches have always had): a --background
    /// launch lands in the tray. Off: it shows the window like a manual
    /// launch. Manual launches always show the window either way.
    /// </summary>
    public bool StartMinimizedToTray { get; set; } = true;

    /// <summary>How long hidden-in-tray before the web view suspends (S4 #34); replaces the fixed 5 minutes. Sanitized to a defined option.</summary>
    public SuspendTimeout TraySuspendTimeout { get; set; } = SuspendTimeout.FiveMinutes;

    /// <summary>Single-click the tray icon reopens the window instead of double-click (S4 #36). Off by default — double-click is the long-standing behavior.</summary>
    public bool TraySingleClickRestore { get; set; }

    /// <summary>Flash the taskbar button when Muse asks for attention while the window is hidden (S4 #39). Off by default.</summary>
    public bool FlashTaskbarOnAttention { get; set; }

    /// <summary>In-app quiet hours (S4 #38): while on and inside the window, MuseDesktop's own balloons/flash stay quiet (and say so in the log).</summary>
    public bool QuietHoursEnabled { get; set; }

    /// <summary>Quiet-hours start, minutes after local midnight (default 22:00). Sanitized to 0–1439.</summary>
    public int QuietHoursStartMinutes { get; set; } = 22 * 60;

    /// <summary>Quiet-hours end, minutes after local midnight (default 07:00; may be earlier than the start — the window wraps midnight).</summary>
    public int QuietHoursEndMinutes { get; set; } = 7 * 60;

    // Appearance & personalization (v0.9.0, S5).
    /// <summary>Shell theme (S5 #43): Dark (the family identity), Light, or FollowSystem. Shell chrome only — the Muse page is never restyled. Undefined values sanitize to Dark.</summary>
    public ThemeMode Theme { get; set; } = ThemeMode.Dark;

    /// <summary>Shell accent color key from <see cref="AccentPalette"/> (S5 #44); sanitized to a palette key, default Red. Profile colors are a separate feature.</summary>
    public string AccentColor { get; set; } = AccentPalette.DefaultKey;

    /// <summary>Shell UI text scale (S5 #46): 1.0, 1.25, or 1.5 (sanitized to the nearest offered factor). Independent of page zoom.</summary>
    public double ShellFontScale { get; set; } = 1.0;

    /// <summary>Toolbar button order (S5 #45): every known item id in display order. Sanitized by ToolbarLayout.</summary>
    public List<string> ToolbarOrder { get; set; } = ToolbarLayout.DefaultOrder();

    /// <summary>Toolbar items the user hid (S5 #45). Sanitized by ToolbarLayout (Back can never be hidden).</summary>
    public List<string> ToolbarHidden { get; set; } = new();

    // Settings-window memory (v0.9.0, S5 #48).
    /// <summary>The Settings section last in view (a SettingsSearch section id), so Settings reopens where it was left. Sanitized to a known id or null.</summary>
    public string? SettingsLastSection { get; set; }

    /// <summary>Settings window width in pixels (S5 #48). Sanitized to a sane range; the default matches the window's long-standing width.</summary>
    public double SettingsWindowWidth { get; set; } = 480;

    /// <summary>Settings window height in pixels (S5 #48); 0 = the window's default height. Sanitized to 0 or a sane range.</summary>
    public double SettingsWindowHeight { get; set; }

    /// <summary>
    /// The first-run setup wizard has been completed or skipped (S5 #50).
    /// False only on a genuinely fresh settings file — Reset-all-settings
    /// restores it to false with the other preferences (see SettingsReset).
    /// </summary>
    public bool FirstRunCompleted { get; set; }

    // Account profiles (v0.3.0).
    /// <summary>Sign-in profiles; each has its own WebView2 data folder. Always contains at least the default profile (see Profiles.EnsureValid).</summary>
    public List<AccountProfile> Profiles { get; set; } = new() { new AccountProfile() };

    /// <summary>The profile this launch uses. Switching profiles restarts the app with the other folder.</summary>
    public string ActiveProfileId { get; set; } = global::MuseDesktop.Core.Profiles.DefaultId;

    /// <summary>
    /// Removed profiles whose data folders could not be deleted at
    /// removal time (S3 #28) — the folder belonged to the running profile
    /// or the delete failed. Startup retries these deletions before any
    /// WebView2 environment exists. Sanitized by Profiles.SanitizePendingDeletions.
    /// </summary>
    public List<string> PendingProfileFolderDeletions { get; set; } = new();

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
        // After the profile list is final: pending deletions must never
        // name a live profile (S3 #28).
        global::MuseDesktop.Core.Profiles.SanitizePendingDeletions(this);
        if (string.IsNullOrWhiteSpace(DownloadsFolder))
            DownloadsFolder = null;
        else
            DownloadsFolder = DownloadsFolder.Trim();
        DownloadHistoryCap = DownloadHistoryStore.SanitizeCap(DownloadHistoryCap);
        if (!Enum.IsDefined(CollisionPolicy))
            CollisionPolicy = DownloadCollisionPolicy.AutoRename;
        // S4: the suspend timeout must be one of the offered options,
        // and quiet-hours bounds must be real minutes-of-day — a bad
        // pair resets BOTH to the defaults rather than muting oddly.
        if (!Enum.IsDefined(TraySuspendTimeout))
            TraySuspendTimeout = SuspendTimeout.FiveMinutes;
        if (!QuietHours.IsValidMinutes(QuietHoursStartMinutes) || !QuietHours.IsValidMinutes(QuietHoursEndMinutes))
        {
            QuietHoursStartMinutes = 22 * 60;
            QuietHoursEndMinutes = 7 * 60;
        }
        // S5: theme/accent/text-scale/toolbar repairs, in the same
        // spirit — a hand-edited file degrades to the default look,
        // never to an unusable one.
        if (!Enum.IsDefined(Theme))
            Theme = ThemeMode.Dark;
        AccentColor = AccentPalette.Sanitize(AccentColor);
        ShellFontScale = ShellTextScale.Sanitize(ShellFontScale);
        var (order, hidden) = ToolbarLayout.Sanitize(ToolbarOrder, ToolbarHidden);
        ToolbarOrder = order;
        ToolbarHidden = hidden;
        // S5 #48: Settings-window memory — a known section id or
        // nothing; sizes clamped to what a real monitor could show.
        if (!SettingsSearch.IsKnownSection(SettingsLastSection))
            SettingsLastSection = null;
        SettingsWindowWidth = SettingsWindowWidth is >= 420 and <= 2000 ? SettingsWindowWidth : 480;
        SettingsWindowHeight = SettingsWindowHeight == 0 || SettingsWindowHeight is >= 400 and <= 2000
            ? SettingsWindowHeight
            : 0;
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
    /// The page to open at launch, per the active profile's start-page
    /// choice (v0.4.0 StartAtHome; v0.9.0 S5 #47 the 3-way mode incl. a
    /// validated custom Muse page). Resolution lives in
    /// <see cref="StartPagePolicy"/> so the fallback is unit-tested.
    /// </summary>
    public string StartupUrl => ResolveStartupUrl().Url;

    /// <summary>The launch page plus whether a custom start page failed validation and fell back to Home (the shell logs that case, redacted).</summary>
    public (string Url, bool CustomFellBack) ResolveStartupUrl()
    {
        var active = Profiles.FirstOrDefault(p => p.Id == ActiveProfileId);
        return StartPagePolicy.Resolve(active, ActiveLastUrl);
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
