using System.Text;
using System.Text.Json;

namespace MuseDesktop.Core;

// ───────────────────── conversation tools (R5B #201–#206) ─────────────────────
//
// The follow-on six, approved by SEAN 2026-10-25 (addendum). These
// are the first features that touch the hosted page at all — a scoped,
// user-requested exception to the standing "never restyle muse.ai"
// rule, recorded in DECISIONS.md: they restyle or observe ONLY at the
// user's explicit request (scrollbar visibility, reading comfort, a
// bookmark button, draft capture), they never act inside the page on
// the user's behalf, and every one of them degrades to a quiet no-op
// when the page's structure isn't what the build expects. Everything
// here stays on this PC: bookmarks, drafts, and snippets live in
// their own small files beside the settings (the reading-list /
// notes discipline), hold app ROUTES never full page addresses, and
// are never exported, linked, or shared.

// ─────────────────────── reading comfort + page CSS (#201/#205) ───────────────────────

/// <summary>
/// The reading-comfort ranges (R5B #205) and the page CSS the shell
/// injects (#201/#205). The values live ON the profile (an int percent,
/// a pixel width, an int line-spacing percent; 0 = "the page's own
/// default" for width/spacing), and this class is the one place that
/// clamps them — Settings, load repair, and the CSS builder all read
/// the same numbers, so a hand-edited settings file can ask for a
/// 40-pixel column and get the floor instead.
/// </summary>
public static class ReadingComfort
{
    /// <summary>Chat text scale bounds (#205): 50%–200% of the page's own size; 100 is the page's size.</summary>
    public const int MinTextScalePercent = 50;
    public const int MaxTextScalePercent = 200;
    public const int DefaultTextScalePercent = 100;

    /// <summary>Reading-column bounds (#205): 0 = the page's own width; otherwise 480–1600 px.</summary>
    public const int MinColumnWidthPx = 480;
    public const int MaxColumnWidthPx = 1600;
    public const int DefaultColumnWidthPx = 0;

    /// <summary>Line-spacing bounds (#205): 0 = the page's own spacing; otherwise 100%–200%.</summary>
    public const int MinLineSpacingPercent = 100;
    public const int MaxLineSpacingPercent = 200;
    public const int DefaultLineSpacingPercent = 0;

    public static int ClampTextScale(int percent) =>
        Math.Clamp(percent, MinTextScalePercent, MaxTextScalePercent);

    /// <summary>0 stays 0 (page default); any other value clamps into the offered range.</summary>
    public static int ClampColumnWidth(int px) =>
        px <= 0 ? DefaultColumnWidthPx : Math.Clamp(px, MinColumnWidthPx, MaxColumnWidthPx);

    /// <summary>0 stays 0 (page default); any other value clamps into the offered range.</summary>
    public static int ClampLineSpacing(int percent) =>
        percent <= 0 ? DefaultLineSpacingPercent : Math.Clamp(percent, MinLineSpacingPercent, MaxLineSpacingPercent);

    /// <summary>True when the profile's comfort values are all at the page's defaults (nothing to inject for #205).</summary>
    public static bool IsDefault(int textScalePercent, int columnWidthPx, int lineSpacingPercent) =>
        ClampTextScale(textScalePercent) == DefaultTextScalePercent
        && ClampColumnWidth(columnWidthPx) == DefaultColumnWidthPx
        && ClampLineSpacing(lineSpacingPercent) == DefaultLineSpacingPercent;
}

/// <summary>
/// The CSS the shell injects into the conversation view (R5B
/// #201/#205), built from the active profile so the tests can pin the
/// exact rules. Two independent halves: the always-visible scrollbar
/// (#201 — the hosted page's overlay scrollbars hide until scrolled
/// and are a few pixels wide; these rules keep a 12px bar with a
/// grabbable thumb on the window scroller AND any inner scroller,
/// which is where the conversation actually scrolls) and the reading
/// comfort rules (#205 — a font-size scale on the page root, a
/// max-width on the conversation column, a line-height on message
/// text). Empty halves are omitted; an all-default profile with the
/// scrollbar toggle off yields an empty string and the shell injects
/// nothing. The selectors are deliberately structural, not
/// class-name-based: muse.ai's class names are build artifacts that
/// change without notice, while `main` and the message testids are
/// the page's stable shape — and if even those move, the rules match
/// nothing and the page renders exactly as Muse shipped it.
/// </summary>
public static class PageStyleCss
{
    /// <summary>The style element id the injected script maintains — one element, replaced per apply, never stacked.</summary>
    public const string StyleElementId = "musedesktop-page-style";

