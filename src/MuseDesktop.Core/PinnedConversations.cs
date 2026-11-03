using System.Text;
using System.Text.Json;

namespace MuseDesktop.Core;

// ─────────────────────── pinned conversations ───────────────────────

/// <summary>
/// One pinned conversation: the title the conversation had when it
/// was pinned (frozen — a later rename on the page never rewrites a
/// pin), the conversation's address, and when it was pinned. The
/// address is the conversation's own URL with any query string or
/// fragment dropped: a conversation's identity lives in its path
/// ("/chat/…", <see cref="PageContext.IsConversationRoute"/>), and
/// a query is never part of that identity anywhere in this app —
/// the same discipline that keeps stored routes free of tokens.
/// Pins live in the app's local data per profile; they are never
/// exported, linked, or shared — there is deliberately no code path
/// that takes one out of pins.json.
/// </summary>
public sealed class PinnedConversation
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public string Url { get; set; } = "";
    public DateTimeOffset PinnedUtc { get; set; }
}

/// <summary>How a pin attempt ended — the shell words each one differently in the status line.</summary>
public enum PinOutcome
{
    /// <summary>A new pin was added at the top of the list.</summary>
    Pinned,

    /// <summary>The conversation was already pinned; nothing changed (its pinned-time title stands).</summary>
    AlreadyPinned,

    /// <summary>The profile's list already holds the cap; the pin was refused and nothing changed.</summary>
    Full,

    /// <summary>The address is not a Muse conversation (or the profile id was unusable); nothing was pinned.</summary>
    NotAConversation,
}

/// <summary>
/// The pinned conversations: per profile, most recently pinned
/// first, in their own file beside the settings — local-only by
/// construction, in the message bookmarks' shape (their store is
/// the precedent: per profile, own JSON file, never in settings,
/// never exported). The archive side of the original idea was
/// deliberately not built — see DECISIONS.md.
/// </summary>
public sealed class PinnedConversations
{
    /// <summary>The most conversations one profile can pin — a short list you scan, not a second history.</summary>
    public const int MaxPerProfile = 50;

    public const int MaxTitleLength = 200;

    public Dictionary<string, List<PinnedConversation>> ByProfile { get; set; } = new();

    /// <summary>True when the address names a Muse conversation — the only pages that can be pinned. <see cref="PageContext.IsConversationRoute"/> is the app's one answer to that question.</summary>
    public static bool IsPinnableUrl(string? url) => PageContext.IsConversationRoute(url);

    /// <summary>
    /// The stored form of a conversation address: scheme + host +
    /// path from the parsed URL (host lowercased by the parser, the
    /// query and fragment dropped), or null when the address isn't
    /// a Muse conversation at all. Idempotent — a stored address
    /// cleans to itself.
    /// </summary>
    public static string? CleanUrl(string? url)
    {
        if (!PageContext.IsConversationRoute(url))
            return null;
        var uri = new Uri(url!, UriKind.Absolute);
        var path = string.IsNullOrEmpty(uri.AbsolutePath) ? AppRoute.Home : uri.AbsolutePath;
        return $"https://{uri.Host}{path}";
    }

    /// <summary>
    /// The title a pin keeps: the page title with the app's own
    /// suffix (" — MuseDesktop", the one window titles carry)
    /// removed, whitespace collapsed to one line, capped. Empty
    /// when nothing usable remains — the caller then substitutes
    /// its dated fallback label.
    /// </summary>
    public static string CleanTitle(string? title)
    {
        var clean = CleanText(title);
        const string suffix = " — MuseDesktop";
        if (clean.EndsWith(suffix, StringComparison.Ordinal))
            clean = clean[..^suffix.Length].TrimEnd();
        return clean.Length <= MaxTitleLength ? clean : clean[..MaxTitleLength].TrimEnd();
    }

    /// <summary>
    /// Pin a conversation for a profile. One pin per conversation:
    /// pinning an already-pinned conversation is a no-op that
    /// reports <see cref="PinOutcome.AlreadyPinned"/> and keeps the
    /// original pin (title and time untouched). At the cap the pin
    /// is refused whole — the list is never silently trimmed.
    /// </summary>
    public PinOutcome Pin(string profileId, string? title, string? url,
        DateTimeOffset now, out PinnedConversation? pin)
    {
        pin = null;
        if (string.IsNullOrWhiteSpace(profileId) || CleanUrl(url) is not { } cleanUrl)
            return PinOutcome.NotAConversation;
        var list = ListFor(profileId);
        var existing = list.FirstOrDefault(p => SameUrl(p.Url, cleanUrl));
        if (existing is not null)
        {
            pin = existing;
            return PinOutcome.AlreadyPinned;
        }
        if (list.Count >= MaxPerProfile)
            return PinOutcome.Full;
        pin = new PinnedConversation
        {
            Id = Guid.NewGuid().ToString("N"),
            Title = CleanTitle(title),
            Url = cleanUrl,
            PinnedUtc = now,
        };
        list.Insert(0, pin);
        return PinOutcome.Pinned;
    }

