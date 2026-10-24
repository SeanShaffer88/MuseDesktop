using System.Text.Json;
using System.Text.RegularExpressions;

namespace MuseDesktop.Core;

// ───────────────────────── granular clear-data (S7 #61) ─────────────────────────

/// <summary>What a clear-data run removes (S7 #61): the three choices Settings offers.</summary>
public enum ClearDataChoice
{
    /// <summary>Cookies only. Signs the profile out of Muse (chats are server-side and unaffected).</summary>
    Cookies,

    /// <summary>Disk + storage caches only. Nobody is signed out; pages may load a little slower once.</summary>
    Cache,

    /// <summary>Everything the profile stored — cookies, caches, site storage. The pre-v0.11.0 behavior.</summary>
    AllSiteData,
}

/// <summary>Which profiles a clear-data run touches (S7 #61).</summary>
public enum ClearDataScope
{
    /// <summary>Only the profile this launch is running.</summary>
    ActiveProfile,

    /// <summary>Every profile on this PC.</summary>
    AllProfiles,
}

/// <summary>
/// The clear-data plan model (S7 #61): the plain-language facts the UI
/// shows BEFORE anything is deleted (what goes, whether it signs you
/// out, how many profiles it touches). The mapping from a choice to
/// WebView2's browsing-data kinds lives in the shell — Core stays free
/// of the SDK — but everything the user reads comes from here so the
/// wording is tested exactly as shipped.
/// </summary>
public static class ClearDataPlan
{
    public static string Title(ClearDataChoice choice) => choice switch
    {
        ClearDataChoice.Cookies => "Cookies only",
        ClearDataChoice.Cache => "Cache only",
        ClearDataChoice.AllSiteData => "All site data",
        _ => "All site data",
    };

    /// <summary>True when the choice removes cookies — the profile(s) it touches sign out of Muse.</summary>
    public static bool SignsOut(ClearDataChoice choice) => choice != ClearDataChoice.Cache;

    /// <summary>What the choice deletes, in plain words (the confirm dialog's body).</summary>
    public static string Describe(ClearDataChoice choice) => choice switch
    {
        ClearDataChoice.Cookies =>
            "Deletes the cookies Muse and its sign-in pages stored. " +
            "The profile signs out of Muse; your chats live in your Muse account and are not affected.",
        ClearDataChoice.Cache =>
            "Deletes cached files (the disk cache and cache storage) so pages load fresh. " +
            "Nobody is signed out and no sign-in data is touched.",
        _ =>
            "Deletes everything the profile stored locally: cookies, caches, and site storage. " +
            "The profile signs out of Muse; your chats live in your Muse account and are not affected.",
    };

    /// <summary>The scope line for the confirm dialog ("for this profile (Work)" / "for all 3 profiles").</summary>
    public static string DescribeScope(ClearDataScope scope, string activeProfileName, int profileCount) =>
        scope == ClearDataScope.AllProfiles
            ? $"This applies to all {profileCount} profiles on this PC."
            : $"This applies to this profile ({activeProfileName}) only.";
}

// ───────────────────────── privacy report + denials (S7 #62/#67) ─────────────────────────

/// <summary>The kinds of blocking event the privacy report counts (S7 #67).</summary>
public enum PrivacyEventCategory
{
    /// <summary>A top-level navigation UrlPolicy refused (non-HTTPS scheme or unparsable).</summary>
    BlockedNavigation,

    /// <summary>A new-window request UrlPolicy refused outright.</summary>
    BlockedNewWindow,

    /// <summary>A permission request the app denied (anything but mic/notifications for muse.ai).</summary>
    DeniedPermission,
}

/// <summary>One recent permission denial (S7 #62): kind + host + when. Never a URL, never page content.</summary>
public sealed class PermissionDenial
{
    public string Kind { get; set; } = "";
    public string Host { get; set; } = "";
    public DateTimeOffset TimeUtc { get; set; }
}

