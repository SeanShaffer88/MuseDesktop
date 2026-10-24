using System.Text.Json;

namespace MuseDesktop.Core;

// ───────────────────── recovery coordinator (S8 #71/#76) ─────────────────────

/// <summary>
/// WebView2 process-failure kinds, mirrored by name (S8 #71). Core stays
/// free of the WebView2 SDK, so the shell maps
/// <c>CoreWebView2ProcessFailedKind</c> onto this enum by name.
/// </summary>
public enum ProcessFailureKind
{
    RenderProcessExited,
    BrowserProcessExited,
    FrameRenderProcessExited,
    RenderProcessUnresponsive,
    GpuProcessExited,
    UtilityProcessExited,
    PpapiPluginProcessExited,
    PpapiBrokerProcessExited,
    SandboxHelperProcessExited,
    UnknownProcessExited,
    Other,
}

/// <summary>What the shell should do next, as decided by <see cref="RecoveryCoordinator"/>.</summary>
public enum RecoveryDecision
{
    /// <summary>Nothing automatic: log only (WebView2 recovers these itself, or the watchdog owns the case).</summary>
    None,

    /// <summary>Reload / re-navigate the page in the existing web view.</summary>
    ReloadPage,

    /// <summary>Tear the web view down and build a fresh one, restoring the last page.</summary>
    RecreateWebView,

    /// <summary>Automatic recovery is spent for this incident — show the failure overlay and let the user decide.</summary>
    ShowFailureOverlay,
}

/// <summary>
/// The ONE recovery coordinator (S8 #71 + #76). Before this slice three
/// mechanisms could each decide to reload the page — the ProcessFailed
/// handler, the S1 network-change auto-retry, and (new) the power-resume
/// reload — with separate flags that could fight. Now every signal goes
/// through this state machine:
///
/// • A fatal process failure (renderer / frame renderer / browser)
///   earns exactly ONE automatic recovery per incident: reload the page
///   in place for renderer exits, recreate the web view for a browser
///   exit. A second fatal failure with no successful load in between
///   gives up to the overlay.
/// • A failed load earns exactly ONE automatic retry, spent when
///   connectivity is reported back (network change or resume from
///   sleep) — the S1 #10 behavior, now with the same reset rules.
/// • Any successful load closes the incident and restores both budgets.
///
/// Non-fatal kinds (GPU, utility, plug-in, sandbox helper…) are
/// <see cref="RecoveryDecision.None"/>: WebView2 restarts those
/// processes itself, and the page survives. RenderProcessUnresponsive
/// is also None here — the hung-page watchdog (#80) owns that UX, with
/// a user choice instead of an automatic reload.
/// All time is caller-supplied so the machine is fully testable.
/// </summary>
public sealed class RecoveryCoordinator
{
    private bool _autoRecoveryUsed;
    private bool _autoRetryUsed;

    /// <summary>True while the current page is in a failed state (load failure or fatal process failure not yet recovered).</summary>
    public bool PageInFailedState { get; private set; }

    /// <summary>True when the one automatic process-failure recovery is still available for this incident.</summary>
    public bool AutoRecoveryAvailable => !_autoRecoveryUsed;

    public RecoveryDecision OnProcessFailed(ProcessFailureKind kind, DateTimeOffset now)
    {
        switch (kind)
        {
            case ProcessFailureKind.RenderProcessExited:
            case ProcessFailureKind.FrameRenderProcessExited:
            case ProcessFailureKind.BrowserProcessExited:
                break;
            default:
                return RecoveryDecision.None;
        }
        PageInFailedState = true;
        if (_autoRecoveryUsed)
            return RecoveryDecision.ShowFailureOverlay;
        _autoRecoveryUsed = true;
        return kind == ProcessFailureKind.BrowserProcessExited
            ? RecoveryDecision.RecreateWebView
            : RecoveryDecision.ReloadPage;
    }

    /// <summary>A load failed (navigation completed with an error). The overlay is the shell's business; the coordinator records the failed state for the connectivity retry.</summary>
    public void OnLoadFailed() => PageInFailedState = true;

