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

    // Update delivery (v0.13.0, S9 #81/#82/#83/#84/#88).
    /// <summary>Which releases the checker may offer (S9 #88): Stable (default) or Stable + pre-releases (opt-in, warned in Settings). Undefined values sanitize to Stable.</summary>
    public UpdateChannel UpdateChannel { get; set; } = UpdateChannel.Stable;

    /// <summary>How often the automatic check runs (S9 #83): every launch (the historical behavior), daily, weekly, or manual-only. The manual ⋯-menu check always works.</summary>
    public UpdateCheckFrequency UpdateCheckFrequency { get; set; } = UpdateCheckFrequency.EveryLaunch;

    /// <summary>When the last automatic or manual check actually ran (S9 #83) — the frequency gate's input. State, not a preference: reset does not touch it.</summary>
    public DateTimeOffset? LastUpdateCheckUtc { get; set; }

    /// <summary>Update reminders are snoozed until this moment (S9 #83), set from the update window (tomorrow / next week). Null = not snoozed.</summary>
    public DateTimeOffset? UpdateSnoozedUntilUtc { get; set; }

    /// <summary>The newest app version this settings file has accounted for (S9 #82): the What's New window shows once when a launch's version differs from it. Null = fresh install.</summary>
    public string? LastSeenVersion { get; set; }

    /// <summary>Show the What's New window on the first launch of a new version (S9 #82). On by default; the window's own "don't show again" turns it off.</summary>
    public bool ShowWhatsNew { get; set; } = true;

    /// <summary>The installer the updater currently keeps (S9 #84): version + file name in the updates folder's "current" slot — the NEXT update rotates it into the rollback slot. Null until the in-app updater has downloaded a version.</summary>
    public PreviousInstallerInfo? KeptInstaller { get; set; }

    /// <summary>The rollback offer (S9 #84): the previous version's installer, kept in the updates folder's "previous" slot. Exactly one is kept; only versions installed through the in-app updater ever appear here.</summary>
    public PreviousInstallerInfo? PreviousInstaller { get; set; }

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

    // Accessibility, keyboard & focus (v0.10.0, S6).
    /// <summary>
    /// Remapped shortcut gestures (S6 #53): action id → canonical
    /// gesture text ("Ctrl+Shift+D") for the remappable actions in
    /// <see cref="ShortcutCatalog"/>. Empty = all defaults. Sanitized by
    /// <see cref="ShortcutBindings.SanitizeOverrides"/>.
    /// </summary>
    public Dictionary<string, string> ShortcutOverrides { get; set; } = new();

    /// <summary>Reduce motion (S6 #55): follow the Windows animation preference, always reduce, or never. Undefined values sanitize to FollowSystem.</summary>
    public ReduceMotionSetting ReduceMotion { get; set; } = ReduceMotionSetting.FollowSystem;

    /// <summary>Extended zoom range (S6 #57): 25%–500% instead of 50%–200%, for low-vision users. Off by default.</summary>
    public bool ExtendedZoomRange { get; set; }

    // Privacy & security (v0.11.0, S7).
    /// <summary>
    /// Let Muse (and its sign-in pages) save passwords in WebView2 and
    /// offer them back (S7 #68). This is WebView2's own default (on);
    /// the app hardcoded it on before v0.11.0. Applied to each profile's
    /// web view at init and live when changed in Settings.
    /// </summary>
    public bool PasswordAutosaveEnabled { get; set; } = true;

    /// <summary>Let WebView2 remember and refill ordinary form fields (S7 #68). WebView2's default (on), like password autosave.</summary>
    public bool GeneralAutofillEnabled { get; set; } = true;

    // App lock (v0.11.0, S7 #64). The credential itself is NEVER stored —
    // only the AppLock record: a random salt + PBKDF2 hash + iteration
    // count. Off by default; IsLockActive also requires a valid record,
    // and Sanitized() repairs an enabled-but-recordless state to off,
    // so a damaged settings file can never lock the user out.
    /// <summary>App lock on/off (S7 #64). Only meaningful with a valid credential record — see <see cref="AppLock"/>.</summary>
    public bool AppLockEnabled { get; set; }

    /// <summary>App-lock credential salt, base64 (16 bytes). Null = no credential set.</summary>
    public string? AppLockSalt { get; set; }

    /// <summary>App-lock credential PBKDF2-HMAC-SHA256 hash, base64 (32 bytes). Null = no credential set.</summary>
    public string? AppLockHash { get; set; }

    /// <summary>App-lock PBKDF2 iteration count stored with the record (0 = no credential).</summary>
    public int AppLockIterations { get; set; }

    /// <summary>Lock when the window hides to the tray (S7 #64). Default ON per the item's "lock on hide/start"; only the at-startup lock is unconditional.</summary>
    public bool AppLockOnTrayHide { get; set; } = true;

    /// <summary>
    /// Set by <see cref="Sanitized"/> when it had to disable a broken
    /// app-lock record (enabled but missing/malformed), so the shell
    /// can log the repair. Not persisted (JsonIgnore) — it's a
    /// diagnostic about THIS load, not a setting.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool AppLockWasRepaired { get; set; }

    // Launching & links (v1.0.0, S11 #96/#99).
    /// <summary>
    /// The global show/hide hotkey is on (S11 #96): RegisterHotKey
    /// with <see cref="GlobalHotkeyGesture"/> (or the Ctrl+Alt+M
    /// default) toggles MuseDesktop from any app. On by default —
    /// it is the resident-companion feature; a registration failure
    /// (another app owns the combination) is reported in Settings and
    /// the log, never fatal.
    /// </summary>
    public bool GlobalHotkeyEnabled { get; set; } = true;

    /// <summary>
    /// The global hotkey's gesture in canonical text (S11 #96), or
    /// null for the default (Ctrl+Alt+M). Sanitized: unparseable or
    /// modifier-less values fall back to null — a global key without
    /// Ctrl/Alt would capture a plain key in every app. Collision
    /// with the in-app bindings is resolved (by blocking) in
    /// <see cref="GlobalHotkey.Resolve"/>, not here, because the
    /// in-app side can change independently.
    /// </summary>
    public string? GlobalHotkeyGesture { get; set; }

    /// <summary>
    /// Keep <c>musedesktop://</c> links registered to this app
    /// (S11 #99): the installer registers them; the app syncs the
    /// HKCU registration to this toggle at startup — registering when
    /// missing, removing only its OWN registration when off, and never
    /// touching a registration that points at a different program.
    /// </summary>
    public bool RegisterProtocolLinks { get; set; } = true;

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

    // Workspaces & sessions (v1.2.0, R2 #111–#120). Workspaces store
    // app ROUTES only (see AppRoute) — never full page URLs — and ride
    // the settings file, so plain and encrypted export/import carry
    // them with everything else (#116); the payload versions with
    // this file's SchemaVersion, and Sanitized repairs it like the
    // profile list.
    /// <summary>Saved workspaces (#111): named layouts of (profile, placement, app route) windows.</summary>
    public List<Workspace> Workspaces { get; set; } = new();

    /// <summary>The workspace applied at launch instead of the single default window (#113), or null for none. Repaired by Workspaces.EnsureValid when it names nothing.</summary>
    public string? StartupWorkspaceId { get; set; }

    /// <summary>The workspace most recently applied or saved (#115) — drives the tray/palette "current" mark and the accent override. Session-adjacent state: Settings edits a clone and OK does not adopt it back.</summary>
    public string? ActiveWorkspaceId { get; set; }

    /// <summary>
    /// A workspace to apply once at the next launch (#112): set when
    /// applying a workspace first requires a profile switch (which
    /// restarts the app); the new process applies it, then clears it.
    /// State, not a preference — like <see cref="ActiveWorkspaceId"/>,
    /// a Settings clone never adopts it.
    /// </summary>
    public string? PendingWorkspaceApplyId { get; set; }

    /// <summary>Auto-save session (#118): restore the windows left open at exit (app routes only). Off by default — the windows you left are yours to reopen until you opt in.</summary>
    public bool AutoSaveSession { get; set; }

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
        ZoomFactor = ZoomMath.Clamp(ZoomFactor, ExtendedZoomRange);
        global::MuseDesktop.Core.Profiles.EnsureValid(this, ExtendedZoomRange);
        // S6: shortcut overrides are repaired by the bindings model
        // (unknown ids, unparseable gestures, and conflicts all drop);
        // an undefined reduce-motion value means follow the system.
        ShortcutOverrides = ShortcutBindings.SanitizeOverrides(ShortcutOverrides);
        ShortcutOverrides ??= new Dictionary<string, string>();
        if (!Enum.IsDefined(ReduceMotion))
            ReduceMotion = ReduceMotionSetting.FollowSystem;
        // After the profile list is final: pending deletions must never
        // name a live profile (S3 #28).
        global::MuseDesktop.Core.Profiles.SanitizePendingDeletions(this);
        // R2 #111: workspace repairs run after the profile list is
        // final too — a workspace window naming a deleted profile
        // drops, and the startup/active pointers follow the list.
        global::MuseDesktop.Core.Workspaces.EnsureValid(this);
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
        // S9: update-delivery repairs — undefined channel/frequency
        // values fall back to the safe defaults (stable only, every
        // launch); malformed kept-installer records drop (a bad record
        // must never produce a rollback offer); a blank last-seen
        // version reads as "fresh install".
        if (!Enum.IsDefined(UpdateChannel))
            UpdateChannel = UpdateChannel.Stable;
        if (!Enum.IsDefined(UpdateCheckFrequency))
            UpdateCheckFrequency = UpdateCheckFrequency.EveryLaunch;
        if (KeptInstaller is { IsWellFormed: false })
            KeptInstaller = null;
        if (PreviousInstaller is { IsWellFormed: false })
            PreviousInstaller = null;
        if (string.IsNullOrWhiteSpace(LastSeenVersion))
            LastSeenVersion = null;
        else
            LastSeenVersion = LastSeenVersion.Trim();
        // S11 #96: the global-hotkey gesture must parse and carry
        // Ctrl or Alt — anything else reads as "the default" (null).
        if (GlobalHotkeyGesture is not null)
        {
            if (ShortcutGesture.TryParse(GlobalHotkeyGesture, out var parsed)
                && (parsed.Modifiers.HasFlag(GestureModifiers.Control)
                    || parsed.Modifiers.HasFlag(GestureModifiers.Alt)))
                GlobalHotkeyGesture = parsed.Format();
            else
                GlobalHotkeyGesture = null;
        }
        // S7 #64: an enabled app lock with a missing/malformed
        // credential record can never be satisfied — disable it
        // (fail-open) rather than lock the user out of their own app.
        // The shell logs the repair via AppLockWasRepaired.
        if (AppLockEnabled && !AppLock.HasValidRecord(this))
        {
            AppLockEnabled = false;
            AppLockSalt = null;
            AppLockHash = null;
            AppLockIterations = 0;
            AppLockWasRepaired = true;
        }
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