    public static string Build(AccountProfile? profile)
    {
        if (profile is null)
            return "";
        var sb = new StringBuilder();

        // #201: the visible, grabbable scrollbar (on by default).
        if (profile.ConversationScrollbars)
        {
            sb.Append("""
                html { scrollbar-width: auto !important; }
                ::-webkit-scrollbar { width: 12px !important; height: 12px !important; }
                ::-webkit-scrollbar-track { background: rgba(127, 127, 127, 0.12) !important; }
                ::-webkit-scrollbar-thumb { background: rgba(127, 127, 127, 0.55) !important; border-radius: 6px !important; border: 2px solid transparent !important; background-clip: padding-box !important; }
                ::-webkit-scrollbar-thumb:hover { background: rgba(127, 127, 127, 0.8) !important; background-clip: padding-box !important; }

                """);
        }

        // #205: reading comfort — only the rules the user moved off default.
        var scale = ReadingComfort.ClampTextScale(profile.ChatTextScalePercent);
        if (scale != ReadingComfort.DefaultTextScalePercent)
            sb.Append($"html {{ font-size: {scale}% !important; }}\n");
        var column = ReadingComfort.ClampColumnWidth(profile.ReadingColumnWidthPx);
        if (column != ReadingComfort.DefaultColumnWidthPx)
            sb.Append($"main [data-testid=\"conversation\"], main .conversation, main form {{ max-width: {column}px !important; margin-left: auto !important; margin-right: auto !important; }}\n");
        var spacing = ReadingComfort.ClampLineSpacing(profile.ChatLineSpacingPercent);
        if (spacing != ReadingComfort.DefaultLineSpacingPercent)
            sb.Append($"main [data-testid*=\"message\"], main article {{ line-height: {spacing / 100.0:0.00} !important; }}\n");

        return sb.ToString().TrimEnd();
    }
}

// ─────────────────────── the page bridge protocol ───────────────────────

/// <summary>What the injected page script can tell the shell (R5B). Deliberately tiny: a bookmark request (a message's text, nothing else), composer draft text, "the draft was sent", the ready handshake that asks the shell to push the current page style + any saved draft for this route, and (R7C) the two approval-card transitions — a card appeared / the card went away, carrying no card content at all.</summary>
public enum PageBridgeKind
{
    Ready,
    Bookmark,
    Draft,
    DraftSent,
    ApprovalAppeared,
    ApprovalGone,
}

/// <summary>One parsed page-bridge message (R5B). <see cref="Text"/> is the message snippet (bookmark) or the composer text (draft) — capped at parse time, and never logged by anyone.</summary>
public sealed record PageBridgeMessage(PageBridgeKind Kind, string Text);

/// <summary>
/// The parser for the page bridge's JSON (R5B). The page is a live
/// third-party document: anything it posts is UNTRUSTED input, so the
/// parser is strict about shape (an object with a known string
/// <c>type</c>), caps every text field, and returns null — never an
/// exception — for anything else. The shell treats null as "ignore",
/// so a page change or a stray script can at worst make the features
/// quietly stop, never fault the app.
/// </summary>
public static class PageBridge
{
    /// <summary>The longest snippet the bookmark flow will carry — enough text to re-find a message, never the whole conversation.</summary>
    public const int MaxSnippetLength = 600;

    /// <summary>The longest draft the bridge will carry (mirrors the draft store's cap).</summary>
    public const int MaxDraftLength = ConversationDrafts.MaxTextLength;

    public static PageBridgeMessage? TryParse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return null;
            if (!root.TryGetProperty("type", out var typeEl) || typeEl.ValueKind != JsonValueKind.String)
                return null;
            var text = "";
            if (root.TryGetProperty("text", out var textEl) && textEl.ValueKind == JsonValueKind.String)
                text = textEl.GetString() ?? "";
            switch (typeEl.GetString())
            {
                case "md-ready":
                    return new PageBridgeMessage(PageBridgeKind.Ready, "");
                case "md-bookmark":
                    if (text.Length > MaxSnippetLength)
                        text = text[..MaxSnippetLength];
                    return new PageBridgeMessage(PageBridgeKind.Bookmark, text);
                case "md-draft":
                    if (text.Length > MaxDraftLength)
                        text = text[..MaxDraftLength];
                    return new PageBridgeMessage(PageBridgeKind.Draft, text);
                case "md-draft-sent":
                    return new PageBridgeMessage(PageBridgeKind.DraftSent, "");
                case "md-approval-appeared":
                    return new PageBridgeMessage(PageBridgeKind.ApprovalAppeared, "");
                case "md-approval-gone":
                    return new PageBridgeMessage(PageBridgeKind.ApprovalGone, "");
                default:
                    return null;
            }
        }
        catch (Exception)
        {
            return null;
        }
    }
}