    /// <summary>
    /// Connectivity is back (network availability/address change, or the
    /// PC resumed). When the page sits in a failed state and the one
    /// automatic retry hasn't been spent, spend it on a reload.
    /// </summary>
    public RecoveryDecision OnConnectivityRestored(DateTimeOffset now)
    {
        if (!PageInFailedState || _autoRetryUsed)
            return RecoveryDecision.None;
        _autoRetryUsed = true;
        return RecoveryDecision.ReloadPage;
    }

    /// <summary>A page loaded successfully: the incident is over and both budgets refill.</summary>
    public void OnPageLoaded()
    {
        PageInFailedState = false;
        _autoRecoveryUsed = false;
        _autoRetryUsed = false;
    }
}

// ───────────────────── hung-renderer watchdog (S8 #80) ─────────────────────

/// <summary>What the shell should do with the hung-page prompt, per <see cref="WatchdogPolicy"/>.</summary>
public enum WatchdogDecision
{
    None,
    ShowHungPrompt,
    HideHungPrompt,
}

/// <summary>
/// The hung-renderer watchdog's decision model (S8 #80). The shell
/// pings the page on a timer (a trivial script eval with a timeout);
/// this policy counts consecutive misses. Two in a row means "hung"
/// and the shell offers Reload / Wait — it NEVER auto-reloads, because
/// a missed ping can also mean a busy page (long voice answer, heavy
/// generation), and yanking the page away mid-thought is worse than
/// asking. Wait snoozes the watchdog for two minutes and resets the
/// count; a successful ping at any point resets the count and hides a
/// showing prompt.
/// </summary>
public sealed class WatchdogPolicy
{
    /// <summary>Consecutive missed pings before the page counts as hung.</summary>
    public const int MissThreshold = 2;

    /// <summary>How long Wait silences the watchdog.</summary>
    public static readonly TimeSpan SnoozeDuration = TimeSpan.FromMinutes(2);

    private int _misses;
    private DateTimeOffset? _snoozedUntilUtc;

    public int ConsecutiveMisses => _misses;

    /// <summary>True while the Reload/Wait prompt should be on screen.</summary>
    public bool PromptVisible { get; private set; }

    public bool IsSnoozed(DateTimeOffset now) =>
        _snoozedUntilUtc is { } until && now < until;

    public WatchdogDecision OnPingSucceeded(DateTimeOffset now)
    {
        _misses = 0;
        if (!PromptVisible)
            return WatchdogDecision.None;
        PromptVisible = false;
        return WatchdogDecision.HideHungPrompt;
    }

    public WatchdogDecision OnPingMissed(DateTimeOffset now)
    {
        if (IsSnoozed(now))
            return WatchdogDecision.None;
        _misses++;
        if (_misses >= MissThreshold && !PromptVisible)
        {
            PromptVisible = true;
            return WatchdogDecision.ShowHungPrompt;
        }
        return WatchdogDecision.None;
    }

    /// <summary>The user chose Wait: hide the prompt and stay quiet for <see cref="SnoozeDuration"/>.</summary>
    public void Snooze(DateTimeOffset now)
    {
        _snoozedUntilUtc = now + SnoozeDuration;
        _misses = 0;
        PromptVisible = false;
    }

    /// <summary>The user chose Reload: full reset — a fresh page earns a fresh watchdog.</summary>
    public void OnReloadChosen()
    {
        _misses = 0;
        PromptVisible = false;
        _snoozedUntilUtc = null;
    }
}

// ───────────────────── safe mode (S8 #72) ─────────────────────

/// <summary>
/// Safe mode's decisions (S8 #72), kept in Core so they're testable:
/// a safe-mode launch starts at the Muse home page (no last-page
/// restore — the restored page is a prime suspect when the app "won't
/// start"), skips the automatic update check, and that's all. The app
/// lock still applies (security is not a preference), downloads and
/// the tray behave normally.
/// </summary>
public static class SafeModePolicy
{
    /// <summary>The update check runs only when the user enabled it AND this isn't a safe-mode launch.</summary>
    public static bool ShouldCheckForUpdates(bool safeMode, bool settingEnabled) =>
        !safeMode && settingEnabled;

