using System.Text;
using System.Text.Json;

namespace MuseDesktop.Core;

// ─────────────────────── context packs (v2.1.0) ───────────────────────
//
// A context pack is a named, local bundle of material that can be
// placed into a fresh conversation in one action: files (whose text
// is read only when a conversation is started), notes kept in the
// pack itself, and references to prompt snippets. The library is a
// small local store in the snippets store's style. Pack contents
// are the user's own words and files: they are never logged, and a
// file item contributes only its file NAME to headings — never its
// full path — when Core composes the context block.

/// <summary>The kind of material one context-pack item points at or holds.</summary>
public enum ContextPackItemKind
{
    /// <summary>A local file; only its path is stored, and its text is read at compose time.</summary>
    File,

    /// <summary>A note stored inside the pack itself (a title and text).</summary>
    Note,

    /// <summary>A prompt snippet, referenced by id so an edited snippet is what a later start uses.</summary>
    Snippet,
}

/// <summary>
/// One item in a context pack. Only the fields for its
/// <see cref="Kind"/> are meaningful; sanitizing clears the rest so
/// a hand-edited store cannot smuggle a second payload into a row.
/// </summary>
public sealed class ContextPackItem
{
    public string Id { get; set; } = "";
    public ContextPackItemKind Kind { get; set; }

    /// <summary>The local file path (File items only). Stored because the pack must find the file again; never logged and never put in a composed heading.</summary>
    public string Path { get; set; } = "";

    /// <summary>The note's title (Note items only).</summary>
    public string Title { get; set; } = "";

    /// <summary>The note's text (Note items only).</summary>
    public string Text { get; set; } = "";

    /// <summary>The referenced snippet's id (Snippet items only).</summary>
    public string SnippetId { get; set; } = "";
}

/// <summary>One named context pack: its identity, its timestamps, and its items in the order the user added them.</summary>
public sealed class ContextPack
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public DateTimeOffset CreatedUtc { get; set; }
    public DateTimeOffset UpdatedUtc { get; set; }
    public List<ContextPackItem> Items { get; set; } = new();
}

/// <summary>
/// The context-pack library: an order-preserving list of packs,
/// capped so the manager and a composed conversation stay usable.
/// Every mutation validates like the snippet library's does, and a
/// failed mutation changes nothing (including the timestamps).
/// </summary>
public sealed class ContextPackLibrary
{
    public const int MaxPacks = 50;
    public const int MaxItemsPerPack = 30;
    public const int MaxNameLength = 80;
    public const int MaxNoteTitleLength = 80;
    public const int MaxNoteTextLength = 12_000;
    public const int MaxFilePathLength = 4_096;

    public List<ContextPack> Packs { get; set; } = new();

    /// <summary>The cleaned pack name (one line, capped), or null when nothing usable remains.</summary>
    public static string? CleanName(string? name) => CleanOneLine(name, MaxNameLength);

    /// <summary>The cleaned note title (one line, capped), or null when nothing usable remains.</summary>
    public static string? CleanNoteTitle(string? title) => CleanOneLine(title, MaxNoteTitleLength);