// ─────────────────────── message bookmarks (#202) ───────────────────────

/// <summary>
/// One message bookmark (R5B #202, SEAN's design): a NAMED
/// placeholder for an individual message. It holds the nickname the
/// user had to give it (there are no unnamed bookmarks — validation
/// refuses them at save and load repair drops them), the
/// conversation's title and app ROUTE (path only, like every stored
/// reference in this app), and a snippet of the message's text — the
/// snippet is how a jump re-finds the message after the page reloads,
/// and it is stored for that purpose only. Bookmarks live in the
/// app's local data per profile; they are never exported, linked, or
/// shared — there is deliberately no code path that takes one out of
/// bookmarks.json.
/// </summary>
public sealed class MessageBookmark
{
    public string Id { get; set; } = "";
    public string Nickname { get; set; } = "";
    public string ConversationTitle { get; set; } = "";
    public string Route { get; set; } = AppRoute.Home;
    public string Snippet { get; set; } = "";
    public DateTimeOffset AddedUtc { get; set; }
}

/// <summary>
/// The message bookmarks (R5B #202): per profile, newest first, in
/// their own file beside the settings — local-only by construction.
/// </summary>
public sealed class MessageBookmarks
{
    public const int MaxPerProfile = 200;
    public const int MaxNicknameLength = 80;
    public const int MaxTitleLength = 200;
    public const int MaxSnippetLength = 280;

    public Dictionary<string, List<MessageBookmark>> ByProfile { get; set; } = new();

    /// <summary>
    /// The nickname rule (SEAN's design): a bookmark cannot be saved
    /// without a name. Returns the cleaned nickname (trimmed,
    /// whitespace-collapsed, capped), or null when nothing usable
    /// remains — every save path (shell prompt, rename, load repair)
    /// goes through this one check.
    /// </summary>
    public static string? CleanNickname(string? nickname)
    {
        var clean = CleanText(nickname);
        if (clean.Length == 0)
            return null;
        return clean.Length <= MaxNicknameLength ? clean : clean[..MaxNicknameLength].TrimEnd();
    }

    /// <summary>The stored snippet: the message text the jump will search for — whitespace-collapsed and capped (the head of the message is its most stable part).</summary>
    public static string CleanSnippet(string? text)
    {
        var clean = CleanText(text);
        return clean.Length <= MaxSnippetLength ? clean : clean[..MaxSnippetLength].TrimEnd();
    }

