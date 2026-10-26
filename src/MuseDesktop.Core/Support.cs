using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace MuseDesktop.Core;

// ───────────────────────── log reading (S10 #89/#93) ─────────────────────────

/// <summary>
/// Reading the diagnostic log for support surfaces (v0.14.0, S10
/// #89/#93): the recent-errors list in Diagnostics, "Copy last error",
/// and the bundle's log excerpt. Error lines are ALWAYS returned
/// redacted through the S7 <see cref="RedactionAudit"/> — a line that
/// somehow carries a query string or a token never reaches the
/// clipboard, the diagnostics text, or a support bundle in the clear.
/// </summary>
public static class LogReading
{
    /// <summary>The marker <see cref="DiagnosticLog"/> writes on error lines.</summary>
    public const string ErrorMarker = " [ERROR] ";

    /// <summary>The last <paramref name="maxLines"/> lines of <paramref name="text"/> (fewer when the text is shorter).</summary>
    public static string TailLines(string? text, int maxLines)
    {
        if (string.IsNullOrEmpty(text) || maxLines <= 0)
            return "";
        var lines = text.Replace("\r\n", "\n").Split('\n');
        // A trailing newline leaves an empty final element — it isn't a line.
        var end = lines.Length;
        if (end > 0 && lines[end - 1].Length == 0)
            end--;
        var start = Math.Max(0, end - maxLines);
        return string.Join("\n", lines[start..end]);
    }

    /// <summary>The raw (unredacted) lines carrying the ERROR marker, chronological.</summary>
    private static List<string> ErrorLines(string? text)
    {
        var result = new List<string>();
        if (string.IsNullOrEmpty(text))
            return result;
        foreach (var line in text.Replace("\r\n", "\n").Split('\n'))
            if (line.Contains(ErrorMarker, StringComparison.Ordinal))
                result.Add(line.TrimEnd('\r'));
        return result;
    }

    /// <summary>The most recent <paramref name="maxCount"/> error lines, chronological, each redacted.</summary>
    public static IReadOnlyList<string> RecentErrors(string? text, int maxCount)
    {
        var errors = ErrorLines(text);
        if (errors.Count == 0 || maxCount <= 0)
            return Array.Empty<string>();
        return errors.Skip(Math.Max(0, errors.Count - maxCount))
                     .Select(line => RedactionAudit.Redact(line))
                     .ToList();
    }

    /// <summary>The most recent error line, redacted — or null when the log has no errors.</summary>
    public static string? LastError(string? text)
    {
        var errors = ErrorLines(text);
        return errors.Count == 0 ? null : RedactionAudit.Redact(errors[^1]);
    }
}

// ───────────────────────── support bundle (S10 #90/#95) ─────────────────────────

/// <summary>One file inside the support bundle: its name in the zip and what it is, in plain words.</summary>
public sealed record BundleFile(string Name, string Description);

/// <summary>
/// The support bundle (v0.14.0, S10 #90): one .zip the user saves
/// locally and may attach to a bug report themselves — nothing is
/// uploaded anywhere. The contents are fixed by this planner, the
/// preview dialog reads the same list, and the manifest inside the
/// zip repeats it, so what the user is told is what ships.
///
/// The bundle's settings file is a STRICTER sanitization than the
/// Settings window's own export. The plain/encrypted export is the
/// user's private backup (it must restore everything, so it carries
/// the app-lock record and per-profile last pages). The bundle is
/// meant to be handed to another person, so
/// <see cref="SanitizeSettingsForBundle"/> strips: the app-lock record
/// (salt/hash/iterations AND the enabled flag — support learns "app
/// lock: on/off" from diagnostics.txt instead), every last-page URL,
/// and every per-profile custom start-page URL. The bundle settings
/// JSON therefore contains no lock record and no full URLs — pinned by
/// tests, not by intentions.
/// </summary>
public static class SupportBundle
{
    public const string DiagnosticsFile = "diagnostics.txt";
    public const string LogFile = "log-redacted.txt";
    public const string SettingsFile = "settings-support.json";
    public const string DownloadsFile = "downloads-history.json";
    public const string ManifestFile = "manifest.txt";
    public const string ScreenshotFile = "screenshot.png";

    /// <summary>How many trailing log lines the bundle carries (the whole log is capped at ~1 MB by rollover; a tail is what support reads).</summary>
    public const int LogTailLineCount = 500;