    /// <summary>The cleaned note text: line endings normalized and edges trimmed — the inside is the user's note, verbatim — or null when empty or overlong.</summary>
    public static string? CleanNoteText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;
        var clean = text.Replace("\r\n", "\n").Replace('\r', '\n').Trim();
        return clean.Length <= MaxNoteTextLength ? clean : null;
    }

    /// <summary>The cleaned file path (trimmed, bounded), or null when nothing usable remains. Existence is NOT judged here: a missing file is a compose-time placeholder, not a reason to drop the user's pack entry.</summary>
    public static string? CleanFilePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return null;
        var clean = path.Trim();
        if (clean.Length > MaxFilePathLength || clean.Any(char.IsControl))
            return null;
        return clean;
    }

    /// <summary>The cleaned snippet id (one short token), or null when it cannot name a snippet.</summary>
    public static string? CleanSnippetId(string? snippetId)
    {
        if (string.IsNullOrWhiteSpace(snippetId))
            return null;
        var clean = snippetId.Trim();
        return clean.Length <= 100 && !clean.Any(c => char.IsWhiteSpace(c) || char.IsControl(c))
            ? clean
            : null;
    }

    public ContextPack? FindPack(string? packId) =>
        string.IsNullOrWhiteSpace(packId)
            ? null
            : Packs.FirstOrDefault(p => p.Id == packId);

    public ContextPackItem? FindItem(string? packId, string? itemId) =>
        string.IsNullOrWhiteSpace(itemId)
            ? null
            : FindPack(packId)?.Items.FirstOrDefault(i => i.Id == itemId);

    /// <summary>Add a pack at the end of the library; returns it, or null when the name fails validation or the library is at its cap.</summary>
    public ContextPack? AddPack(string? name, DateTimeOffset now)
    {
        if (CleanName(name) is not { } cleanName || Packs.Count >= MaxPacks)
            return null;
        var pack = new ContextPack
        {
            Id = Guid.NewGuid().ToString("N"),
            Name = cleanName,
            CreatedUtc = now,
            UpdatedUtc = now,
        };
        Packs.Add(pack);
        return pack;
    }

    /// <summary>Rename a pack — the same validation as Add; a failing rename changes nothing.</summary>
    public bool RenamePack(string packId, string? name, DateTimeOffset now)
    {
        if (CleanName(name) is not { } cleanName || FindPack(packId) is not { } pack)
            return false;
        pack.Name = cleanName;
        pack.UpdatedUtc = now;
        return true;
    }

    /// <summary>Remove a pack; returns false when it isn't there.</summary>
    public bool RemovePack(string packId) =>
        FindPack(packId) is { } pack && Packs.Remove(pack);

    /// <summary>
    /// Put a previously removed pack back at (or near) its old
    /// position — the manager's one-step undo for a delete. The pack
    /// goes through the same sanitize pass as a loaded one; a pack
    /// whose id is already present, or one that arrives when the
    /// library is full, is refused.
    /// </summary>
    public bool RestorePack(ContextPack? pack, int index)
    {
        if (pack is null || string.IsNullOrWhiteSpace(pack.Id)
            || FindPack(pack.Id) is not null || Packs.Count >= MaxPacks)
            return false;
        Packs.Insert(Math.Clamp(index, 0, Packs.Count), pack);
        Sanitize();
        return FindPack(pack.Id) is not null;
    }

    /// <summary>Add a file item to a pack; returns it, or null when the pack is missing/full or the path fails validation.</summary>
    public ContextPackItem? AddFileItem(string packId, string? path, DateTimeOffset now)
    {
        if (CleanFilePath(path) is not { } cleanPath || !CanAddItem(packId, out var pack))
            return null;
        var item = new ContextPackItem
        {
            Id = Guid.NewGuid().ToString("N"),
            Kind = ContextPackItemKind.File,
            Path = cleanPath,
        };
        pack!.Items.Add(item);
        pack.UpdatedUtc = now;
        return item;
    }

    /// <summary>Add a note item to a pack; returns it, or null when the pack is missing/full or the note fails validation.</summary>
    public ContextPackItem? AddNoteItem(string packId, string? title, string? text, DateTimeOffset now)
    {
        if (CleanNoteTitle(title) is not { } cleanTitle
            || CleanNoteText(text) is not { } cleanText
            || !CanAddItem(packId, out var pack))
            return null;
        var item = new ContextPackItem
        {
            Id = Guid.NewGuid().ToString("N"),
            Kind = ContextPackItemKind.Note,
            Title = cleanTitle,
            Text = cleanText,
        };
        pack!.Items.Add(item);
        pack.UpdatedUtc = now;
        return item;
    }

    /// <summary>Add a snippet reference to a pack; returns it, or null when the pack is missing/full or the id fails validation.</summary>
    public ContextPackItem? AddSnippetItem(string packId, string? snippetId, DateTimeOffset now)
    {
        if (CleanSnippetId(snippetId) is not { } cleanId || !CanAddItem(packId, out var pack))
            return null;
        var item = new ContextPackItem
        {
            Id = Guid.NewGuid().ToString("N"),
            Kind = ContextPackItemKind.Snippet,
            SnippetId = cleanId,
        };
        pack!.Items.Add(item);
        pack.UpdatedUtc = now;
        return item;
    }

    /// <summary>Remove one item from a pack; returns false when either isn't there.</summary>
    public bool RemoveItem(string packId, string itemId, DateTimeOffset now)
    {
        if (FindPack(packId) is not { } pack || FindItem(packId, itemId) is not { } item)
            return false;
        pack.Items.Remove(item);
        pack.UpdatedUtc = now;
        return true;
    }

    /// <summary>Repair pass after a load (and after a restore): drop packs and items that fail the same validation the Add methods enforce, cap both lists in their stored order, and clear fields a kind doesn't use.</summary>
    public void Sanitize()
    {
        var seenPackIds = new HashSet<string>(StringComparer.Ordinal);
        var cleanPacks = new List<ContextPack>();
        foreach (var pack in Packs)
        {
            if (cleanPacks.Count >= MaxPacks)
                break;
            if (pack is null || string.IsNullOrWhiteSpace(pack.Id)
                || !seenPackIds.Add(pack.Id)
                || CleanName(pack.Name) is not { } cleanName)
                continue;
            pack.Name = cleanName;
            if (pack.CreatedUtc == default && pack.UpdatedUtc != default)
                pack.CreatedUtc = pack.UpdatedUtc;
            if (pack.UpdatedUtc == default)
                pack.UpdatedUtc = pack.CreatedUtc;
            pack.Items ??= new List<ContextPackItem>();
            var seenItemIds = new HashSet<string>(StringComparer.Ordinal);
            var cleanItems = new List<ContextPackItem>();
            foreach (var item in pack.Items)
            {
                if (cleanItems.Count >= MaxItemsPerPack)
                    break;
                if (item is null || string.IsNullOrWhiteSpace(item.Id) || !seenItemIds.Add(item.Id))
                    continue;
                switch (item.Kind)
                {
                    case ContextPackItemKind.File:
                        if (CleanFilePath(item.Path) is not { } cleanPath)
                            continue;
                        item.Path = cleanPath;
                        item.Title = "";
                        item.Text = "";
                        item.SnippetId = "";
                        break;
                    case ContextPackItemKind.Note:
                        if (CleanNoteTitle(item.Title) is not { } cleanTitle
                            || CleanNoteText(item.Text) is not { } cleanText)
                            continue;
                        item.Title = cleanTitle;
                        item.Text = cleanText;
                        item.Path = "";
                        item.SnippetId = "";
                        break;
                    case ContextPackItemKind.Snippet:
                        if (CleanSnippetId(item.SnippetId) is not { } cleanSnippetId)
                            continue;
                        item.SnippetId = cleanSnippetId;
                        item.Path = "";
                        item.Title = "";
                        item.Text = "";
                        break;
                    default:
                        continue;
                }
                cleanItems.Add(item);
            }
            pack.Items = cleanItems;
            cleanPacks.Add(pack);
        }
        Packs = cleanPacks;
    }

    private bool CanAddItem(string packId, out ContextPack? pack)
    {
        pack = FindPack(packId);
        return pack is not null && pack.Items.Count < MaxItemsPerPack;
    }

    private static string? CleanOneLine(string? value, int maxLength)
    {
        var clean = string.IsNullOrWhiteSpace(value)
            ? ""
            : string.Join(" ", value.Split((char[]?)null,
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        if (clean.Length == 0)
            return null;
        return clean.Length <= maxLength ? clean : clean[..maxLength].TrimEnd();
    }
}

/// <summary>
/// The context-pack store: local JSON beside the other small
/// stores, written atomically. A file that fails to parse is
/// preserved as <c>.corrupt-*</c> and an empty library is used —
/// the user's packs must never cost them the app, and the damaged
/// file is kept for whatever recovery is possible.
/// </summary>
public static class ContextPackStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public static ContextPackLibrary Load(string path)
    {
        try
        {
            if (!File.Exists(path))
                return new ContextPackLibrary();
            var library = JsonSerializer.Deserialize<ContextPackLibrary>(File.ReadAllText(path), Options)
                ?? throw new JsonException("The context-pack file did not contain a library.");
            library.Packs ??= new List<ContextPack>();
            library.Sanitize();
            return library;
        }
        catch (Exception)
        {
            PreserveCorruptCopy(path);
            return new ContextPackLibrary();
        }
    }

    public static void Save(string path, ContextPackLibrary library)
    {
        ArgumentNullException.ThrowIfNull(library);
        AtomicFile.WriteAllText(path, JsonSerializer.Serialize(library, Options));
    }

    private static void PreserveCorruptCopy(string path)
    {
        try
        {
            if (!File.Exists(path))
                return;
            var backup = $"{path}.corrupt-{DateTime.Now:yyyyMMdd-HHmmss-fff}-{Guid.NewGuid():N}";
            File.Copy(path, backup, overwrite: false);
        }
        catch
        {
            // Best effort — an empty library is the contract either way.
        }
    }
}