    /// <summary>The launch page: safe mode always lands on Home, ignoring the resolved start page.</summary>
    public static string StartupUrl(bool safeMode, string resolvedUrl) =>
        safeMode ? UrlPolicy.HomeUrl : resolvedUrl;
}

// ───────────────────── startup timings (S8 #73) ─────────────────────

/// <summary>One launch's startup measurements (S8 #73): milliseconds from process start to each milestone.</summary>
public sealed class StartupTimings
{
    public DateTimeOffset StartedUtc { get; set; }
    public long? WindowShownMs { get; set; }
    public long? WebViewReadyMs { get; set; }
    public long? FirstNavigationMs { get; set; }

    private static string Fmt(long? ms) => ms is { } v ? $"{v} ms" : "—";

    /// <summary>One line for the log and the Diagnostics window: "window 118 ms · WebView2 ready 842 ms · first page 2,041 ms".</summary>
    public string Describe() =>
        $"window {Fmt(WindowShownMs)} · WebView2 ready {Fmt(WebViewReadyMs)} · first page {Fmt(FirstNavigationMs)}";
}

/// <summary>
/// Stopwatch plumbing for <see cref="StartupTimings"/> (S8 #73). The
/// elapsed-ms source is injectable so tests drive it with a fake clock;
/// the production constructor runs a real <see cref="System.Diagnostics.Stopwatch"/>.
/// Each milestone records once — the first mark wins.
/// </summary>
public sealed class StartupClock
{
    private readonly Func<long> _elapsedMs;
    private readonly StartupTimings _timings;

    public StartupClock()
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        _elapsedMs = () => watch.ElapsedMilliseconds;
        _timings = new StartupTimings { StartedUtc = DateTimeOffset.UtcNow };
    }

    public StartupClock(Func<long> elapsedMs, DateTimeOffset startedUtc)
    {
        _elapsedMs = elapsedMs;
        _timings = new StartupTimings { StartedUtc = startedUtc };
    }

    public void MarkWindowShown() => _timings.WindowShownMs ??= _elapsedMs();
    public void MarkWebViewReady() => _timings.WebViewReadyMs ??= _elapsedMs();
    public void MarkFirstNavigation() => _timings.FirstNavigationMs ??= _elapsedMs();

    public StartupTimings Snapshot() => new()
    {
        StartedUtc = _timings.StartedUtc,
        WindowShownMs = _timings.WindowShownMs,
        WebViewReadyMs = _timings.WebViewReadyMs,
        FirstNavigationMs = _timings.FirstNavigationMs,
    };
}

/// <summary>The last few launches' timings, persisted as a small JSON list (S8 #73) — newest first, capped.</summary>
public static class StartupTimingsStore
{
    public const int MaxKept = 10;

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public static List<StartupTimings> Load(string path)
    {
        try
        {
            if (!File.Exists(path))
                return new List<StartupTimings>();
            var list = JsonSerializer.Deserialize<List<StartupTimings>>(File.ReadAllText(path), Options);
            return (list ?? new List<StartupTimings>()).Take(MaxKept).ToList();
        }
        catch (Exception)
        {
            return new List<StartupTimings>();
        }
    }

    /// <summary>Prepend <paramref name="entry"/> (the newest launch) and trim to the cap. Returns the list for saving.</summary>
    public static List<StartupTimings> Add(List<StartupTimings> runs, StartupTimings entry)
    {
        runs.Insert(0, entry);
        while (runs.Count > MaxKept)
            runs.RemoveAt(runs.Count - 1);
        return runs;
    }

    public static void Save(string path, List<StartupTimings> runs)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(runs.Take(MaxKept).ToList(), Options));
        File.Move(tmp, path, overwrite: true);
    }
}

// ───────────────────── memory readout (S8 #74) ─────────────────────