    /// <summary>The bundle's file list, in zip order (manifest last in the list but written first-class like the others).</summary>
    public static IReadOnlyList<BundleFile> Contents(bool includeScreenshot)
    {
        var files = new List<BundleFile>
        {
            new(ManifestFile, "This list: what every file in the bundle is."),
            new(DiagnosticsFile, "The Diagnostics report: versions, runtime health, proxy, memory, startup timings, data sizes, and the most recent errors (redacted)."),
            new(LogFile, $"The last {LogTailLineCount} lines of the diagnostic log, redacted — web addresses are host + path only and anything that looks like a token, credential, or email is removed first."),
            new(SettingsFile, "Your settings with the sensitive parts removed: the app-lock record, your last-page addresses, and custom start-page addresses are stripped out (see manifest.txt)."),
            new(DownloadsFile, "Your download history: file names, sizes, dates, and paths only — it never contained the addresses files came from."),
        };
        if (includeScreenshot)
            files.Add(new(ScreenshotFile, "A picture of the MuseDesktop window that YOU reviewed in the preview before saving. It can show your conversation — that's why it's opt-in."));
        return files;
    }

    /// <summary>The statement every bundle surface repeats: what is NOT in the bundle.</summary>
    public const string ExclusionsStatement =
        "Not included: cookies, sign-in tokens, page or conversation content, web addresses from your settings, " +
        "and the app-lock password record. Nothing is uploaded anywhere — the bundle is a file you save, and " +
        "you choose whether to share it.";

    /// <summary>The preview dialog's text: the file list plus the exclusions statement.</summary>
    public static string PreviewText(bool includeScreenshot)
    {
        var lines = new List<string> { "The support bundle is one .zip file containing:" };
        foreach (var file in Contents(includeScreenshot))
            lines.Add($"• {file.Name} — {file.Description}");
        lines.Add("");
        lines.Add(ExclusionsStatement);
        return string.Join("\n", lines);
    }

    /// <summary>The manifest.txt written inside the zip.</summary>
    public static string ComposeManifest(string appVersion, DateTimeOffset created, bool includeScreenshot)
    {
        var lines = new List<string>
        {
            "MuseDesktop support bundle",
            $"Created: {created:yyyy-MM-dd HH:mm} (this PC's local time)",
            $"MuseDesktop version: {appVersion}",
            "",
            "Files in this bundle:",
        };
        foreach (var file in Contents(includeScreenshot))
            lines.Add($"- {file.Name} — {file.Description}");
        lines.Add("");
        lines.Add(ExclusionsStatement);
        return string.Join("\n", lines);
    }

    /// <summary>The bundle's log excerpt: the tail, redacted. Never the raw file.</summary>
    public static string LogExcerpt(string? logText) =>
        RedactionAudit.Redact(LogReading.TailLines(logText, LogTailLineCount));

    /// <summary>
    /// The bundle-safe copy of the settings (see the class remarks for
    /// the rules). The input is not modified — the caller keeps using
    /// its live settings; this returns a deep copy with the sensitive
    /// parts stripped.
    /// </summary>
    public static AppSettings SanitizeSettingsForBundle(AppSettings settings)
    {
        var copy = settings.Clone();
        // The app-lock record: a salted hash is still a credential
        // verifier — it never leaves in a bundle. The enabled flag goes
        // too so the stripped record can't look half-alive; support
        // sees "App lock: on/off" in diagnostics.txt.
        copy.AppLockEnabled = false;
        copy.AppLockSalt = null;
        copy.AppLockHash = null;
        copy.AppLockIterations = 0;
        // Last pages are full conversation URLs; custom start pages are
        // full URLs the user typed. Both stay home.
        copy.LastUrl = null;
        copy.LastUrlByProfile = new Dictionary<string, string>();
        foreach (var profile in copy.Profiles)
            profile.CustomStartUrl = null;
        return copy;
    }

    /// <summary>
    /// The bundle's settings file text: the sanitized copy, serialized
    /// WITHOUT the settings object's derived properties. The plain
    /// serializer emits get-only computed values too (startupUrl,
    /// activeLastUrl, activePlacement — import ignores them, they are
    /// pure derivations), and a derived startup URL would put a full
    /// URL back into a file whose contract is "no full URLs" — so the
    /// bundle form drops them by name.
    /// </summary>
    public static string SettingsJson(AppSettings settings) =>
        JsonSerializer.Serialize(SanitizeSettingsForBundle(settings), BundleJsonOptions);