    /// <summary>
    /// Save a bookmark. The URL must be a muse.ai app page
    /// (<see cref="AppRoute.FromUrl"/> decides, so the stored
    /// reference is a route — never a full address), and the nickname
    /// must satisfy <see cref="CleanNickname"/>. Either failure
    /// returns null and saves nothing.
    /// </summary>
    public MessageBookmark? Add(string profileId, string? nickname, string? conversationTitle,
        string? url, string? snippet, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(profileId) || AppRoute.FromUrl(url) is not { } route)
            return null;
        if (CleanNickname(nickname) is not { } name)
            return null;
        var bookmark = new MessageBookmark
        {
            Id = Guid.NewGuid().ToString("N"),
            Nickname = name,
            ConversationTitle = CleanTitle(conversationTitle),
            Route = route,
            Snippet = CleanSnippet(snippet),
            AddedUtc = now,
        };
        var list = ListFor(profileId);
        list.Insert(0, bookmark);
        while (list.Count > MaxPerProfile)
            list.RemoveAt(list.Count - 1);
        return bookmark;
    }

    /// <summary>Rename a bookmark — the same nickname rule applies: an empty name refuses and the old name stays.</summary>
    public bool Rename(string profileId, string bookmarkId, string? newNickname)
    {
        if (CleanNickname(newNickname) is not { } name)
            return false;
        if (Find(profileId, bookmarkId) is not { } bookmark)
            return false;
        bookmark.Nickname = name;
        return true;
    }

    public bool Remove(string profileId, string bookmarkId) =>
        ByProfile.TryGetValue(profileId, out var list)
        && list.RemoveAll(b => b.Id == bookmarkId) > 0;

    /// <summary>The profile's bookmarks, newest first; empty when it has none.</summary>
    public IReadOnlyList<MessageBookmark> EntriesFor(string profileId) =>
        ByProfile.TryGetValue(profileId, out var list) ? list : Array.Empty<MessageBookmark>();

    public MessageBookmark? Find(string profileId, string bookmarkId) =>
        EntriesFor(profileId).FirstOrDefault(b => b.Id == bookmarkId);

    /// <summary>Repair pass after a load: drop unusable bookmarks — including any without a nickname, which the design says cannot exist — re-sort newest first, cap each list.</summary>
    public void Sanitize()
    {
        foreach (var (profileId, list) in ByProfile.ToList())
        {
            if (string.IsNullOrWhiteSpace(profileId) || list is null)
            {
                ByProfile.Remove(profileId);
                continue;
            }
            list.RemoveAll(b => b is null
                || string.IsNullOrWhiteSpace(b.Id)
                || CleanNickname(b.Nickname) is null
                || AppRoute.Sanitize(b.Route) is null);
            foreach (var bookmark in list)
            {
                bookmark.Nickname = CleanNickname(bookmark.Nickname)!;
                bookmark.Route = AppRoute.Sanitize(bookmark.Route)!;
                bookmark.ConversationTitle = CleanTitle(bookmark.ConversationTitle);
                bookmark.Snippet = CleanSnippet(bookmark.Snippet);
            }
            var sorted = list.OrderByDescending(b => b.AddedUtc).ToList();
            list.Clear();
            list.AddRange(sorted);
            while (list.Count > MaxPerProfile)
                list.RemoveAt(list.Count - 1);
            if (list.Count == 0)
                ByProfile.Remove(profileId);
        }
    }

    private List<MessageBookmark> ListFor(string profileId)
    {
        if (!ByProfile.TryGetValue(profileId, out var list) || list is null)
        {
            list = new List<MessageBookmark>();
            ByProfile[profileId] = list;
        }
        return list;
    }

    private static string CleanTitle(string? title)
    {
        var clean = CleanText(title);
        return clean.Length <= MaxTitleLength ? clean : clean[..MaxTitleLength].TrimEnd();
    }

    /// <summary>One line, single spaces: titles, nicknames, and snippets are display/find text, never documents.</summary>
    private static string CleanText(string? text) =>
        string.IsNullOrWhiteSpace(text)
            ? ""
            : string.Join(" ", text.Split((char[]?)null,
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
}

/// <summary>The message-bookmark store: the same atomic-save, fail-soft-load pattern as the reading-list store.</summary>
public static class MessageBookmarkStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public static MessageBookmarks Load(string path)
    {
        try
        {
            if (!File.Exists(path))
                return new MessageBookmarks();
            var bookmarks = JsonSerializer.Deserialize<MessageBookmarks>(File.ReadAllText(path), Options)
                ?? new MessageBookmarks();
            bookmarks.ByProfile ??= new Dictionary<string, List<MessageBookmark>>();
            bookmarks.Sanitize();
            return bookmarks;
        }
        catch (Exception)
        {
            return new MessageBookmarks();
        }
    }

    public static void Save(string path, MessageBookmarks bookmarks)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(bookmarks, Options));
        File.Move(tmp, path, overwrite: true);
    }
}

/// <summary>One palette command per bookmark (R5B #202): titled by its nickname, payload = the bookmark id (the shell resolves route + snippet from the store at run time, so a renamed or deleted bookmark can never leave a stale command behind).</summary>
public sealed class MessageBookmarksPaletteProvider : IPaletteProvider
{
    private readonly IReadOnlyList<MessageBookmark> _bookmarks;

    public MessageBookmarksPaletteProvider(IReadOnlyList<MessageBookmark> bookmarks) => _bookmarks = bookmarks;

    public string Name => "Bookmarks";

