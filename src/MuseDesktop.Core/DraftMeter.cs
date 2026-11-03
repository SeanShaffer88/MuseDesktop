using System.Globalization;
using System.Text;

namespace MuseDesktop.Core;

// ─────────────────────── draft meter + tidy paste (v2.1.0) ───────────────────────
//
// The composer's size, made visible, and a gentle way out of a wall
// of pasted text. The meter measures what the bridge's draft channel
// already delivers (the user's own unsent words, on this PC only)
// and shows a rough size in the status bar. A long paste is offered
// — never forced — a tidy form: the same text under a one-line
// header, quoted as a Markdown blockquote, so the conversation reads
// as the user's words ABOUT the paste instead of the paste itself
// wearing the message's clothes.
//
// Privacy shape (see DECISIONS, 2026-11-02): the page tells the
// shell only THAT a long paste happened and HOW LONG it was — a
// number. The pasted text itself reaches the shell only through the
// existing draft channel, exactly as it does for drafts autosave,
// and nothing here is logged.

/// <summary>How big a draft reads, in bands: nothing there, an ordinary message, long for one message, and very long.</summary>
public enum DraftBand
{
    Empty,
    Normal,
    Long,
    VeryLong,
}

/// <summary>One draft measurement: exact character and word counts, a rough token estimate, and the band those numbers land in.</summary>
public sealed record DraftMeasurement(int Characters, int Words, int RoughTokens, DraftBand Band);

/// <summary>
/// Measuring a draft (v2.1.0): characters as typed, words as
/// whitespace-separated runs, and a rough token estimate of
/// characters ÷ 4 rounded up — the usual rule of thumb for English
/// prose, offered as a guide, never a promise. The bands are about
/// how a message reads, not about any hard limit: Long starts above
/// 1,500 words or 8,000 characters; VeryLong above 4,000 words or
/// 20,000 characters (20,000 is also the draft store's cap, so a
/// VeryLong draft is one that fills the whole channel).
/// </summary>
public static class DraftMeter
{
    /// <summary>Words above which a draft counts as long for one message.</summary>
    public const int LongWordThreshold = 1_500;

    /// <summary>Characters above which a draft counts as long for one message.</summary>
    public const int LongCharacterThreshold = 8_000;

    /// <summary>Words above which a draft counts as very long.</summary>
    public const int VeryLongWordThreshold = 4_000;

    /// <summary>Characters above which a draft counts as very long.</summary>
    public const int VeryLongCharacterThreshold = 20_000;

    /// <summary>Measure a draft; null and whitespace-only text measure as <see cref="DraftBand.Empty"/> (a box with nothing in it has nothing to meter).</summary>
    public static DraftMeasurement Measure(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return new DraftMeasurement(0, 0, 0, DraftBand.Empty);
        var words = CountWords(text);
        if (words == 0)
            return new DraftMeasurement(text.Length, 0, 0, DraftBand.Empty);
        var band =
            text.Length > VeryLongCharacterThreshold || words > VeryLongWordThreshold
                ? DraftBand.VeryLong
                : text.Length > LongCharacterThreshold || words > LongWordThreshold
                    ? DraftBand.Long
                    : DraftBand.Normal;
        return new DraftMeasurement(
            Characters: text.Length,
            Words: words,
            RoughTokens: (text.Length + 3) / 4,
            Band: band);
    }

    /// <summary>
    /// The status-bar line for a measurement: the localized
    /// <paramref name="template"/> (with {0} = words and {1} = rough
    /// tokens, formatted in <paramref name="culture"/>) plus the
    /// band's hint for the long bands. An Empty measurement formats
    /// to "" — no draft, no meter line.
    /// </summary>
    public static string Format(
        DraftMeasurement measurement,
        string template,
        string? longHint,
        string? veryLongHint,
        CultureInfo? culture = null)
    {
        if (measurement.Band == DraftBand.Empty)
            return "";
        var text = string.Format(
            culture ?? CultureInfo.CurrentCulture, template, measurement.Words, measurement.RoughTokens);
        var hint = measurement.Band switch
        {
            DraftBand.Long => longHint,
            DraftBand.VeryLong => veryLongHint,
            _ => null,
        };
        return string.IsNullOrEmpty(hint) ? text : $"{text} {hint}";
    }

    /// <summary>Words in a text: runs of non-whitespace, the way a reader counts them.</summary>
    internal static int CountWords(string text)
    {
        var words = 0;
        var inWord = false;
        foreach (var c in text)
        {
            if (char.IsWhiteSpace(c))
            {
                inWord = false;
            }
            else if (!inWord)
            {
                inWord = true;
                words++;
            }
        }
        return words;
    }
}

/// <summary>
/// The tidy form of a long paste (v2.1.0): a first line
/// "Pasted text (N words):" — N the paste's word count, written as
/// plain digits so the header is recognisable in any culture — then
/// the text itself as a Markdown blockquote, every line prefixed
/// "> " (an empty line is just ">"), trailing blank lines trimmed.
/// Tidying is idempotent by construction: the header is how an
/// already-tidied draft is recognised, so a tidied paste is never
/// offered twice and never double-wrapped.
/// </summary>
public static class PasteTidier
{
    /// <summary>A draft longer than this (in characters) is a tidy candidate.</summary>
    public const int LongPasteThreshold = 2_000;

    private const string HeaderPrefix = "Pasted text (";
    private const string HeaderSuffix = " words):";

    /// <summary>True when a draft is long enough to offer tidying AND is not already in the tidied form.</summary>
    public static bool NeedsTidying(string? text) =>
        text is not null
        && text.Length > LongPasteThreshold
        && !IsTidied(text);

    /// <summary>True when the text already wears the tidied header as its first line.</summary>
    public static bool IsTidied(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return false;
        var firstLine = text;
        var newline = text.IndexOf('\n');
        if (newline >= 0)
            firstLine = text[..newline];
        firstLine = firstLine.TrimEnd('\r', ' ', '\t');
        if (!firstLine.StartsWith(HeaderPrefix, StringComparison.Ordinal)
            || !firstLine.EndsWith(HeaderSuffix, StringComparison.Ordinal))
            return false;
        var digits = firstLine[HeaderPrefix.Length..^HeaderSuffix.Length];
        return digits.Length > 0 && digits.All(char.IsDigit);
    }

    /// <summary>
    /// Tidy a text into the header + blockquote form. Null or
    /// whitespace-only input tidies to "" (there is nothing to
    /// quote). Works on any length — the threshold decides whether
    /// the shell OFFERS it, not whether it can be done.
    /// </summary>
    public static string Tidy(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return "";
        var lines = text.Replace("\r\n", "\n").Split('\n');
        var last = lines.Length - 1;
        while (last >= 0 && string.IsNullOrWhiteSpace(lines[last]))
            last--;
        if (last < 0)
            return "";
        var body = new StringBuilder();
        for (var i = 0; i <= last; i++)
        {
            if (body.Length > 0)
                body.Append('\n');
            var line = lines[i].TrimEnd('\r');
            body.Append(string.IsNullOrWhiteSpace(line) ? ">" : $"> {line}");
        }
        var words = DraftMeter.CountWords(string.Join("\n", lines, 0, last + 1));
        return $"{HeaderPrefix}{words.ToString(CultureInfo.InvariantCulture)}{HeaderSuffix}\n{body}";
    }
}
