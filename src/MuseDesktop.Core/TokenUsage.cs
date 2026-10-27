using System.Globalization;
using System.Text.Json;

namespace MuseDesktop.Core;

// ───────────────────── token usage ring (R7B, v1.8.2) ─────────────────────
//
// SEAN directed (2026-10-27) a ring in the main window's upper-right
// cluster showing the current conversation's token usage. The shell
// has no API for Muse's token counts — the hosted page is the only
// possible source, and it may not expose a figure at all. The honesty
// rule for this feature is therefore structural, not cosmetic:
//
//   1. If the page itself reports a usage figure (observed through
//      the R5B bridge's probe — observe-only, like everything else
//      the bridge does), the ring shows THAT, labeled page-reported.
//   2. Otherwise the ring shows a local ESTIMATE from the length of
//      the conversation text the bridge can already see, labeled an
//      estimate everywhere it appears (ring tooltip, panel, ~ mark).
//
// An estimate is never presented as an exact count, and a reading
// whose source is unknown is never produced: Resolve returns null
// when the probe itself failed, which the shell renders as "no
// reading yet" rather than a made-up zero-with-confidence.

/// <summary>Where a token-usage reading came from (R7B). The label is part of the data: the UI must be able to say, for every number it shows, whether the page reported it or the app estimated it.</summary>
public enum TokenUsageSource
{
    /// <summary>The hosted Muse page displayed the figure; the bridge only read it.</summary>
    PageReported,

    /// <summary>MuseDesktop estimated the figure from the conversation's text length (see <see cref="TokenEstimator"/>). Rough by definition.</summary>
    Estimated,
}

/// <summary>
/// One token-usage reading (R7B). <see cref="Percent"/> is always
/// clamped to 0–100 (an over-limit conversation reads as a full
/// ring, not 137%). Counts are nullable because a page may report
/// only a percent — in that case the percent is the page's own and
/// no used/limit pair is invented for it.
/// </summary>
public sealed record TokenUsage(int? UsedTokens, int? LimitTokens, double Percent, TokenUsageSource Source)
{
    /// <summary>True when this reading is the local estimate — every surface must mark it as one.</summary>
    public bool IsEstimate => Source == TokenUsageSource.Estimated;

    /// <summary>The percent as a whole number for compact displays (the ring's center text).</summary>
    public int PercentRounded => (int)Math.Round(Percent);

    /// <summary>Build a reading from a used/limit pair; the percent is computed and clamped. Negative counts are refused (treated as no data) rather than clamped into a plausible-looking lie — callers sanitize page data through the probe parser, which already drops negatives.</summary>
    public static TokenUsage FromCounts(int usedTokens, int limitTokens, TokenUsageSource source)
    {
        var percent = limitTokens > 0
            ? Math.Clamp(100.0 * usedTokens / limitTokens, 0.0, 100.0)
            : 0.0;
        return new TokenUsage(usedTokens, limitTokens, percent, source);
    }

    /// <summary>Build a reading from a percent alone (a page that shows "62% of context used" but no counts). The percent clamps to 0–100; counts stay unknown.</summary>
    public static TokenUsage FromPercent(double percent, TokenUsageSource source) =>
        new(null, null, Math.Clamp(percent, 0.0, 100.0), source);

    /// <summary>
    /// Decide the reading from one probe result. Preference order:
    /// the page's own counts, then the page's own percent, then the
    /// local estimate against the user's estimated limit. A null
    /// probe (the script failed or isn't there) yields null — no
    /// reading — never a fabricated one.
    /// </summary>
    public static TokenUsage? Resolve(TokenUsageProbe? probe, int estimatedLimitTokens)
    {
        if (probe is null)
            return null;
        if (probe.ReportedUsedTokens is { } used && probe.ReportedLimitTokens is { } limit && limit > 0)
            return FromCounts(used, limit, TokenUsageSource.PageReported);
        if (probe.ReportedPercent is { } percent)
            return FromPercent(percent, TokenUsageSource.PageReported);
        var estimatedUsed = TokenEstimator.EstimateTokens(probe.ConversationTextLength);
        return FromCounts(estimatedUsed, TokenEstimator.SanitizeLimit(estimatedLimitTokens),
            TokenUsageSource.Estimated);
    }
}

