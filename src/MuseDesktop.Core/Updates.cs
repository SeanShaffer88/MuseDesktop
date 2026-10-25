using System.Globalization;
using System.Text.Json;

namespace MuseDesktop.Core;

// ──────────────────────── release channels (S9 #88) ────────────────────────

/// <summary>
/// Which GitHub releases the update checker may offer (v0.13.0, S9 #88).
/// Stable is the default and the safe answer for anything unrecognized:
/// a stable user must NEVER be offered a pre-release.
/// </summary>
public enum UpdateChannel
{
    /// <summary>Stable releases only (GitHub's /latest semantics).</summary>
    Stable,

    /// <summary>Stable releases plus pre-releases, for users who opted in with the warning shown.</summary>
    IncludePrereleases,
}

/// <summary>How often the automatic (non-manual) update check runs (v0.13.0, S9 #83).</summary>
public enum UpdateCheckFrequency
{
    /// <summary>Once per launch — the behavior the app has always had.</summary>
    EveryLaunch,

    /// <summary>At most once a day.</summary>
    Daily,

    /// <summary>At most once a week.</summary>
    Weekly,

    /// <summary>Never automatically; the ⋯ menu's manual check always works.</summary>
    ManualOnly,
}

/// <summary>How long an update reminder is snoozed (v0.13.0, S9 #83).</summary>
public enum UpdateSnooze
{
    /// <summary>Remind me tomorrow (+1 day).</summary>
    Tomorrow,

    /// <summary>Remind me next week (+7 days).</summary>
    NextWeek,
}

/// <summary>One downloadable asset of a GitHub release (name + browser download URL + size).</summary>
public sealed record ReleaseAsset(string Name, string DownloadUrl, long Size);

/// <summary>
/// One GitHub release as the update checker needs it (v0.13.0, S9 #88):
/// tag, pre-release flag, page URL, and its assets. Parsed from the
/// releases LIST endpoint (the check can no longer use /latest, which
/// structurally hides pre-releases from the opt-in channel).
/// </summary>
public sealed record GitHubRelease(
    string Tag,
    bool IsPrerelease,
    string PageUrl,
    IReadOnlyList<ReleaseAsset> Assets)
{
    /// <summary>The release's assets by exact name (ordinal — asset names are exact).</summary>
    public ReleaseAsset? FindAsset(string name) =>
        Assets.FirstOrDefault(a => string.Equals(a.Name, name, StringComparison.Ordinal));
}

/// <summary>
/// Parsing GitHub's releases list and picking the release a given
/// channel may offer (v0.13.0, S9 #81/#83/#88). Pure logic; the HTTP
/// calls live in the shell's UpdateService.
/// </summary>
public static class ReleaseSelection
{
    /// <summary>The releases LIST endpoint — unlike /latest it includes pre-releases, which the channel filter then governs.</summary>
    public const string ReleasesApiUrl =
        "https://api.github.com/repos/SeanShaffer88/MuseDesktop/releases?per_page=20";