    public IEnumerable<PaletteCommand> GetCommands()
    {
        foreach (var bookmark in _bookmarks)
        {
            yield return new PaletteCommand(
                Id: $"bookmark.open.{bookmark.Id}",
                Title: bookmark.Nickname,
                Detail: string.IsNullOrWhiteSpace(bookmark.ConversationTitle)
                    ? "Bookmark · in a conversation"
                    : $"Bookmark · {bookmark.ConversationTitle}",
                Category: "Bookmarks",
                Keywords: "bookmark marked message saved jump nickname conversation",
                Source: PaletteCommandSource.Bookmark,
                Payload: bookmark.Id);
        }
    }
}

// ─────────────────────── composer drafts (#204) ───────────────────────

/// <summary>
/// One unsent composer draft (R5B #204): the conversation's app
/// ROUTE and the text the user had typed but not sent. Draft text is
/// the user's own words — it is stored only so the app can hand it
/// back, and it is never logged, never exported, and never put in a
/// title or status line.
/// </summary>
public sealed class ConversationDraft
{
    public string Route { get; set; } = AppRoute.Home;
    public string Text { get; set; } = "";
    public DateTimeOffset UpdatedUtc { get; set; }
}

/// <summary>
/// The composer drafts (R5B #204): per profile, one draft per
/// conversation route, in their own file beside the settings. A
/// draft is REPLACED as the user types, REMOVED when the text goes
/// empty (which is what sending looks like from the composer's side)
/// or when the user discards it, and capped so a forgotten draft can
/// never grow the file without bound.
/// </summary>
public sealed class ConversationDrafts
{
    public const int MaxPerProfile = 50;
    public const int MaxTextLength = 20_000;

    public Dictionary<string, List<ConversationDraft>> ByProfile { get; set; } = new();

    /// <summary>
    /// Record the composer's current text for a conversation.
    /// Empty/whitespace text REMOVES the draft (sent or cleared —
    /// either way there is nothing to restore). Returns true when
    /// the stored state changed.
    /// </summary>
    public bool Set(string profileId, string? route, string? text, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(profileId) || AppRoute.Sanitize(route) is not { } cleanRoute)
            return false;
        var list = ListFor(profileId);
        var existing = list.FirstOrDefault(d => d.Route == cleanRoute);
        if (string.IsNullOrWhiteSpace(text))
        {
            if (existing is null)
                return false;
            list.Remove(existing);
            if (list.Count == 0)
                ByProfile.Remove(profileId);
            return true;
        }
        var clean = text.Length <= MaxTextLength ? text : text[..MaxTextLength];
        if (existing is not null)
        {
            if (existing.Text == clean)
            {
                existing.UpdatedUtc = now;
                return false;
            }
            existing.Text = clean;
            existing.UpdatedUtc = now;
            return true;
        }
        list.Add(new ConversationDraft { Route = cleanRoute, Text = clean, UpdatedUtc = now });
        // Over the cap, the stalest drafts go — the user is least
        // likely to come back for the one untouched longest.
        while (list.Count > MaxPerProfile)
        {
            var oldest = list.OrderBy(d => d.UpdatedUtc).First();
            list.Remove(oldest);
        }
        return true;
    }

    /// <summary>The draft saved for a conversation, or null when there is none.</summary>
    public string? Get(string profileId, string? route) =>
        AppRoute.Sanitize(route) is { } cleanRoute
            ? EntriesFor(profileId).FirstOrDefault(d => d.Route == cleanRoute)?.Text
            : null;

    public bool Remove(string profileId, string? route) =>
        AppRoute.Sanitize(route) is { } cleanRoute
        && ByProfile.TryGetValue(profileId, out var list)
        && list.RemoveAll(d => d.Route == cleanRoute) > 0;

    public IReadOnlyList<ConversationDraft> EntriesFor(string profileId) =>
        ByProfile.TryGetValue(profileId, out var list) ? list : Array.Empty<ConversationDraft>();

    public int CountFor(string profileId) => EntriesFor(profileId).Count;

    /// <summary>Repair pass after a load: drop drafts that are empty (nothing to restore), unroutable, or overlong; cap each profile's list by stalest-first.</summary>
    public void Sanitize()
    {
        foreach (var (profileId, list) in ByProfile.ToList())
        {
            if (string.IsNullOrWhiteSpace(profileId) || list is null)
            {
                ByProfile.Remove(profileId);
                continue;
            }
            list.RemoveAll(d => d is null
                || string.IsNullOrWhiteSpace(d.Text)
                || d.Text.Length > MaxTextLength
                || AppRoute.Sanitize(d.Route) is null);
            foreach (var draft in list)
                draft.Route = AppRoute.Sanitize(draft.Route)!;
            // One draft per route: duplicates (a hand-edited file)
            // keep the freshest.
            var freshest = list.GroupBy(d => d.Route)
                .Select(g => g.OrderByDescending(d => d.UpdatedUtc).First())
                .ToList();
            list.Clear();
            list.AddRange(freshest);
            while (list.Count > MaxPerProfile)
            {
                var oldest = list.OrderBy(d => d.UpdatedUtc).First();
                list.Remove(oldest);
            }
            if (list.Count == 0)
                ByProfile.Remove(profileId);
        }
    }

    private List<ConversationDraft> ListFor(string profileId)
    {
        if (!ByProfile.TryGetValue(profileId, out var list) || list is null)
        {
            list = new List<ConversationDraft>();
            ByProfile[profileId] = list;
        }
        return list;
    }
}