/// <summary>
/// What one probe of the hosted page found (R7B): the usage figure
/// the page itself displays, if any, plus the conversation's text
/// length in characters (the estimate's only input — the text itself
/// never crosses the bridge for this feature). Parsed strictly like
/// every bridge payload: the page is untrusted input, negative or
/// absurd values are dropped to null, and garbage yields null.
/// </summary>
public sealed record TokenUsageProbe(
    int? ReportedUsedTokens, int? ReportedLimitTokens, double? ReportedPercent, int ConversationTextLength)
{
    /// <summary>The largest plausible context window the parser will believe from a page figure (10M tokens); a bigger "limit" is a parsing accident, not data.</summary>
    public const int MaxPlausibleTokens = 10_000_000;

    public static TokenUsageProbe? TryParse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return null;

            int? Count(string name)
            {
                if (!root.TryGetProperty(name, out var el) || el.ValueKind != JsonValueKind.Number)
                    return null;
                if (!el.TryGetInt64(out var value) || value < 0 || value > MaxPlausibleTokens)
                    return null;
                return (int)value;
            }

            double? Percent()
            {
                if (!root.TryGetProperty("percent", out var el) || el.ValueKind != JsonValueKind.Number)
                    return null;
                var value = el.GetDouble();
                return value is >= 0 and <= 1000 ? value : null;
            }

            var textLength = 0;
            if (root.TryGetProperty("textLength", out var lenEl) && lenEl.ValueKind == JsonValueKind.Number
                && lenEl.TryGetInt64(out var len) && len > 0)
                textLength = (int)Math.Min(len, int.MaxValue);

            return new TokenUsageProbe(Count("used"), Count("limit"), Percent(), textLength);
        }
        catch (Exception)
        {
            return null;
        }
    }
}

/// <summary>
/// The local estimator (R7B): conversation characters → tokens at a
/// rough characters-per-token ratio. The ratio is the well-known
/// rule of thumb for English prose (~4 characters per token); code,
/// other languages, and markup all move the real number, which is
/// exactly why every surface labels this path an estimate. The
/// estimate is a guide for "how full is this conversation", never a
/// billing figure.
/// </summary>
public static class TokenEstimator
{
    /// <summary>Characters per token in the estimate's rule of thumb.</summary>
    public const double CharsPerToken = 4.0;

    /// <summary>The default estimated context limit (tokens) — the common large context window; the user can change it in Settings because only they know their plan's real window.</summary>
    public const int DefaultLimitTokens = 200_000;

    /// <summary>The smallest limit the setting accepts (a smaller "context window" is a typo, not a plan).</summary>
    public const int MinLimitTokens = 1_000;

    /// <summary>The largest limit the setting accepts.</summary>
    public const int MaxLimitTokens = 10_000_000;

    /// <summary>Estimate tokens from a character count: ceiling division at the rule-of-thumb ratio, so any text at all estimates to at least one token and empty text to zero.</summary>
    public static int EstimateTokens(int textLength) =>
        textLength <= 0 ? 0 : (int)Math.Ceiling(textLength / CharsPerToken);

    /// <summary>Repair a stored limit: unset/non-positive returns the default; anything else clamps into the offered range.</summary>
    public static int SanitizeLimit(int value) =>
        value <= 0 ? DefaultLimitTokens : Math.Clamp(value, MinLimitTokens, MaxLimitTokens);
}

/// <summary>Display formatting for token counts (R7B): thousands separators, always — a raw 7-digit number is how misreadings happen.</summary>
public static class TokenUsageText
{
    public static string FormatCount(int value) => value.ToString("N0", CultureInfo.InvariantCulture);

    /// <summary>The ring's compact percent label: "~42%" for an estimate (the tilde is the honesty mark, present even at a glance), "42%" for a page-reported reading.</summary>
    public static string FormatRingPercent(TokenUsage usage) =>
        usage.IsEstimate ? $"~{usage.PercentRounded}%" : $"{usage.PercentRounded}%";
}

/// <summary>
/// The ring's geometry (R7B): where a percent lands on a circle
/// drawn clockwise from the top — the progress-ring convention. Pure
/// math so the shell's path building is a transcription, not a
/// decision. Center/radius are the caller's (view metrics).
/// </summary>
public static class TokenRingGeometry
{
    public static (double X, double Y) Point(double percent, double center, double radius)
    {
        var clamped = Math.Clamp(percent, 0.0, 100.0);
        var angle = (clamped / 100.0 * 360.0 - 90.0) * Math.PI / 180.0;
        return (center + radius * Math.Cos(angle), center + radius * Math.Sin(angle));
    }

    /// <summary>Whether the arc from the top to <paramref name="percent"/> sweeps more than a half circle (the arc segment's large-arc flag).</summary>
    public static bool IsLargeArc(double percent) => Math.Clamp(percent, 0.0, 100.0) > 50.0;
}