    /// <summary>
    /// Parse a releases-list JSON body. Tolerant by design (the caller
    /// treats any failure as "no update info"): unparseable bodies and
    /// entries without a tag drop out; missing asset fields degrade to
    /// empty values rather than failing the whole list.
    /// </summary>
    public static IReadOnlyList<GitHubRelease> ParseReleasesJson(string json)
    {
        var result = new List<GitHubRelease>();
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
                return result;
            foreach (var entry in doc.RootElement.EnumerateArray())
            {
                var tag = GetString(entry, "tag_name");
                if (string.IsNullOrWhiteSpace(tag))
                    continue;
                var assets = new List<ReleaseAsset>();
                if (entry.TryGetProperty("assets", out var assetsEl)
                    && assetsEl.ValueKind == JsonValueKind.Array)
                {
                    foreach (var a in assetsEl.EnumerateArray())
                    {
                        var name = GetString(a, "name");
                        var url = GetString(a, "browser_download_url");
                        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(url))
                            continue;
                        var size = a.TryGetProperty("size", out var s) && s.TryGetInt64(out var v) ? v : 0;
                        assets.Add(new ReleaseAsset(name, url, size));
                    }
                }
                result.Add(new GitHubRelease(
                    tag,
                    entry.TryGetProperty("prerelease", out var pre) && pre.ValueKind == JsonValueKind.True,
                    GetString(entry, "html_url") ?? "",
                    assets));
            }
        }
        catch
        {
            // Any malformed body reads as "no releases" — never an error.
        }
        return result;
    }

    private static string? GetString(JsonElement el, string name) =>
        el.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;

    /// <summary>
    /// The release to offer this user, or null when nothing on their
    /// channel is newer. Rules (all pinned by tests):
    /// - a candidate's base version must beat the running version —
    ///   updates only, never a downgrade;
    /// - Stable channel: only non-prerelease releases without a tag
    ///   suffix (belt-and-braces — the flag and the suffix must agree);
    /// - Pre-release channel: stable beats a pre-release at the SAME
    ///   base version, and a running pre-release is offered its own
    ///   base version's stable release (moving back to stable is not a
    ///   downgrade);
    /// - drafts never appear in the API response for this repo's
    ///   unauthenticated calls, and unparseable tags are skipped.
    /// </summary>
    public static GitHubRelease? SelectNewest(
        IEnumerable<GitHubRelease> releases, Version current,
        UpdateChannel channel, bool currentIsPrerelease = false)
    {
        var cur = Base(current);
        GitHubRelease? best = null;
        var bestBase = cur;
        var bestIsPre = false;
        foreach (var release in releases)
        {
            var parsed = UpdateInfo.ParseTag(release.Tag);
            if (parsed is null)
                continue;
            var isPre = release.IsPrerelease || UpdateInfo.HasSuffix(release.Tag);
            if (channel == UpdateChannel.Stable && isPre)
                continue;
            var baseVersion = Base(parsed);
            // The same-base stable release for a running pre-release is
            // an offer (channel switch-back), not a downgrade.
            var sameBaseStableForPreUser =
                currentIsPrerelease && !isPre && baseVersion == cur;
            if (baseVersion < cur || (baseVersion == cur && !sameBaseStableForPreUser))
                continue;
            if (best is null
                || baseVersion > bestBase
                || (baseVersion == bestBase && bestIsPre && !isPre))
            {
                best = release;
                bestBase = baseVersion;
                bestIsPre = isPre;
            }
        }
        return best;
    }

    private static Version Base(Version v) =>
        new(v.Major, Math.Max(0, v.Minor), Math.Max(0, v.Build));
}

/// <summary>
/// When the automatic update check may run (v0.13.0, S9 #83): the master
/// switch, the frequency since the last check, and the snooze all gate
/// it. A manual check bypasses this entirely — the user asked, so it
/// runs (subject only to the master switch living in the menu itself).
/// </summary>
public static class UpdateSchedule
{
    public static bool ShouldAutoCheck(
        DateTimeOffset now, bool updatesEnabled, UpdateCheckFrequency frequency,
        DateTimeOffset? lastCheckUtc, DateTimeOffset? snoozedUntilUtc)
    {
        if (!updatesEnabled)
            return false;
        if (snoozedUntilUtc is { } until && now < until)
            return false;
        return frequency switch
        {
            UpdateCheckFrequency.ManualOnly => false,
            UpdateCheckFrequency.Daily => lastCheckUtc is null || now - lastCheckUtc.Value >= TimeSpan.FromDays(1),
            UpdateCheckFrequency.Weekly => lastCheckUtc is null || now - lastCheckUtc.Value >= TimeSpan.FromDays(7),
            _ => true, // EveryLaunch (and anything undefined sanitized earlier)
        };
    }

    /// <summary>When a snooze choice made at <paramref name="now"/> expires.</summary>
    public static DateTimeOffset SnoozeUntil(DateTimeOffset now, UpdateSnooze choice) =>
        now + (choice == UpdateSnooze.NextWeek ? TimeSpan.FromDays(7) : TimeSpan.FromDays(1));