/// <summary>The draft store: the same atomic-save, fail-soft-load pattern as the other R5B stores.</summary>
public static class ConversationDraftStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public static ConversationDrafts Load(string path)
    {
        try
        {
            if (!File.Exists(path))
                return new ConversationDrafts();
            var drafts = JsonSerializer.Deserialize<ConversationDrafts>(File.ReadAllText(path), Options)
                ?? new ConversationDrafts();
            drafts.ByProfile ??= new Dictionary<string, List<ConversationDraft>>();
            drafts.Sanitize();
            return drafts;
        }
        catch (Exception)
        {
            return new ConversationDrafts();
        }
    }

    public static void Save(string path, ConversationDrafts drafts)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(drafts, Options));
        File.Move(tmp, path, overwrite: true);
    }
}

// ─────────────────────── prompt snippets (#206) ───────────────────────

/// <summary>
/// One saved prompt snippet (R5B #206): a name the user picks it by,
/// and the prompt text itself. Snippet bodies are the user's own
/// words — stored per profile in their own file, inserted into the
/// composer only at the user's command, and never logged.
/// </summary>
public sealed class PromptSnippet
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Body { get; set; } = "";
    public DateTimeOffset UpdatedUtc { get; set; }
}

/// <summary>
/// The prompt snippet library (R5B #206): per profile, name-sorted,
/// capped. Both fields are required — a snippet you can't name you
/// can't pick from a list, and an empty body inserts nothing.
/// </summary>
public sealed class PromptSnippets
{
    public const int MaxPerProfile = 100;
    public const int MaxNameLength = 60;
    public const int MaxBodyLength = 4_000;

    public Dictionary<string, List<PromptSnippet>> ByProfile { get; set; } = new();

    /// <summary>The cleaned snippet name (one line, capped), or null when nothing usable remains.</summary>
    public static string? CleanName(string? name)
    {
        var clean = string.IsNullOrWhiteSpace(name)
            ? ""
            : string.Join(" ", name.Split((char[]?)null,
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        if (clean.Length == 0)
            return null;
        return clean.Length <= MaxNameLength ? clean : clean[..MaxNameLength].TrimEnd();
    }

    /// <summary>The cleaned snippet body: trimmed at the edges only — the inside is the user's prompt, verbatim — or null when empty or overlong.</summary>
    public static string? CleanBody(string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
            return null;
        var clean = body.Trim();
        return clean.Length <= MaxBodyLength ? clean : null;
    }

    /// <summary>Add a snippet; returns it, or null when the name or body fails validation or the profile is at the cap.</summary>
    public PromptSnippet? Add(string profileId, string? name, string? body, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(profileId)
            || CleanName(name) is not { } cleanName
            || CleanBody(body) is not { } cleanBody)
            return null;
        var list = ListFor(profileId);
        if (list.Count >= MaxPerProfile)
            return null;
        var snippet = new PromptSnippet
        {
            Id = Guid.NewGuid().ToString("N"),
            Name = cleanName,
            Body = cleanBody,
            UpdatedUtc = now,
        };
        list.Add(snippet);
        return snippet;
    }

    /// <summary>Replace a snippet's name and body — the same validation as Add; a failing edit changes nothing.</summary>
    public bool Update(string profileId, string snippetId, string? name, string? body, DateTimeOffset now)
    {
        if (CleanName(name) is not { } cleanName || CleanBody(body) is not { } cleanBody)
            return false;
        if (Find(profileId, snippetId) is not { } snippet)
            return false;
        snippet.Name = cleanName;
        snippet.Body = cleanBody;
        snippet.UpdatedUtc = now;
        return true;
    }

    public bool Remove(string profileId, string snippetId) =>
        ByProfile.TryGetValue(profileId, out var list)
        && list.RemoveAll(s => s.Id == snippetId) > 0;

    /// <summary>The profile's snippets, name-sorted — the order every picker shows.</summary>
    public IReadOnlyList<PromptSnippet> EntriesFor(string profileId) =>
        ByProfile.TryGetValue(profileId, out var list)
            ? list.OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase).ToList()
            : Array.Empty<PromptSnippet>();