    private static readonly JsonSerializerOptions BundleJsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        TypeInfoResolver = new DefaultJsonTypeInfoResolver
        {
            Modifiers = { DropDerivedSettingsProperties },
        },
    };

    private static void DropDerivedSettingsProperties(JsonTypeInfo typeInfo)
    {
        if (typeInfo.Type != typeof(AppSettings))
            return;
        foreach (var name in new[] { "startupUrl", "activeLastUrl", "activePlacement" })
        {
            var property = typeInfo.Properties.FirstOrDefault(p => p.Name == name);
            if (property is not null)
                typeInfo.Properties.Remove(property);
        }
    }

    /// <summary>Default bundle file name for the save dialog.</summary>
    public static string DefaultFileName(DateTimeOffset now) =>
        $"MuseDesktop-support-{now:yyyyMMdd-HHmm}.zip";
}

// ───────────────────────── help catalog (S10 #91) ─────────────────────────

/// <summary>What a help page's action button does (v0.14.0, S10 #91).</summary>
public enum HelpAction
{
    /// <summary>No in-app action fits — the page is steps only.</summary>
    None,

    /// <summary>Open Settings at <see cref="HelpPage.SettingsSectionId"/>.</summary>
    OpenSettings,

    /// <summary>Open the Diagnostics window.</summary>
    OpenDiagnostics,

    /// <summary>Open the log viewer.</summary>
    OpenLogViewer,
}

/// <summary>
/// One offline troubleshooting page (v0.14.0, S10 #91). The catalog —
/// titles, symptom words, and the action each page offers — lives here
/// so it's unit-tested; the page BODIES live in the shipped Help.md
/// under a <c>## Title</c> heading with exactly this title (a test pins
/// the two together, the same drift guard as What's New).
/// </summary>
public sealed record HelpPage(
    string Id, string Title, string Keywords,
    HelpAction Action, string? ActionLabel, string? SettingsSectionId);

public static class HelpCatalog
{
    /// <summary>The generated commands page (R1 #109): listed here like every page, with its command list appended live from the palette registry by the Help window.</summary>
    public const string CommandsPageId = "commands";

    public static readonly IReadOnlyList<HelpPage> Pages = new[]
    {
        new HelpPage("sign-in-loop",
            "Muse keeps asking me to sign in",
            "sign in login log in signed out loop session expired cookie password keeps asking again",
            HelpAction.OpenSettings, "Open Settings — clear site data", SettingsSearch.PrivacyId),
        new HelpPage("microphone-denied",
            "Voice dictation doesn't hear me",
            "microphone mic voice dictation speech hear denied permission no sound input device",
            HelpAction.OpenSettings, "Open Settings — test the microphone", SettingsSearch.PrivacyId),
        new HelpPage("notifications-silent",
            "I'm not getting notifications",
            "notifications alerts silent balloon toast quiet hours do not disturb focus assist finished task",
            HelpAction.OpenSettings, "Open Settings — tray & notifications", SettingsSearch.TrayId),
        new HelpPage("webview2-missing",
            "The WebView2 Runtime is missing or too old",
            "webview2 runtime missing outdated old version edge browser engine won't start blank",
            HelpAction.OpenDiagnostics, "Open Diagnostics", null),
        new HelpPage("downloads-failing",
            "Downloads fail or never finish",
            "downloads fail failing stuck never finish error file save folder disk interrupted",
            HelpAction.OpenSettings, "Open Settings — downloads", SettingsSearch.DownloadsId),
        new HelpPage("app-lock-forgotten",
            "I forgot my app password",
            "app lock password pin forgot locked out unlock reset settings.json",
            HelpAction.OpenSettings, "Open Settings — data folder", SettingsSearch.DataId),
        new HelpPage("page-wont-load",
            "The page won't load",
            "page won't load blank offline internet connection dns error failed try again network",
            HelpAction.OpenDiagnostics, "Open Diagnostics", null),
        // R1 (#109): listed last like the S7 privacy section in
        // SettingsSearch — the troubleshooting pages keep their
        // established first-match behavior for the queries they own.
        new HelpPage(CommandsPageId,
            "Commands & the command palette",
            "command palette ctrl k keyboard commands switch window recent actions quick open tray middle click profile chords",
            HelpAction.None, null, null),
        // R4 (#131–#140): the downloads round-2 how-to page, also
        // listed last — it answers "how do I organize downloads",
        // while downloads-failing keeps owning the failure queries.
        new HelpPage("downloads-organizing",
            "Organizing downloads: sorting, archiving, notes, and checking a file",
            "organize sort sorting subfolder archive dated note verify sha-256 hash genuine duplicate already downloaded retry links batch pile messy filing",
            HelpAction.OpenSettings, "Open Settings — downloads", SettingsSearch.DownloadsId),
    };