/// <summary>The words a composed context block needs, resolved by the shell from the string catalog (the DraftMeter/PageContext split: Core composes, the catalog words). Header and section templates take {0} as the pack/file/note/snippet name; the file-unavailable template takes {0} as the file name.</summary>
public sealed record ContextPackComposeLabels(
    string Header,
    string FileSection,
    string NoteSection,
    string SnippetSection,
    string SnippetUnavailableSection,
    string FileUnavailable,
    string SnippetUnavailable,
    string ItemCutNote,
    string TotalCutNote);

/// <summary>
/// Turns a pack into the one organized block that starts a
/// conversation. Files are read through the caller's reader at
/// compose time only; a file that cannot be read as text becomes a
/// one-line placeholder section rather than failing the pack. A
/// snippet that has left the library gets the same honest
/// placeholder treatment. Nothing here logs or keeps the text.
/// </summary>
public static class ContextPackComposer
{
    /// <summary>The most text one file item may contribute.</summary>
    public const int MaxFileTextChars = 8_000;

    /// <summary>The most characters the whole composed block may carry, headings and cut notes included.</summary>
    public const int MaxTotalChars = 24_000;

    /// <summary>
    /// Compose the block for <paramref name="pack"/>: a header
    /// naming the pack, then one labelled section per item in pack
    /// order. Returns null for a missing or empty pack — there is
    /// nothing to start a conversation with.
    /// </summary>
    public static string? Compose(
        ContextPack? pack,
        Func<string, string?>? fileReader,
        Func<string, PromptSnippet?>? snippetLookup,
        ContextPackComposeLabels labels)
    {
        ArgumentNullException.ThrowIfNull(labels);
        if (pack is null || pack.Items is null || pack.Items.Count == 0)
            return null;

        var sections = new List<string>();
        foreach (var item in pack.Items)
        {
            if (item is not null)
                sections.Add(RenderSection(item, fileReader, snippetLookup, labels));
        }
        if (sections.Count == 0)
            return null;

        var packName = ContextPackLibrary.CleanName(pack.Name) ?? "";
        var result = new StringBuilder(string.Format(labels.Header, packName));
        foreach (var section in sections)
        {
            if (result.Length + 2 + section.Length <= MaxTotalChars)
            {
                result.Append("\n\n").Append(section);
                continue;
            }
            // The total cap wins over completeness — and says so in
            // the block, so nobody mistakes a cut pack for the whole
            // of it. Room is reserved for the note before any of
            // this section is admitted.
            var noteBlock = "\n\n" + labels.TotalCutNote;
            var room = MaxTotalChars - result.Length - 2 - noteBlock.Length;
            if (room > 0)
            {
                var partial = section[..Math.Min(section.Length, room)].TrimEnd();
                if (partial.Length > 0)
                    result.Append("\n\n").Append(partial);
            }
            result.Append(noteBlock);
            break;
        }
        var composed = result.ToString();
        return composed.Length <= MaxTotalChars ? composed : composed[..MaxTotalChars];
    }