    public PromptSnippet? Find(string profileId, string snippetId) =>
        (ByProfile.TryGetValue(profileId, out var list) ? list : null)
            ?.FirstOrDefault(s => s.Id == snippetId);

    /// <summary>Repair pass after a load: drop snippets that fail the same validation Add enforces, cap each list (name order decides what stays).</summary>
    public void Sanitize()
    {
        foreach (var (profileId, list) in ByProfile.ToList())
        {
            if (string.IsNullOrWhiteSpace(profileId) || list is null)
            {
                ByProfile.Remove(profileId);
                continue;
            }
            list.RemoveAll(s => s is null
                || string.IsNullOrWhiteSpace(s.Id)
                || CleanName(s.Name) is null
                || CleanBody(s.Body) is null);
            foreach (var snippet in list)
            {
                snippet.Name = CleanName(snippet.Name)!;
                snippet.Body = CleanBody(snippet.Body)!;
            }
            var sorted = list.OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase).ToList();
            list.Clear();
            list.AddRange(sorted);
            while (list.Count > MaxPerProfile)
                list.RemoveAt(list.Count - 1);
            if (list.Count == 0)
                ByProfile.Remove(profileId);
        }
    }

    private List<PromptSnippet> ListFor(string profileId)
    {
        if (!ByProfile.TryGetValue(profileId, out var list) || list is null)
        {
            list = new List<PromptSnippet>();
            ByProfile[profileId] = list;
        }
        return list;
    }
}

/// <summary>The snippet store: the same atomic-save, fail-soft-load pattern as the other R5B stores.</summary>
public static class PromptSnippetStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public static PromptSnippets Load(string path)
    {
        try
        {
            if (!File.Exists(path))
                return new PromptSnippets();
            var snippets = JsonSerializer.Deserialize<PromptSnippets>(File.ReadAllText(path), Options)
                ?? new PromptSnippets();
            snippets.ByProfile ??= new Dictionary<string, List<PromptSnippet>>();
            snippets.Sanitize();
            return snippets;
        }
        catch (Exception)
        {
            return new PromptSnippets();
        }
    }

    public static void Save(string path, PromptSnippets snippets)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(snippets, Options));
        File.Move(tmp, path, overwrite: true);
    }
}

/// <summary>One palette command per snippet (R5B #206): "Insert snippet: name" — payload = the snippet id, resolved to the body from the store at run time so an edited snippet is always what gets inserted.</summary>
public sealed class PromptSnippetsPaletteProvider : IPaletteProvider
{
    private readonly IReadOnlyList<PromptSnippet> _snippets;

    public PromptSnippetsPaletteProvider(IReadOnlyList<PromptSnippet> snippets) => _snippets = snippets;

    public string Name => "Snippets";

    public IEnumerable<PaletteCommand> GetCommands()
    {
        foreach (var snippet in _snippets)
        {
            yield return new PaletteCommand(
                Id: $"snippet.insert.{snippet.Id}",
                Title: $"Insert snippet: {snippet.Name}",
                Detail: "Snippet · into the message box",
                Category: "Snippets",
                Keywords: "snippet prompt insert saved text template reuse",
                Source: PaletteCommandSource.Snippet,
                Payload: snippet.Id);
        }
    }
}

// ─────────────────────── conversation export (#203) ───────────────────────

/// <summary>One message of an extracted conversation (R5B #203): its role ("user" / "assistant" / anything the page named) and its text, code blocks included as text.</summary>
public sealed record ExportedMessage(string Role, string Text);