/// <summary>
/// The privacy report's counters (S7 #67) and the recent-denials list
/// (S7 #62), persisted as one small JSON file beside the settings.
/// Deliberately counts and host names ONLY: no URL paths, no query
/// strings, no page content, no per-event timestamps beyond the
/// since-date and the short recent-denials list. A hand-edited or
/// corrupt file sanitizes back to something sane — see
/// <see cref="Sanitized"/>.
/// </summary>
public sealed class PrivacyStats
{
    /// <summary>Most distinct counter rows kept — one row is category|kind|host, so this bounds the file.</summary>
    public const int MaxCounterRows = 200;

    /// <summary>Most recent denials kept for the Permissions section.</summary>
    public const int MaxRecentDenials = 20;

    /// <summary>When counting started (first recorded event, or the last reset). Null = nothing recorded yet.</summary>
    public DateTimeOffset? SinceUtc { get; set; }

    /// <summary>Counts keyed "category|kind|host" (host may be empty for scheme-only blocks).</summary>
    public Dictionary<string, long> Counts { get; set; } = new();

    /// <summary>Newest-first permission denials for the Settings Permissions section.</summary>
    public List<PermissionDenial> RecentDenials { get; set; } = new();

    public static string Key(PrivacyEventCategory category, string kind, string host) =>
        $"{CategoryName(category)}|{kind}|{host}";

    public static string CategoryName(PrivacyEventCategory category) => category switch
    {
        PrivacyEventCategory.BlockedNavigation => "blocked-navigation",
        PrivacyEventCategory.BlockedNewWindow => "blocked-new-window",
        PrivacyEventCategory.DeniedPermission => "denied-permission",
        _ => "other",
    };

    /// <summary>Total events in one category.</summary>
    public long Total(PrivacyEventCategory category)
    {
        var prefix = CategoryName(category) + "|";
        long total = 0;
        foreach (var (key, count) in Counts)
            if (key.StartsWith(prefix, StringComparison.Ordinal))
                total += count;
        return total;
    }

    public long TotalBlocks =>
        Total(PrivacyEventCategory.BlockedNavigation)
        + Total(PrivacyEventCategory.BlockedNewWindow)
        + Total(PrivacyEventCategory.DeniedPermission);

    /// <summary>Counter rows for display, biggest first (ties broken by key so the list is stable).</summary>
    public IReadOnlyList<(string Category, string Kind, string Host, long Count)> Rows() =>
        Counts
            .Select(kv =>
            {
                var parts = kv.Key.Split('|');
                return (Category: parts.Length > 0 ? parts[0] : "",
                        Kind: parts.Length > 1 ? parts[1] : "",
                        Host: parts.Length > 2 ? parts[2] : "",
                        Count: kv.Value);
            })
            .OrderByDescending(r => r.Count)
            .ThenBy(r => r.Category, StringComparer.Ordinal)
            .ThenBy(r => r.Kind, StringComparer.Ordinal)
            .ThenBy(r => r.Host, StringComparer.Ordinal)
            .ToList();

    /// <summary>
    /// Record one event. Host and kind are normalized through the same
    /// rules sanitize applies, so a caller can pass a raw origin host or
    /// a permission-kind name without pre-cleaning.
    /// </summary>
    public void Record(PrivacyEventCategory category, string kind, string? host, DateTimeOffset whenUtc)
    {
        kind = CleanKind(kind);
        host = CleanHost(host);
        SinceUtc ??= whenUtc;
        var key = Key(category, kind, host);
        Counts[key] = Counts.TryGetValue(key, out var current) ? current + 1 : 1;
        if (category == PrivacyEventCategory.DeniedPermission)
        {
            RecentDenials.Insert(0, new PermissionDenial { Kind = kind, Host = host, TimeUtc = whenUtc });
            if (RecentDenials.Count > MaxRecentDenials)
                RecentDenials.RemoveRange(MaxRecentDenials, RecentDenials.Count - MaxRecentDenials);
        }
        TrimCounters();
    }