    private static string RenderSection(
        ContextPackItem item,
        Func<string, string?>? fileReader,
        Func<string, PromptSnippet?>? snippetLookup,
        ContextPackComposeLabels labels)
    {
        switch (item.Kind)
        {
            case ContextPackItemKind.Note:
            {
                var title = ContextPackLibrary.CleanNoteTitle(item.Title) ?? "";
                var text = NormalizeText(item.Text);
                return string.Format(labels.NoteSection, title) + "\n\n" + text;
            }
            case ContextPackItemKind.Snippet:
            {
                PromptSnippet? snippet = null;
                try { snippet = snippetLookup?.Invoke(item.SnippetId); }
                catch (Exception) { snippet = null; }
                if (snippet is null || string.IsNullOrWhiteSpace(snippet.Body))
                    return labels.SnippetUnavailableSection + "\n\n" + labels.SnippetUnavailable;
                var name = PromptSnippets.CleanName(snippet.Name) ?? "";
                return string.Format(labels.SnippetSection, name) + "\n\n" + NormalizeText(snippet.Body);
            }
            case ContextPackItemKind.File:
            default:
            {
                var fileName = FileNameForHeading(item.Path);
                var heading = string.Format(labels.FileSection, fileName);
                string? raw = null;
                try { raw = fileReader?.Invoke(item.Path); }
                catch (Exception) { raw = null; }
                var text = NormalizeText(raw);
                if (text.Length == 0)
                    return heading + "\n\n" + string.Format(labels.FileUnavailable, fileName);
                if (text.Length > MaxFileTextChars)
                    text = text[..MaxFileTextChars].TrimEnd() + "\n\n" + labels.ItemCutNote;
                return heading + "\n\n" + text;
            }
        }
    }

    /// <summary>The heading name for a file item: the file name only — a full path never enters the composed block.
    /// Both separators are honored explicitly (a stored path is a Windows path whatever OS composes it).</summary>
    private static string FileNameForHeading(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return "";
        var trimmed = path.Trim().TrimEnd('\\', '/');
        if (trimmed.Length == 0)
            return "";
        var cut = trimmed.LastIndexOfAny(['\\', '/']);
        var name = cut >= 0 && cut + 1 < trimmed.Length ? trimmed[(cut + 1)..] : trimmed;
        return string.IsNullOrWhiteSpace(name) ? trimmed : name.Trim();
    }

    private static string NormalizeText(string? text) =>
        string.IsNullOrWhiteSpace(text)
            ? ""
            : text.Replace("\r\n", "\n").Replace('\r', '\n').Trim();
}