/// <summary>One WebView2 process's working set, as enumerated by the shell (S8 #74).</summary>
public sealed record ProcessMemoryEntry(string Kind, int ProcessId, long WorkingSetBytes);

/// <summary>An aggregated memory reading (S8 #74): the app's own working set plus the sum over its WebView2 processes.</summary>
public sealed record MemorySnapshot(
    DateTimeOffset AtUtc, long AppWorkingSetBytes, long WebViewTotalBytes, int WebViewProcessCount)
{
    public long TotalBytes => AppWorkingSetBytes + WebViewTotalBytes;

    public string Describe() =>
        $"MuseDesktop {DownloadText.FormatBytes(AppWorkingSetBytes)} + WebView2 " +
        $"{DownloadText.FormatBytes(WebViewTotalBytes)} across {WebViewProcessCount} process(es) " +
        $"= {DownloadText.FormatBytes(TotalBytes)} total";
}

public static class MemoryReadout
{
    /// <summary>Aggregate raw process entries into a snapshot. Negative/garbage entries are dropped, never summed.</summary>
    public static MemorySnapshot Snapshot(long appWorkingSetBytes, IEnumerable<ProcessMemoryEntry> webViewProcesses, DateTimeOffset now)
    {
        var entries = webViewProcesses.Where(e => e.WorkingSetBytes > 0).ToList();
        return new MemorySnapshot(
            now,
            Math.Max(0, appWorkingSetBytes),
            entries.Sum(e => e.WorkingSetBytes),
            entries.Count);
    }

    /// <summary>
    /// The before/after-suspend comparison line (S8 #74): what the
    /// WebView2 processes held just before tray suspend and just after
    /// resume. Null unless both halves exist.
    /// </summary>
    public static string? SuspendComparison(MemorySnapshot? beforeSuspend, MemorySnapshot? afterResume)
    {
        if (beforeSuspend is null || afterResume is null)
            return null;
        var delta = afterResume.WebViewTotalBytes - beforeSuspend.WebViewTotalBytes;
        var direction = delta <= 0
            ? $"({DownloadText.FormatBytes(-delta)} less after resume)"
            : $"({DownloadText.FormatBytes(delta)} more after resume)";
        return $"WebView2 memory before tray suspend: {DownloadText.FormatBytes(beforeSuspend.WebViewTotalBytes)} · " +
               $"after resume: {DownloadText.FormatBytes(afterResume.WebViewTotalBytes)} {direction}";
    }
}

// ───────────────────── per-profile data cap + cache cleanup (S8 #75) ─────────────────────

/// <summary>The offered per-profile data caps (S8 #75). 0 = off (the default); anything not in the allowed set sanitizes to off.</summary>
public static class ProfileDataCaps
{
    public const long Off = 0;
    public const long Mb500 = 500L * 1024 * 1024;
    public const long Gb1 = 1024L * 1024 * 1024;
    public const long Gb2 = 2L * 1024 * 1024 * 1024;

    public static readonly IReadOnlyList<long> Allowed = new[] { Off, Mb500, Gb1, Gb2 };

    public static long Sanitize(long bytes) => Allowed.Contains(bytes) ? bytes : Off;

    public static string Label(long bytes) => bytes switch
    {
        Mb500 => "500 MB",
        Gb1 => "1 GB",
        Gb2 => "2 GB",
        _ => "Off",
    };
}

/// <summary>One disposable cache folder found in a profile (S8 #75), sized and aged for the cleanup plan.</summary>
public sealed record CacheFolderEntry(string RelativePath, long Bytes, DateTimeOffset LastWriteUtc);

/// <summary>
/// A cache-cleanup plan (S8 #75): the disposable folders found, and the
/// oldest-first subset whose removal would bring the profile back
/// under its target. Planning is read-only and pure over the folder
/// walk; the shell executes the deletions and reports what happened.
/// </summary>
public sealed class CacheCleanupPlan
{
    public required string ProfileDir { get; init; }
    public required long TotalBytes { get; init; }
    public required long TargetBytes { get; init; }
    public required IReadOnlyList<CacheFolderEntry> Candidates { get; init; }
    public required IReadOnlyList<CacheFolderEntry> ToDelete { get; init; }

