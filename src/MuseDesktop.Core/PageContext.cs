using System.Text;
using System.Text.Json;

namespace MuseDesktop.Core;

// ─────────────────────── ask about this page (v2.1.0) ───────────────────────
//
// A page the user opened in the app (a link followed out of a
// conversation) becomes context for a new conversation: one click
// pulls the page's readable text across, and the shell drops it
// into a fresh conversation's composer as a labelled block, with
// the question box focused and nothing sent.
//
// Privacy shape (see DECISIONS, 2026-11-02): the text crosses from
// the page to the shell ONLY on that explicit click, through the
// same ExecuteScript channel conversation export already uses —
// never through the bridge, which carries no page content. It is
// never logged and never stored; it lives in the composer and
// nowhere else. This file owns the parts that can be judged
// without a page: which pages the command is offered on, how the
// extractor's answer is parsed, and the exact shape and size of
// the composed block.

/// <summary>One page's extraction (Ask about this page, v2.1.0): the page's own title and its readable text, exactly as the page script reported them — cleaning and capping are <see cref="PageContext.Compose"/>'s job, not the parser's.</summary>
public sealed record ExtractedPage(string Title, string Text);

/// <summary>The composed page-context block and its rules (v2.1.0).</summary>
public static class PageContext
{
    /// <summary>
    /// The most page text one block may carry. A page is context
    /// for a question, not an archive: 12,000 characters is a long
    /// article's worth, and anything past it is cut with a note in
    /// the block itself saying so — the reader (and Muse) can
    /// always tell the page was longer than what arrived.
    /// </summary>
    public const int MaxPageTextChars = 12_000;

    /// <summary>A page title longer than this is cut in the header — the header names the page, it doesn't quote it.</summary>
    public const int MaxTitleChars = 200;

    /// <summary>A host longer than this is cut in the header (real hosts are far shorter; this only tames junk).</summary>
    public const int MaxHostChars = 100;

    /// <summary>
    /// True when <paramref name="url"/> is a Muse conversation
    /// page — the one place Ask about this page is NOT offered:
    /// a conversation is already Muse's context, and pulling its
    /// text into another conversation is export's job, not this
    /// command's. Built on <see cref="AppRoute"/> — Core's existing
    /// route knowledge (path-only, the muse.ai host exactly, from
    /// workspaces/bookmarks/drafts) — with the one fact this
    /// feature adds: conversations live under "/chat". Anything
    /// that isn't a muse.ai page at all (an article on another
    /// host, a sign-in page) is not a conversation route.
    /// </summary>
    public static bool IsConversationRoute(string? url)
    {
        var route = AppRoute.FromUrl(url);
        return route is not null
            && (route.Equals("/chat", StringComparison.Ordinal)
                || route.StartsWith("/chat/", StringComparison.Ordinal));
    }

    /// <summary>
    /// Parse the page script's extraction answer (a JSON object
    /// with string "title" and "text" properties). Null for
    /// anything else — unparseable, not an object, no text
    /// property: the shell reads null as "nothing readable came
    /// back" and stops. The values are returned as reported;
    /// whitespace-only text still parses (Compose is what judges
    /// it unreadable).
    /// </summary>
    public static ExtractedPage? TryParseExtraction(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return null;
            var title = root.TryGetProperty("title", out var t) && t.ValueKind == JsonValueKind.String
                ? t.GetString() ?? ""
                : "";
            if (!root.TryGetProperty("text", out var x) || x.ValueKind != JsonValueKind.String)
                return null;
            return new ExtractedPage(title, x.GetString() ?? "");
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// Compose the block that lands in the new conversation's
    /// composer: the header line (the localized
    /// <paramref name="header"/> with {0} = cleaned title and
    /// {1} = cleaned host, or <paramref name="headerNoTitle"/>
    /// with {0} = host when the page had no usable title), a blank
    /// line, then the page text — line endings normalized, edges
    /// trimmed, capped at <see cref="MaxPageTextChars"/> — and,
    /// when the text was cut, a blank line and the localized
    /// <paramref name="cutNote"/> saying the rest was left out.
    /// The shell resolves the three strings from the catalog (the
    /// DraftMeter.Format split: Core composes, the catalog words).
    /// Returns null when the text is null, empty, or whitespace —
    /// the "nothing readable" result the shell detects and stops
    /// on.
    /// </summary>
    public static string? Compose(
        string? title, string? host, string? text,
        string header, string headerNoTitle, string cutNote)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;
        var body = text.Replace("\r\n", "\n").Replace('\r', '\n').Trim();
        if (body.Length == 0)
            return null;
        var cut = body.Length > MaxPageTextChars;
        if (cut)
            body = body[..MaxPageTextChars].TrimEnd();
        var cleanTitle = CleanTitle(title);
        var cleanHost = CleanHost(host);
        var head = cleanTitle.Length > 0
            ? string.Format(header, cleanTitle, cleanHost)
            : string.Format(headerNoTitle, cleanHost);
        var composed = new StringBuilder(head).Append("\n\n").Append(body);
        if (cut)
            composed.Append("\n\n").Append(cutNote);
        return composed.ToString();
    }

    /// <summary>A title for the header: every whitespace run (newlines included) collapses to one space, edges trimmed, capped — a page title is one line, however the page wrote it.</summary>
    public static string CleanTitle(string? title)
    {
        if (string.IsNullOrWhiteSpace(title))
            return "";
        var clean = string.Join(" ", title.Split((char[]?)null,
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        return clean.Length <= MaxTitleChars ? clean : clean[..MaxTitleChars].TrimEnd();
    }

    /// <summary>A host for the header: whitespace and control characters removed outright (a real host has none), capped.</summary>
    public static string CleanHost(string? host)
    {
        if (string.IsNullOrWhiteSpace(host))
            return "";
        var clean = new StringBuilder(host.Length);
        foreach (var c in host)
        {
            if (!char.IsWhiteSpace(c) && !char.IsControl(c))
                clean.Append(c);
        }
        var result = clean.ToString();
        return result.Length <= MaxHostChars ? result : result[..MaxHostChars];
    }
}
