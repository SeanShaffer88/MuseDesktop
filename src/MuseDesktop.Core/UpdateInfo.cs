using System.Text.Json;

namespace MuseDesktop.Core;

/// <summary>
/// Update-check logic (v0.2.0): parse GitHub's "latest release" response and
/// decide whether it is newer than the running build. Pure logic so the
/// comparison rules are unit-testable; the HTTP call lives in the shell.
/// </summary>
public static class UpdateInfo
{
    /// <summary>The repo whose Releases are the update channel.</summary>
    public const string LatestReleaseApiUrl =
        "https://api.github.com/repos/SeanShaffer88/MuseDesktop/releases/latest";

    /// <summary>Parse a release tag ("v0.2.0", "0.2.0") into a Version. Returns null for anything unparseable (incl. pre-release suffixes we don't compare).</summary>
    public static Version? ParseTag(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag))
            return null;
        var t = tag.Trim();
        if (t.StartsWith('v') || t.StartsWith('V'))
            t = t[1..];
        // Split off any pre-release/build suffix: "0.2.0-beta" compares as 0.2.0's
        // family, but we treat suffixed tags as not-newer (handled by callers via
        // IsNewer returning false when the tag carried a suffix).
        var dash = t.IndexOf('-');
        var plus = t.IndexOf('+');
        var cut = dash < 0 ? plus : (plus < 0 ? dash : Math.Min(dash, plus));
        if (cut >= 0)
            t = t[..cut];
        return Version.TryParse(t, out var v) ? v : null;
    }

    /// <summary>True when the tag carries a pre-release/build suffix.</summary>
    public static bool HasSuffix(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag))
            return false;
        var t = tag.Trim().TrimStart('v', 'V');
        return t.Contains('-') || t.Contains('+');
    }

    /// <summary>
    /// Is <paramref name="latestTag"/> a stable release newer than
    /// <paramref name="current"/>? Unparseable or pre-release tags are never
    /// "newer" — a public user should not be nudged toward a beta by the
    /// stable channel (GitHub's /latest endpoint already excludes
    /// pre-releases; this is belt-and-braces).
    /// </summary>
    public static bool IsNewer(string? latestTag, Version current)
    {
        if (HasSuffix(latestTag))
            return false;
        var latest = ParseTag(latestTag);
        if (latest is null)
            return false;
        // Compare on major.minor.build only; Version revision quirks
        // (e.g. 0.2.0 vs 0.2.0.0) should read as equal.
        var cur = new Version(current.Major, Math.Max(0, current.Minor), Math.Max(0, current.Build));
        var lat = new Version(latest.Major, Math.Max(0, latest.Minor), Math.Max(0, latest.Build));
        return lat > cur;
    }

    /// <summary>
    /// Extract (tag, release page URL) from a GitHub releases/latest JSON
    /// body. Returns null when the shape isn't what we expect — the caller
    /// treats any failure as "no update info", never as an error.
    /// </summary>
    public static (string Tag, string Url)? ParseLatestReleaseJson(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var tag = root.GetProperty("tag_name").GetString();
            var url = root.GetProperty("html_url").GetString();
            if (string.IsNullOrWhiteSpace(tag) || string.IsNullOrWhiteSpace(url))
                return null;
            return (tag, url);
        }
        catch
        {
            return null;
        }
    }
}