    public long OverByBytes => Math.Max(0, TotalBytes - TargetBytes);
    public long BytesToFree => ToDelete.Sum(e => e.Bytes);
    public bool IsOverTarget => ToDelete.Count > 0;
}

/// <summary>
/// Which folders in a WebView2 profile are disposable caches (S8 #75).
///
/// The safety rule is an ALLOWLIST, and it's the whole design: cleanup
/// may only ever delete folders whose name is in
/// <see cref="DisposableFolderNames"/> — the well-known Chromium cache
/// stores — and the walk never even descends into a folder named in
/// <see cref="ProtectedFolderNames"/> (where sign-in and site data
/// live: cookies sit under <c>Network</c>, storage under <c>Local
/// Storage</c> / <c>Session Storage</c> / <c>IndexedDB</c> /
/// <c>WebStorage</c>, service workers hold offline site state). An
/// unknown folder is left exactly where it was, whatever its size:
/// if a future WebView2 changes its layout, cleanup quietly does less,
/// never more.
/// </summary>
public static class CacheCleanup
{
    public static readonly IReadOnlyList<string> DisposableFolderNames = new[]
    {
        "Cache",
        "Code Cache",
        "GPUCache",
        "GrShaderCache",
        "ShaderCache",
        "DawnCache",
        "GraphiteDawnCache",
    };

    public static readonly IReadOnlyList<string> ProtectedFolderNames = new[]
    {
        "Network",          // cookies live here in modern Chromium profiles
        "Local Storage",
        "Session Storage",
        "IndexedDB",
        "WebStorage",
        "Service Worker",   // offline site state — not a disposable cache
        "Sessions",
        "shared_proto_db",
        "Sync Data",
    };

    private static readonly HashSet<string> DisposableSet =
        new(DisposableFolderNames, StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> ProtectedSet =
        new(ProtectedFolderNames, StringComparer.OrdinalIgnoreCase);

    public static bool IsDisposableName(string folderName) => DisposableSet.Contains(folderName);
    public static bool IsProtectedName(string folderName) => ProtectedSet.Contains(folderName);

    /// <summary>
    /// Build the plan to bring <paramref name="profileDir"/> down to
    /// <paramref name="targetTotalBytes"/>: walk the folder (skipping
    /// protected subtrees entirely), collect allowlisted cache folders
    /// with their sizes/ages, then pick oldest-first until the excess
    /// is covered. The cap-driven caller passes the cap as the target;
    /// a manual "clean caches now" passes 0 (every disposable folder).
    /// A missing folder plans as an empty, zero-byte no-op.
    /// </summary>
    public static CacheCleanupPlan BuildPlan(string profileDir, long targetTotalBytes)
    {
        var candidates = new List<CacheFolderEntry>();
        long total = 0;
        try
        {
            if (Directory.Exists(profileDir))
            {
                total = ProfileDataInfo.MeasureFolderBytes(profileDir) ?? 0;
                var stack = new Stack<string>();
                stack.Push(profileDir);
                while (stack.Count > 0)
                {
                    var dir = stack.Pop();
                    List<string> subdirs;
                    try { subdirs = Directory.EnumerateDirectories(dir).ToList(); }
                    catch { continue; /* unreadable directory — skip its subtree */ }
                    foreach (var sub in subdirs)
                    {
                        var name = Path.GetFileName(sub);
                        if (ProtectedSet.Contains(name))
                            continue; // never descend, never delete
                        if (DisposableSet.Contains(name))
                        {
                            var bytes = ProfileDataInfo.MeasureFolderBytes(sub) ?? 0;
                            DateTime lastWrite;
                            try { lastWrite = Directory.GetLastWriteTimeUtc(sub); }
                            catch { lastWrite = DateTime.MinValue; }
                            candidates.Add(new CacheFolderEntry(
                                Path.GetRelativePath(profileDir, sub), bytes,
                                new DateTimeOffset(DateTime.SpecifyKind(lastWrite, DateTimeKind.Utc))));
                            continue; // a cache folder's contents are part of its size — don't descend
                        }
                        stack.Push(sub);
                    }
                }
            }
        }
        catch
        {
            // Measuring must never be fatal; a partial plan just frees less.
        }

        var over = Math.Max(0, total - Math.Max(0, targetTotalBytes));
        var toDelete = new List<CacheFolderEntry>();
        if (over > 0)
        {
            long freed = 0;
            foreach (var entry in candidates.OrderBy(e => e.LastWriteUtc).ThenBy(e => e.RelativePath, StringComparer.Ordinal))
            {
                if (freed >= over)
                    break;
                toDelete.Add(entry);
                freed += entry.Bytes;
            }
        }
        return new CacheCleanupPlan
        {
            ProfileDir = profileDir,
            TotalBytes = total,
            TargetBytes = Math.Max(0, targetTotalBytes),
            Candidates = candidates,
            ToDelete = toDelete,
        };
    }
}

// ───────────────────── proxy readout (S8 #77) ─────────────────────

/// <summary>
/// The effective Windows system proxy state (S8 #77), as read by the
/// shell from the current user's WinINET settings and interpreted
/// here. WebView2 uses the system proxy by default and MuseDesktop
/// never overrides it (no browser arguments anywhere — see
/// DECISIONS.md), so this readout IS the app's proxy state. The server
/// text is display-sanitized: any userinfo ("user:pass@host") is
/// stripped — proxy credentials are never shown or logged.
/// </summary>
public sealed record ProxyInfo(bool Enabled, string? Server, bool HasPac)
{
    public static ProxyInfo Parse(bool enabled, string? server, string? autoConfigUrl)
    {
        var clean = SanitizeServer(server);
        return new ProxyInfo(enabled, clean, !string.IsNullOrWhiteSpace(autoConfigUrl));
    }