    /// <summary>Repair anything untrustworthy (hand-edited or corrupt file). Idempotent.</summary>
    public PrivacyStats Sanitized()
    {
        Counts ??= new Dictionary<string, long>();
        var cleaned = new Dictionary<string, long>();
        foreach (var (key, count) in Counts)
        {
            if (count <= 0 || key is null)
                continue;
            var parts = key.Split('|');
            if (parts.Length != 3)
                continue;
            var category = parts[0] is "blocked-navigation" or "blocked-new-window" or "denied-permission"
                ? parts[0]
                : null;
            if (category is null)
                continue;
            var rebuilt = $"{category}|{CleanKind(parts[1])}|{CleanHost(parts[2])}";
            cleaned[rebuilt] = cleaned.TryGetValue(rebuilt, out var existing) ? existing + count : count;
        }
        Counts = cleaned;
        TrimCounters();
        RecentDenials ??= new List<PermissionDenial>();
        RecentDenials = RecentDenials
            .Where(d => d is not null)
            .Select(d => new PermissionDenial
            {
                Kind = CleanKind(d.Kind),
                Host = CleanHost(d.Host),
                TimeUtc = d.TimeUtc,
            })
            .OrderByDescending(d => d.TimeUtc)
            .Take(MaxRecentDenials)
            .ToList();
        return this;
    }

    private void TrimCounters()
    {
        if (Counts.Count <= MaxCounterRows)
            return;
        // Deterministic trim: biggest counts survive, ties by key.
        var keep = Counts
            .OrderByDescending(kv => kv.Value)
            .ThenBy(kv => kv.Key, StringComparer.Ordinal)
            .Take(MaxCounterRows)
            .Select(kv => kv.Key)
            .ToHashSet();
        Counts = Counts.Where(kv => keep.Contains(kv.Key))
                       .ToDictionary(kv => kv.Key, kv => kv.Value);
    }

    /// <summary>A kind is a short label (a scheme or a permission kind): letters/digits/dash only, capped.</summary>
    public static string CleanKind(string? kind)
    {
        if (string.IsNullOrWhiteSpace(kind))
            return "unknown";
        var cleaned = new string(kind.Trim().ToLowerInvariant()
            .Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '-')
            .ToArray()).Trim('-');
        if (cleaned.Length == 0)
            return "unknown";
        return cleaned.Length <= 40 ? cleaned : cleaned[..40];
    }

    /// <summary>
    /// A host for display/storage: lower-case host characters only.
    /// Anything carrying a path, port, query, or user-info is not a
    /// host — it becomes "" (the row then reads as scheme-only).
    /// </summary>
    public static string CleanHost(string? host)
    {
        if (string.IsNullOrWhiteSpace(host))
            return "";
        var h = host.Trim().ToLowerInvariant();
        if (h.Length > 253)
            return "";
        foreach (var c in h)
            if (!(char.IsLetterOrDigit(c) || c is '.' or '-' or '_'))
                return "";
        return h;
    }
}

/// <summary>Loads/saves <see cref="PrivacyStats"/> — same atomic-write pattern as the settings store.</summary>
public static class PrivacyStatsStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public static PrivacyStats Load(string path)
    {
        try
        {
            if (!File.Exists(path))
                return new PrivacyStats();
            var stats = JsonSerializer.Deserialize<PrivacyStats>(File.ReadAllText(path), Options);
            return (stats ?? new PrivacyStats()).Sanitized();
        }
        catch (Exception)
        {
            return new PrivacyStats();
        }
    }

    public static void Save(string path, PrivacyStats stats)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(stats.Sanitized(), Options));
        File.Move(tmp, path, overwrite: true);
    }
}

// ───────────────────────── log redaction audit (S7 #66) ─────────────────────────

/// <summary>One thing the redaction audit found suspicious in a log line (S7 #66).</summary>
public sealed record RedactionFinding(int LineNumber, string Category, string Description);

/// <summary>
/// The log viewer's redaction check (S7 #66). The app's logging rules
/// already say URLs are host+path only and no tokens are ever logged;
/// this audit PROVES it on the actual log instead of asserting it: it
/// scans for web addresses carrying a query string or long fragment,
/// email addresses, credential-looking assignments, and long unbroken
/// tokens. Findings are reported by line number + kind only — the
/// matched text itself is never echoed back into the UI or a copy —
/// and <see cref="Redact"/> produces the copy-safe version of a log.
/// </summary>
public static class RedactionAudit
{
    public const string UrlWithQueryCategory = "url-with-query";
    public const string EmailCategory = "email-address";
    public const string CredentialCategory = "credential-assignment";
    public const string LongTokenCategory = "long-token";