    /// <summary>Remove one pin by id. False when it wasn't there.</summary>
    public bool Unpin(string profileId, string pinId) =>
        ByProfile.TryGetValue(profileId, out var list)
        && list.RemoveAll(p => p.Id == pinId) > 0;

    /// <summary>Remove the profile's pin for a conversation address — the ⋯ menu's toggle path. False when it wasn't pinned.</summary>
    public bool UnpinByUrl(string profileId, string? url) =>
        FindByUrl(profileId, url) is { } pin && Unpin(profileId, pin.Id);

    /// <summary>The profile's pin for a conversation address, or null — the toggle's state check.</summary>
    public PinnedConversation? FindByUrl(string profileId, string? url)
    {
        if (CleanUrl(url) is not { } cleanUrl)
            return null;
        return EntriesFor(profileId).FirstOrDefault(p => SameUrl(p.Url, cleanUrl));
    }

    public bool IsPinned(string profileId, string? url) => FindByUrl(profileId, url) is not null;

    /// <summary>The profile's pins, most recently pinned first; empty when it has none.</summary>
    public IReadOnlyList<PinnedConversation> EntriesFor(string profileId) =>
        ByProfile.TryGetValue(profileId, out var list) ? list : Array.Empty<PinnedConversation>();

    /// <summary>Repair pass after a load: drop unusable pins (no id, an address that is no longer a conversation), re-clean what survives, one pin per conversation (the newest wins), re-sort newest first, cap each list.</summary>
    public void Sanitize()
    {
        foreach (var (profileId, list) in ByProfile.ToList())
        {
            if (string.IsNullOrWhiteSpace(profileId) || list is null)
            {
                ByProfile.Remove(profileId);
                continue;
            }
            list.RemoveAll(p => p is null
                || string.IsNullOrWhiteSpace(p.Id)
                || CleanUrl(p.Url) is null);
            foreach (var pin in list)
            {
                pin.Url = CleanUrl(pin.Url)!;
                pin.Title = CleanTitle(pin.Title);
            }
            var sorted = list.OrderByDescending(p => p.PinnedUtc).ToList();
            list.Clear();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var pin in sorted)
                if (seen.Add(pin.Url))
                    list.Add(pin);
            while (list.Count > MaxPerProfile)
                list.RemoveAt(list.Count - 1);
            if (list.Count == 0)
                ByProfile.Remove(profileId);
        }
    }

    private List<PinnedConversation> ListFor(string profileId)
    {
        if (!ByProfile.TryGetValue(profileId, out var list) || list is null)
        {
            list = new List<PinnedConversation>();
            ByProfile[profileId] = list;
        }
        return list;
    }

    private static bool SameUrl(string? a, string? b) =>
        string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    /// <summary>One line, single spaces — the bookmarks' text cleaning.</summary>
    private static string CleanText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return "";
        var result = new StringBuilder();
        var pendingSpace = false;
        foreach (var character in text)
        {
            if (char.IsWhiteSpace(character))
            {
                pendingSpace = result.Length > 0;
                continue;
            }
            if (pendingSpace)
            {
                result.Append(' ');
                pendingSpace = false;
            }
            result.Append(character);
        }
        return result.ToString().TrimEnd();
    }
}

/// <summary>
/// The pin store: the house pattern (the standing-brief store's
/// shape) — atomic write through the shared <see cref="AtomicFile"/>;
/// a damaged file is preserved as .corrupt-* and an empty list is
/// used, so a broken pins file can never cost the user the app,
/// only the pins.
/// </summary>
public static class PinnedConversationStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public static PinnedConversations Load(string path)
    {
        try
        {
            if (!File.Exists(path))
                return new PinnedConversations();
            var pins = JsonSerializer.Deserialize<PinnedConversations>(File.ReadAllText(path), Options)
                ?? new PinnedConversations();
            pins.ByProfile ??= new Dictionary<string, List<PinnedConversation>>();
            pins.Sanitize();
            return pins;
        }
        catch (Exception)
        {
            try
            {
                var backup = $"{path}.corrupt-{DateTime.Now:yyyyMMdd-HHmmss}";
                // Move, not copy: the poisoned file leaves the live
                // path, so the next load starts empty instead of
                // re-failing (and minting another preserved copy) on
                // every menu open until some unrelated save.
                File.Move(path, backup, overwrite: false);
            }
            catch { /* best effort — an empty list is the contract */ }
            return new PinnedConversations();
        }
    }

    public static void Save(string path, PinnedConversations pins)
    {
        AtomicFile.WriteAllText(path, JsonSerializer.Serialize(pins, Options));
    }
}