    public static HelpPage? ById(string? id) =>
        id is null ? null : Pages.FirstOrDefault(p => p.Id == id);

    /// <summary>
    /// Pages matching <paramref name="query"/> — every term must
    /// prefix-match a word in the title or keywords (the Settings
    /// search rule, so the two searches feel the same). A blank query
    /// returns every page in catalog order.
    /// </summary>
    public static IReadOnlyList<HelpPage> Search(string? query)
    {
        if (string.IsNullOrWhiteSpace(query))
            return Pages;
        var terms = query.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                         .Select(t => t.ToLowerInvariant())
                         .ToList();
        if (terms.Count == 0)
            return Pages;
        return Pages.Where(p => terms.All(t => Matches(p, t))).ToList();
    }

    private static bool Matches(HelpPage page, string term)
    {
        var haystack = (page.Title + " " + page.Keywords).ToLowerInvariant();
        return haystack.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
                       .Any(word => word.StartsWith(term, StringComparison.Ordinal));
    }

    /// <summary>
    /// The body of a page from Help.md-shaped markdown: the text under
    /// the <c>## Title</c> heading, up to the next <c>## </c> heading.
    /// Null when the document has no such section.
    /// </summary>
    public static string? ExtractBody(string? markdown, HelpPage page)
    {
        if (string.IsNullOrEmpty(markdown))
            return null;
        var lines = markdown.Replace("\r\n", "\n").Split('\n');
        var start = -1;
        for (var i = 0; i < lines.Length; i++)
        {
            if (!lines[i].StartsWith("## ", StringComparison.Ordinal))
                continue;
            if (start >= 0)
                return JoinBody(lines, start, i);
            if (string.Equals(lines[i][3..].Trim(), page.Title, StringComparison.Ordinal))
                start = i + 1;
        }
        return start >= 0 ? JoinBody(lines, start, lines.Length) : null;
    }

    private static string? JoinBody(string[] lines, int from, int to)
    {
        var body = string.Join("\n", lines[from..to]).Trim();
        return body.Length == 0 ? null : body;
    }
}

// ───────────────────────── guided self-tests (S10 #92) ─────────────────────────

/// <summary>How the microphone test ended (v0.14.0, S10 #92).</summary>
public enum MicTestOutcome
{
    /// <summary>The mic was granted and sound actually arrived during the listening window.</summary>
    HeardSound,

    /// <summary>The mic was granted but stayed silent — muted device, wrong input, or nothing said.</summary>
    GrantedButSilent,

    /// <summary>The request was denied (Windows microphone privacy, or the browser engine refused).</summary>
    Denied,

    /// <summary>No microphone device exists / none is enabled.</summary>
    NoDevice,

    /// <summary>The device exists but couldn't be opened (busy, unplugged mid-test, driver error).</summary>
    DeviceError,

    /// <summary>The test page never answered.</summary>
    TimedOut,
}

/// <summary>
/// The guided self-tests' wording (v0.14.0, S10 #92), composed here so
/// the exact lines the user reads are unit-tested.
///
/// Notification behavior, decided and documented: a test the user
/// explicitly asks for is SENT even when quiet hours / Windows Do Not
/// Disturb would hold back automatic notifications — a test that
/// silently does nothing teaches nothing — and the result line says
/// so, naming the suppression that automatic notifications are under.
///
/// Microphone approach (verified against the pinned WebView2 SDK
/// 1.0.4258.31): the test window hosts a WebView2 on the ACTIVE
/// profile's environment, serving the app's own built-in test page
/// from a temporary folder under the reserved host
/// <c>musedesktop.test</c> via <c>SetVirtualHostNameToFolderMapping</c>
/// (present in the pinned SDK), so the page is a secure context and
/// its microphone request travels the real WebView2
/// <c>PermissionRequested</c> path. That window's handler grants the
/// microphone to the test host ONLY — the production policy (grant
/// mic/notifications to muse.ai, deny everything else) is untouched,
/// and the test page additionally verifies an actual audio device
/// answers, which the permission record alone can't prove.
/// </summary>
public static class GuidedSelfTests
{
    /// <summary>Peak amplitude (0–1) at or above which the listening window counts as "sound arrived".</summary>
    public const double HeardPeakThreshold = 0.02;