    private static readonly Regex UrlWithQuery = new(
        @"https?://[^\s""'<>]+[?#][^\s""'<>]*", RegexOptions.Compiled);
    private static readonly Regex Email = new(
        @"\b[\w.+-]+@[\w-]+(\.[\w-]+)+\b", RegexOptions.Compiled);
    private static readonly Regex CredentialAssignment = new(
        @"(?i)\b(bearer|access[_\s-]?token|api[_\s-]?key|password|passwd|secret|session[_\s-]?id|auth[_\s-]?token)\b\s*[:=]\s*\S+",
        RegexOptions.Compiled);
    private static readonly Regex LongToken = new(
        @"\b[A-Za-z0-9+/=_-]{40,}\b", RegexOptions.Compiled);
    private static readonly Regex UrlParts = new(
        @"^(https?://[^\s?#]+)", RegexOptions.Compiled);

    public static string DescribeCategory(string category) => category switch
    {
        UrlWithQueryCategory => "a web address with a query string or fragment (those can carry tokens)",
        EmailCategory => "an email address",
        CredentialCategory => "something that looks like a credential (a token/password assignment)",
        LongTokenCategory => "a long unbroken token (40+ characters — could be a key or hash)",
        _ => "something that should not be in a log",
    };

    /// <summary>Scan log text line by line. A line can produce several findings (one per category).</summary>
    public static IReadOnlyList<RedactionFinding> Scan(string text)
    {
        var findings = new List<RedactionFinding>();
        if (string.IsNullOrEmpty(text))
            return findings;
        var lines = text.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            if (line.Length == 0)
                continue;
            var lineNumber = i + 1;
            if (UrlWithQuery.IsMatch(line))
                findings.Add(new RedactionFinding(lineNumber, UrlWithQueryCategory, DescribeCategory(UrlWithQueryCategory)));
            if (CredentialAssignment.IsMatch(line))
                findings.Add(new RedactionFinding(lineNumber, CredentialCategory, DescribeCategory(CredentialCategory)));
            if (Email.IsMatch(line))
                findings.Add(new RedactionFinding(lineNumber, EmailCategory, DescribeCategory(EmailCategory)));
            if (LongToken.IsMatch(StripBenignUrls(line)))
                findings.Add(new RedactionFinding(lineNumber, LongTokenCategory, DescribeCategory(LongTokenCategory)));
        }
        return findings;
    }

    /// <summary>
    /// The copy-safe version of a log: flagged spans replaced, the rest
    /// untouched. Ordinary host+path addresses survive — they are the
    /// log's designed content; only their query/fragment is cut.
    /// </summary>
    public static string Redact(string text)
    {
        if (string.IsNullOrEmpty(text))
            return text;
        var result = UrlWithQuery.Replace(text, m => UrlParts.Match(m.Value).Groups[1].Value);
        result = CredentialAssignment.Replace(result, m =>
            $"{m.Groups[1].Value} = [redacted]");
        result = Email.Replace(result, "[redacted email]");
        result = LongToken.Replace(result, "[redacted token]");
        return result;
    }

    /// <summary>The one-line verdict the viewer shows (and says honestly when the log is NOT clean).</summary>
    public static string Summarize(IReadOnlyList<RedactionFinding> findings, int lineCount)
    {
        if (findings.Count == 0)
            return $"Redaction check: scanned {lineCount} lines — nothing that looks like a full address with a query, " +
                   "a credential, an email, or a token. Addresses in this log are host + path only, as designed.";
        var byCategory = findings
            .GroupBy(f => f.Category)
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => $"{g.Count()}× {DescribeCategory(g.Key)}");
        return $"Redaction check: scanned {lineCount} lines and found {findings.Count} thing(s) worth a look: " +
               string.Join("; ", byCategory) +
               ". Line numbers are listed below. “Copy redacted log” removes them before copying.";
    }

    // Long-token matching ignores plain host+path URLs (their runs are
    // broken by slashes anyway, but a long path segment is a page path,
    // not a token — flagging it would cry wolf on every chat URL).
    private static string StripBenignUrls(string line) =>
        Regex.Replace(line, @"https?://\S+", " ");
}