    /// <summary>Strip any "userinfo@" prefix from each server entry ("http=user:pass@h:1;https=h2:2" → "http=h:1;https=h2:2").</summary>
    public static string? SanitizeServer(string? server)
    {
        if (string.IsNullOrWhiteSpace(server))
            return null;
        var parts = server.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(part =>
            {
                var eq = part.IndexOf('=');
                var prefix = eq >= 0 ? part[..(eq + 1)] : "";
                var host = eq >= 0 ? part[(eq + 1)..] : part;
                var at = host.LastIndexOf('@');
                if (at >= 0)
                    host = host[(at + 1)..];
                return prefix + host.Trim();
            })
            .Where(p => p.Length > 0)
            .ToList();
        return parts.Count == 0 ? null : string.Join("; ", parts);
    }

    /// <summary>The Diagnostics line for this state.</summary>
    public string Describe()
    {
        if (Enabled && Server is not null)
            return $"System proxy on: {Server} (from Windows settings; WebView2 follows it automatically)"
                   + (HasPac ? ". A PAC auto-config file is also configured." : "");
        if (HasPac)
            return "Automatic proxy configuration (PAC) from Windows is in effect; WebView2 follows it automatically.";
        if (Enabled)
            return "Windows reports the system proxy as on but names no server; WebView2 follows Windows either way.";
        return "No system proxy — WebView2 connects directly (the Windows system proxy is off).";
    }
}

// ───────────────────── WebView2 runtime health (S8 #78) ─────────────────────

public enum RuntimeHealthStatus
{
    Ok,
    Missing,
    Outdated,
    FixedVersionFolderPresent,
}

/// <summary>The runtime-health verdict (S8 #78): status, the version found (null when none), and the plain-language line for the log/Diagnostics.</summary>
public sealed record RuntimeHealthResult(RuntimeHealthStatus Status, string? Version, string Message);

public static class RuntimeHealth
{
    /// <summary>
    /// The "far out of date" floor. Set 2026-10-24 at 120 (December
    /// 2023): comfortably past every WebView2 API this app calls (Find,
    /// certificate-error actions, non-default permission settings all
    /// predate it), old enough that nothing supported trips it, and
    /// honest about its purpose — it flags genuinely ancient runtimes
    /// that miss security years, not last month's build.
    /// </summary>
    public static readonly Version MinimumVersion = new(120, 0, 0, 0);