    /// <summary>The virtual host the built-in microphone test page is served from.</summary>
    public const string MicTestHost = "musedesktop.test";

    /// <summary>
    /// Interpret the test page's posted report
    /// (<c>{"ok":true,"peak":0.4}</c> or <c>{"ok":false,"error":"NotAllowedError"}</c>).
    /// </summary>
    public static MicTestOutcome ParseMicReport(string? reportJson)
    {
        if (string.IsNullOrWhiteSpace(reportJson))
            return MicTestOutcome.TimedOut;
        try
        {
            using var doc = JsonDocument.Parse(reportJson);
            var root = doc.RootElement;
            if (root.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.True)
            {
                var peak = root.TryGetProperty("peak", out var p) && p.ValueKind == JsonValueKind.Number
                    ? p.GetDouble()
                    : 0;
                return peak >= HeardPeakThreshold ? MicTestOutcome.HeardSound : MicTestOutcome.GrantedButSilent;
            }
            var error = root.TryGetProperty("error", out var e) && e.ValueKind == JsonValueKind.String
                ? e.GetString() ?? ""
                : "";
            return error switch
            {
                "NotAllowedError" or "SecurityError" => MicTestOutcome.Denied,
                "NotFoundError" or "OverconstrainedError" => MicTestOutcome.NoDevice,
                _ => MicTestOutcome.DeviceError,
            };
        }
        catch (Exception)
        {
            return MicTestOutcome.DeviceError;
        }
    }

    /// <summary>The result line Settings shows (and announces) for a microphone test outcome.</summary>
    public static string MicrophoneLine(MicTestOutcome outcome) => outcome switch
    {
        MicTestOutcome.HeardSound =>
            "Microphone test passed — the microphone was granted and sound was picked up. " +
            "Muse's voice input uses this same microphone and permission path.",
        MicTestOutcome.GrantedButSilent =>
            "The microphone was granted, but no sound was picked up during the test. " +
            "Check that the right input device is selected and not muted in Windows Settings → Sound → Input, then test again.",
        MicTestOutcome.Denied =>
            "The microphone request was denied. Windows may be blocking microphone access for apps: " +
            "check Windows Settings → Privacy & security → Microphone (both the master switch and \"Let desktop apps access your microphone\"), then test again.",
        MicTestOutcome.NoDevice =>
            "No microphone was found. Connect or enable one in Windows Settings → Sound → Input, then test again.",
        MicTestOutcome.TimedOut =>
            "The test didn't finish — the web view may still be starting. Give it a moment and test again.",
        _ =>
            "The microphone couldn't be started (it may be in use by another app, or it was unplugged). " +
            "Close other apps using it and test again.",
    };

    /// <summary>
    /// The line about Muse's OWN microphone permission: the app's
    /// standing policy plus the profile's saved choice when one exists
    /// (<paramref name="savedState"/> = the S7 permission row's state
    /// for microphone/muse.ai, null when there is no saved row).
    /// </summary>
    public static string SavedMicLine(string? savedState) => savedState is null
        ? "Muse's microphone: MuseDesktop grants it to muse.ai automatically whenever Muse asks — no saved choice exists yet."
        : $"Muse's microphone: the saved choice for muse.ai is \"{savedState}\" (MuseDesktop's policy grants the microphone to muse.ai automatically).";

    /// <summary>The result line for the test-notification button.</summary>
    public static string NotificationLine(NotificationSuppression suppression, bool shown)
    {
        if (!shown)
            return "The test notification couldn't be shown — the tray icon isn't available right now.";
        if (suppression == NotificationSuppression.None)
            return "Test notification sent. If it didn't appear, check Windows Settings → Notifications: " +
                   "MuseDesktop must be allowed, and Focus Assist can hold notifications back.";
        return "Test notification sent — because you asked for it, it was shown even though " +
               $"{NotificationPolicy.ReasonText(suppression)} is currently keeping MuseDesktop's automatic " +
               "notifications (like a finished download) quiet.";
    }
}