/// <summary>
/// The conversation export's text half (R5B #203): parse the page
/// extraction (JSON the injected script produced), render the tidy
/// Markdown, and suggest the save-dialog file name. The shell owns
/// the page reading and the dialog; everything that shapes text is
/// here and pinned. Message content is user data: it goes to the
/// file the user picked and nowhere else — never the log.
/// </summary>
public static class ConversationExport
{
    public const int MaxMessages = 2_000;
    public const int MaxMessageLength = 100_000;
    public const int MaxFileNameLength = 80;

    /// <summary>
    /// Parse the extraction script's output: a JSON array of
    /// {role, text}. Strict like the bridge parser — anything else
    /// (garbage, an object, an error string) yields null, which the
    /// shell reports as "couldn't read the conversation" rather than
    /// exporting a broken file. Empty-text messages drop; caps apply.
    /// </summary>
    public static IReadOnlyList<ExportedMessage>? TryParseExtraction(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
                return null;
            var messages = new List<ExportedMessage>();
            foreach (var el in doc.RootElement.EnumerateArray())
            {
                if (messages.Count >= MaxMessages)
                    break;
                if (el.ValueKind != JsonValueKind.Object)
                    continue;
                var role = el.TryGetProperty("role", out var r) && r.ValueKind == JsonValueKind.String
                    ? (r.GetString() ?? "").Trim()
                    : "";
                var text = el.TryGetProperty("text", out var t) && t.ValueKind == JsonValueKind.String
                    ? (t.GetString() ?? "").Trim()
                    : "";
                if (text.Length == 0)
                    continue;
                if (text.Length > MaxMessageLength)
                    text = text[..MaxMessageLength];
                messages.Add(new ExportedMessage(role, text));
            }
            return messages;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>The heading a role gets in the Markdown: the two known roles get the conversational names; anything else is titled as-is (capitalized, one line, capped).</summary>
    public static string RoleHeading(string? role)
    {
        var clean = string.IsNullOrWhiteSpace(role)
            ? ""
            : string.Join(" ", role.Split((char[]?)null,
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        return clean.ToLowerInvariant() switch
        {
            "user" or "human" => "You",
            "assistant" or "Muse" => "Muse",
            "" => "Message",
            _ => clean.Length <= 40
                ? char.ToUpperInvariant(clean[0]) + clean[1..]
                : char.ToUpperInvariant(clean[0]) + clean[1..40],
        };
    }

    /// <summary>
    /// The tidy Markdown file: the conversation title, the export
    /// date, then one headed section per message in page order. The
    /// message text goes in verbatim — code fences and all — because
    /// tidiness here means structure around the messages, not edits
    /// inside them.
    /// </summary>
    public static string RenderMarkdown(string? conversationTitle, DateTimeOffset exportedUtc,
        IReadOnlyList<ExportedMessage> messages)
    {
        var title = string.IsNullOrWhiteSpace(conversationTitle)
            ? "Muse conversation"
            : string.Join(" ", conversationTitle.Split((char[]?)null,
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        var sb = new StringBuilder();
        sb.Append("# ").Append(title).Append('\n');
        sb.Append('\n');
        sb.Append("Exported from MuseDesktop on ")
          .Append(exportedUtc.LocalDateTime.ToString("yyyy-MM-dd HH:mm")).Append(".\n");
        foreach (var message in messages)
        {
            sb.Append('\n');
            sb.Append("## ").Append(RoleHeading(message.Role)).Append('\n');
            sb.Append('\n');
            sb.Append(message.Text.Trim()).Append('\n');
        }
        return sb.ToString();
    }

    /// <summary>The save dialog's suggested file name: the conversation title, filesystem-safe, dated; "muse-conversation" when the title gives nothing usable.</summary>
    public static string SuggestFileName(string? conversationTitle, DateTimeOffset now, string extension)
    {
        var clean = new StringBuilder();
        var raw = string.IsNullOrWhiteSpace(conversationTitle) ? "" : conversationTitle.Trim();
        foreach (var c in raw)
        {
            if (char.IsLetterOrDigit(c) || c is ' ' or '-' or '_' or '\'')
                clean.Append(c);
        }
        var name = string.Join(" ", clean.ToString().Split((char[]?)null,
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        if (name.Length == 0)
            name = "muse-conversation";
        var suffix = $" — {now:yyyy-MM-dd}";
        if (name.Length + suffix.Length > MaxFileNameLength)
            name = name[..(MaxFileNameLength - suffix.Length)].TrimEnd();
        return name + suffix + extension;
    }
}