    /// <summary>
    /// Evaluate the runtime found on this PC. <paramref name="browserVersionString"/>
    /// is the Evergreen runtime's version (null/blank = not installed).
    /// <paramref name="fixedVersionFolderPresent"/> notes a
    /// Fixed-Version runtime folder sitting beside the exe — the app
    /// ships Evergreen and passes no executable folder, so such a
    /// folder is inert; it's reported, never used.
    /// </summary>
    public static RuntimeHealthResult Evaluate(string? browserVersionString, bool fixedVersionFolderPresent)
    {
        var fixedNote = fixedVersionFolderPresent
            ? " Note: a Fixed-Version WebView2 runtime folder sits beside the app, but MuseDesktop uses the Evergreen runtime — the folder is ignored."
            : "";
        if (string.IsNullOrWhiteSpace(browserVersionString)
            || !Version.TryParse(browserVersionString.Trim(), out var version))
        {
            return new RuntimeHealthResult(RuntimeHealthStatus.Missing, null,
                "WebView2 Runtime not found (or its version couldn't be read). MuseDesktop needs the Evergreen WebView2 Runtime." + fixedNote);
        }
        var text = version.ToString();
        if (version < MinimumVersion)
        {
            return new RuntimeHealthResult(RuntimeHealthStatus.Outdated, text,
                $"WebView2 Runtime {text} is far out of date (this app expects at least {MinimumVersion}). " +
                "Update the WebView2 Runtime (it normally updates itself with Edge) — old runtimes miss security fixes and newer features." + fixedNote);
        }
        if (fixedVersionFolderPresent)
        {
            return new RuntimeHealthResult(RuntimeHealthStatus.FixedVersionFolderPresent, text,
                $"WebView2 Runtime {text} (Evergreen) — healthy." + fixedNote);
        }
        return new RuntimeHealthResult(RuntimeHealthStatus.Ok, text,
            $"WebView2 Runtime {text} (Evergreen) — healthy.");
    }
}

// ───────────────────── backup rotation (S8 #79) ─────────────────────

/// <summary>One automatic backup file, parsed from its name (S8 #79).</summary>
public sealed record BackupFileInfo(string Kind, DateTimeOffset Stamp, string FileName);

/// <summary>
/// Automatic rotating backups (S8 #79): timestamped copies of
/// settings.json and the download history in a <c>backups</c> folder,
/// at most one new pair per day from the automatic path (plus on
/// demand in Settings), keeping the last <see cref="KeepCount"/> per
/// kind. Names carry their stamp so cataloguing and pruning are pure
/// functions of file names: <c>auto-settings-20261024-153000.json</c>.
/// The naming deliberately doesn't collide with the reset flow's
/// <c>settings-backup-*.json</c> (S5 #42), which is a separate,
/// user-facing undo file.
/// </summary>
public static class BackupRotation
{
    public const int KeepCount = 5;
    public const string SettingsKind = "settings";
    public const string DownloadsKind = "downloads";

    private const string StampFormat = "yyyyMMdd-HHmmss";

    public static string FileNameFor(string kind, DateTimeOffset now) =>
        $"auto-{kind}-{now.ToString(StampFormat, System.Globalization.CultureInfo.InvariantCulture)}.json";