// ───────────────────────── connection info (S7 #69) ─────────────────────────

/// <summary>Plain-language connection facts for the padlock dialog (S7 #69), composed in Core so the wording is tested.</summary>
public static class ConnectionInfo
{
    /// <summary>The connection line: what the padlock means for the current page.</summary>
    public static string SecurityLine(string? url)
    {
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return "No page is loaded yet.";
        if (uri.Scheme == "https")
            return $"Secure connection (HTTPS) to {uri.Host}. MuseDesktop only loads HTTPS pages.";
        if (uri.Scheme == "http")
            return $"Not secure: this page loaded over plain HTTP from {uri.Host}. MuseDesktop's policy blocks " +
                   "HTTP top-level pages, so you should not be seeing this — please report it.";
        return $"This is a {uri.Scheme} page, not a web page MuseDesktop loaded from a server.";
    }

    /// <summary>What a certificate error means, in plain words (the cert dialog's opening).</summary>
    public const string CertificateErrorExplainer =
        "Windows could not verify the certificate for this site. That can mean an attack is intercepting " +
        "the connection, a misconfigured network (some public Wi-Fi and antivirus tools do this), or a " +
        "broken server certificate. Anything you type — including your Muse sign-in — could be readable " +
        "by someone else if you continue.";

    /// <summary>A plain name for a WebView2 certificate error status string.</summary>
    public static string DescribeCertificateError(string? webErrorStatus) => webErrorStatus switch
    {
        "CertificateExpired" => "The certificate has expired.",
        "CertificateCommonNameIsIncorrect" => "The certificate was issued for a different site name.",
        "CertificateRevoked" => "The certificate has been revoked by its issuer.",
        "CertificateIsInvalid" => "The certificate is not valid.",
        "CertificateContainsErrors" => "The certificate contains errors.",
        "CertificateAuthorityInvalid" => "The certificate's issuer is not trusted.",
        _ => "The certificate could not be verified.",
    };
}

// ───────────────────────── backup guide (S7 #70) ─────────────────────────

/// <summary>
/// The backup guide text (S7 #70), composed from the real folder paths
/// so the Settings dialog, and only it, shows this PC's actual places.
/// The moving-parts honesty: settings export covers preferences and
/// profile definitions; the sign-in itself lives in each profile's
/// WebView2 folder, whose cookies are encrypted by Windows for THIS
/// user on THIS PC.
/// </summary>
public static class BackupGuide
{
    public static string Compose(string settingsRoot, string webViewRoot)
    {
        return
            "What to back up, and what each piece covers:\n\n" +
            $"1) Your sign-ins (cookies and site data) live in each profile's WebView2 folder under:\n{webViewRoot}\n" +
            "Copying that whole folder backs up every profile's sign-in on this PC.\n\n" +
            $"2) Your settings (preferences, profile names, start pages) live in:\n{settingsRoot}\n" +
            "— or use Settings → Data → Export settings, which writes the same settings to one file " +
            "(there is also an encrypted-export option). Import brings them back on any PC.\n\n" +
            "Honest limit: Windows encrypts the sign-in cookies with your Windows user account " +
            "(DPAPI — the same protection Windows gives every app's secrets). A copied WebView2 folder " +
            "restored under the SAME Windows user on the SAME PC keeps you signed in; copied to a " +
            "different PC or a different Windows user, the cookies usually can't be read and Muse " +
            "will simply ask you to sign in again. Your chats are never at risk either way — they " +
            "live in your Muse account, not in these folders.\n\n" +
            "Downloads are ordinary files in your Downloads folder — back them up like any other files.";
    }
}