    /// <summary>"Snoozed until Tue, Oct 28" for the Settings note and the update window.</summary>
    public static string DescribeSnooze(DateTimeOffset until) =>
        "Update reminders are snoozed until " + until.LocalDateTime.ToString("ddd, MMM d", CultureInfo.CurrentCulture) + ".";
}

// ──────────────────────── SHA256SUMS verification (S9 #81) ────────────────────────

/// <summary>
/// The release's SHA256SUMS.txt (v0.13.0, S9 #81): sha256sum-format
/// lines, "&lt;64 hex&gt;  &lt;file name&gt;". The updater refuses to
/// run an installer whose hash is not listed or does not match — the
/// sums file is what turns "downloaded from GitHub" into "the exact
/// bytes the release published".
/// </summary>
public static class SumsFile
{
    /// <summary>The asset name our releases publish the sums under.</summary>
    public const string AssetName = "SHA256SUMS.txt";

    /// <summary>Parse sums text into name → lowercase hex hash. Junk lines are skipped.</summary>
    public static IReadOnlyDictionary<string, string> Parse(string text)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length < 67)
                continue;
            var hash = line[..64];
            if (!IsSha256Hex(hash))
                continue;
            var rest = line[64..].TrimStart(' ', '\t', '*');
            if (rest.Length == 0 || rest.Contains(' ') || rest.Contains('\t'))
                continue; // a file name with spaces isn't an asset we ship; junk line
            map[rest] = hash.ToLowerInvariant();
        }
        return map;
    }

    /// <summary>The expected hash for an asset name, or null when the sums don't list it.</summary>
    public static string? ExpectedHash(string sumsText, string assetName) =>
        Parse(sumsText).TryGetValue(assetName, out var hash) ? hash : null;

    /// <summary>Exact hash comparison (either case accepted on input; content compared normalized).</summary>
    public static bool HashesMatch(string? expectedHex, string? actualHex)
    {
        if (!IsSha256Hex(expectedHex) || !IsSha256Hex(actualHex))
            return false;
        return string.Equals(expectedHex, actualHex, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>True for exactly 64 hex characters.</summary>
    public static bool IsSha256Hex(string? value) =>
        value is { Length: 64 } && value.All(Uri.IsHexDigit);
}

// ──────────────────────── installer assets + rollback (S9 #81/#84) ────────────────────────

/// <summary>
/// Installer asset naming + the updater's kept-installer bookkeeping
/// (v0.13.0, S9 #81/#84). Asset names are the release contract:
/// <c>MuseDesktop-Setup-v&lt;version&gt;-&lt;arch&gt;.exe</c>.
/// </summary>
public static class UpdateAssets
{
    /// <summary>The process architecture token used in asset names ("x64" / "arm64"); other values pass through lowercased.</summary>
    public static string ArchToken(string processArchitecture) =>
        processArchitecture.Trim().ToLowerInvariant() switch
        {
            "x64" => "x64",
            "arm64" => "arm64",
            var other => other,
        };

    /// <summary>The installer asset name for a version ("0.13.0" or "v0.13.0") and architecture token.</summary>
    public static string InstallerAssetName(string version, string archToken)
    {
        var v = version.Trim().TrimStart('v', 'V');
        return $"MuseDesktop-Setup-v{v}-{archToken}.exe";
    }

    /// <summary>The installer asset for this architecture in a release, or null when the release doesn't carry one.</summary>
    public static ReleaseAsset? PickInstaller(GitHubRelease release, string archToken)
    {
        var tagVersion = UpdateInfo.ParseTag(release.Tag);
        if (tagVersion is null)
            return null;
        return release.FindAsset(InstallerAssetName(tagVersion.ToString(3), archToken));
    }

    /// <summary>True when a file name is an installer asset for the given architecture token (the rollback offer checks this — a kept installer from another architecture must never be offered).</summary>
    public static bool IsInstallerForArch(string? fileName, string archToken) =>
        fileName is not null
        && fileName.EndsWith($"-{archToken}.exe", StringComparison.OrdinalIgnoreCase)
        && fileName.StartsWith("MuseDesktop-Setup-v", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// The rollback record kept in settings (v0.13.0, S9 #84): which
/// installer's file the updater kept as the "previous version", and
/// when. The file itself lives in the updates folder's previous slot —
/// see <see cref="InstallerKeep"/>.
/// </summary>
public sealed record PreviousInstallerInfo
{
    /// <summary>The version the kept installer installs, tag form ("0.12.0").</summary>
    public string Version { get; set; } = "";

    /// <summary>The kept installer's file name inside the updates folder's previous slot.</summary>
    public string FileName { get; set; } = "";

    /// <summary>When the updater kept it.</summary>
    public DateTimeOffset KeptUtc { get; set; }

    /// <summary>A record is usable only with a real version and file name; anything else sanitizes away.</summary>
    public bool IsWellFormed =>
        UpdateInfo.ParseTag(Version) is not null
        && !string.IsNullOrWhiteSpace(FileName)
        && Path.GetFileName(FileName) == FileName;
}

/// <summary>
/// Which kept installer, if any, may be offered as a rollback
/// (v0.13.0, S9 #84). Honest coverage: a rollback exists only when the
/// updater itself kept an installer — versions installed from the
/// website or a zip before the updater existed have nothing to roll
/// back TO from inside the app. The file must still exist and match
/// this PC's architecture.
/// </summary>
public static class RollbackOffer
{
    /// <summary>The full path to launch for a rollback, or null when none may be offered.</summary>
    public static string? UsableInstallerPath(
        PreviousInstallerInfo? record, string updatesDir, string archToken,
        Func<string, bool> fileExists)
    {
        if (record is null || !record.IsWellFormed)
            return null;
        if (!UpdateAssets.IsInstallerForArch(record.FileName, archToken))
            return null;
        var path = Path.Combine(updatesDir, InstallerKeep.PreviousSlot, record.FileName);
        return fileExists(path) ? path : null;
    }

    /// <summary>The Diagnostics/About line for an offered rollback.</summary>
    public static string Describe(PreviousInstallerInfo record) =>
        $"Reinstall previous version ({record.Version})";
}

/// <summary>
/// The updater's two installer slots under the updates folder
/// (v0.13.0, S9 #84): <c>current</c> holds the installer of the newest
/// version the in-app updater downloaded (so the NEXT update can make
/// it the rollback), and <c>previous</c> holds the one rollback target.
/// Exactly one of each — an update rotates current → previous and the
/// old previous is replaced, never accumulated.
/// </summary>
public static class InstallerKeep
{
    public const string CurrentSlot = "current";
    public const string PreviousSlot = "previous";

    /// <summary>
    /// Decide the rotation when a verified <paramref name="newVersion"/>
    /// installer is about to be launched: the kept current installer of
    /// a DIFFERENT version becomes the rollback; the new installer
    /// becomes the kept current. Reinstalling the same version rotates
    /// nothing.
    /// </summary>
    public static (bool RotateCurrentToPrevious, PreviousInstallerInfo? NewPrevious) PlanRotation(
        string? keptCurrentVersion, string? keptCurrentFileName,
        string newVersion, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(keptCurrentVersion)
            || string.IsNullOrWhiteSpace(keptCurrentFileName)
            || UpdateInfo.ParseTag(keptCurrentVersion) is null
            || string.Equals(keptCurrentVersion, newVersion, StringComparison.OrdinalIgnoreCase))
            return (false, null);
        var previous = new PreviousInstallerInfo
        {
            Version = keptCurrentVersion,
            FileName = keptCurrentFileName,
            KeptUtc = now,
        };
        return previous.IsWellFormed ? (true, previous) : (false, null);
    }
}

// ──────────────────────── update download status (S9 #81) ────────────────────────

/// <summary>
/// The in-app update download's progress line (v0.13.0, S9 #81) —
/// percent + ETA in the house style (DownloadText), built from the
/// same EWMA sampler page downloads use. Indeterminate when the
/// server gives no total.
/// </summary>
public static class UpdateDownloadStatus
{
    public static string Format(long bytesReceived, long? totalBytes, double speedBytesPerSecond)
    {
        if (totalBytes is { } total && total > 0)
        {
            var pct = (int)Math.Clamp(bytesReceived * 100L / total, 0, 100);
            var line = $"Downloading update — {pct}% ({DownloadText.FormatBytes(bytesReceived)} of {DownloadText.FormatBytes(total)})";
            if (speedBytesPerSecond > 0)
            {
                line += $" · {DownloadText.FormatSpeed(speedBytesPerSecond)}";
                var remaining = total - bytesReceived;
                if (remaining > 0)
                    line += $", about {DownloadText.FormatEta(TimeSpan.FromSeconds(remaining / speedBytesPerSecond))} left";
            }
            return line;
        }
        return $"Downloading update — {DownloadText.FormatBytes(bytesReceived)} received";
    }
}

// ──────────────────────── What's New (S9 #82) ────────────────────────

/// <summary>What the What's New check decides at launch (v0.13.0, S9 #82).</summary>
public enum WhatsNewDecision
{
    /// <summary>Show the running version's notes.</summary>
    Show,

    /// <summary>Fresh install: record the version, show nothing — there is no "new" yet for this user.</summary>
    RecordOnly,

    /// <summary>Already seen (or the user turned What's New off): nothing to do.</summary>
    Skip,
}

/// <summary>
/// What's New (v0.13.0, S9 #82): extract one version's section from
/// changelog-shaped markdown, and decide whether a launch shows it.
/// The shell ships the text as a WhatsNew.md content file beside the
/// exe, regenerated from CHANGELOG.md at release time (a test pins
/// the two together so the copy can't silently drift).
/// </summary>
public static class WhatsNew
{
    /// <summary>
    /// Decide from the persisted last-seen version: nothing recorded =
    /// fresh install (record only); a different version recorded =
    /// this launch is the first on a new version (show); the same =
    /// seen. The opt-out wins over everything.
    /// </summary>
    public static WhatsNewDecision Decide(string? lastSeenVersion, string currentVersion, bool showEnabled)
    {
        if (!showEnabled)
            return WhatsNewDecision.Skip;
        if (string.IsNullOrWhiteSpace(lastSeenVersion))
            return WhatsNewDecision.RecordOnly;
        return string.Equals(lastSeenVersion.Trim(), currentVersion.Trim(), StringComparison.OrdinalIgnoreCase)
            ? WhatsNewDecision.Skip
            : WhatsNewDecision.Show;
    }

    /// <summary>
    /// The markdown body of the <c>## [x.y.z]</c> section for
    /// <paramref name="version"/> ("0.13.0" or "v0.13.0"), or null when
    /// the document has no such section. The heading line itself is
    /// not part of the body.
    /// </summary>
    public static string? ExtractSection(string markdown, string version)
    {
        var wanted = version.Trim().TrimStart('v', 'V');
        var lines = markdown.Replace("\r\n", "\n").Split('\n');
        var start = -1;
        for (var i = 0; i < lines.Length; i++)
        {
            if (!lines[i].StartsWith("## ", StringComparison.Ordinal))
                continue;
            if (start >= 0)
                return JoinBody(lines, start, i);
            var heading = lines[i][3..].Trim();
            // Heading shapes in the wild: "[0.13.0] — Unreleased",
            // "[0.13.0] — 2026-11-01", "0.13.0". Compare the bracketed
            // or leading token only.
            var token = heading.TrimStart('[');
            var end = token.IndexOfAny(new[] { ']', ' ', '\t' });
            if (end >= 0)
                token = token[..end];
            if (string.Equals(token, wanted, StringComparison.OrdinalIgnoreCase))
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