    /// <summary>Parse a backup file name; null when it isn't one of ours.</summary>
    public static BackupFileInfo? Parse(string fileName)
    {
        var name = Path.GetFileName(fileName);
        if (!name.StartsWith("auto-", StringComparison.OrdinalIgnoreCase)
            || !name.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            return null;
        var body = name["auto-".Length..^".json".Length];
        foreach (var kind in new[] { SettingsKind, DownloadsKind })
        {
            if (!body.StartsWith(kind + "-", StringComparison.OrdinalIgnoreCase))
                continue;
            var stampText = body[(kind.Length + 1)..];
            if (DateTime.TryParseExact(stampText, StampFormat,
                    System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.None, out var stamp))
            {
                return new BackupFileInfo(kind,
                    new DateTimeOffset(DateTime.SpecifyKind(stamp, DateTimeKind.Local)), name);
            }
            return null;
        }
        return null;
    }

    /// <summary>All backups among <paramref name="fileNames"/>, newest first.</summary>
    public static IReadOnlyList<BackupFileInfo> Catalog(IEnumerable<string> fileNames) =>
        fileNames.Select(Parse).Where(i => i is not null).Cast<BackupFileInfo>()
            .OrderByDescending(i => i.Stamp).ThenBy(i => i.FileName, StringComparer.Ordinal)
            .ToList();

    /// <summary>Names to delete so <paramref name="kind"/> keeps only its newest <see cref="KeepCount"/> backups.</summary>
    public static IReadOnlyList<string> PruneCandidates(IEnumerable<string> fileNames, string kind) =>
        Catalog(fileNames).Where(i => i.Kind == kind)
            .Skip(KeepCount).Select(i => i.FileName).ToList();

    /// <summary>The automatic path's throttle: a backup of <paramref name="kind"/> is due when none exists stamped today (the caller's local date).</summary>
    public static bool IsBackupDue(IEnumerable<string> fileNames, string kind, DateTimeOffset now) =>
        !Catalog(fileNames).Any(i => i.Kind == kind && i.Stamp.Date == now.Date);
}

// ───────────────────── diagnostics report (S8 #73/#74/#77/#78) ─────────────────────

/// <summary>Everything the Diagnostics window shows (S8), as plain data the shell collects and Core composes — the full diagnostics window in S10 builds on these same models.</summary>
public sealed class DiagnosticsData
{
    public required string AppVersion { get; init; }
    public bool SafeMode { get; init; }
    public required string SettingsPath { get; init; }
    public required string LogPath { get; init; }
    public required string ProfileDir { get; init; }
    public RuntimeHealthResult? Runtime { get; init; }
    public ProxyInfo? Proxy { get; init; }
    public MemorySnapshot? Memory { get; init; }
    public MemorySnapshot? MemoryBeforeSuspend { get; init; }
    public MemorySnapshot? MemoryAfterResume { get; init; }
    public IReadOnlyList<StartupTimings> StartupRuns { get; init; } = Array.Empty<StartupTimings>();
}

public static class DiagnosticsReport
{
    public static string Compose(DiagnosticsData data)
    {
        var lines = new List<string>
        {
            $"MuseDesktop {data.AppVersion}" + (data.SafeMode ? " — SAFE MODE (started at Home, update check skipped)" : ""),
            "",
            "WebView2 runtime",
            "  " + (data.Runtime?.Message ?? "Not checked yet (the web view hasn't started)."),
            "",
            "Proxy (Windows system settings — WebView2 follows them; MuseDesktop never overrides them)",
            "  " + (data.Proxy?.Describe() ?? "Couldn't be read."),
            "",
            "Memory",
            "  " + (data.Memory?.Describe() ?? "Not available (the web view isn't running)."),
        };
        if (MemoryReadout.SuspendComparison(data.MemoryBeforeSuspend, data.MemoryAfterResume) is { } comparison)
            lines.Add("  " + comparison);
        lines.Add("");
        lines.Add("Startup (this PC, most recent first)");
        if (data.StartupRuns.Count == 0)
        {
            lines.Add("  No completed startup has been recorded yet.");
        }
        else
        {
            foreach (var run in data.StartupRuns)
                lines.Add($"  {run.StartedUtc.ToLocalTime():yyyy-MM-dd HH:mm} — {run.Describe()}");
        }
        lines.Add("");
        lines.Add("Files");
        lines.Add($"  Settings: {data.SettingsPath}");
        lines.Add($"  Log: {data.LogPath}");
        lines.Add($"  Active profile data: {data.ProfileDir}");
        return string.Join(Environment.NewLine, lines);
    }
}
