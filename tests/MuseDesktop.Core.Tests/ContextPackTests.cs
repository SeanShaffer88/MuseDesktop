using MuseDesktop.Core;
using Xunit;

namespace MuseDesktop.Core.Tests;

// ───────────────────── Context packs: store, caps, compose ─────────────────────

/// <summary>
/// The context-pack rules (v2.1.0): the library round-trips through
/// its JSON store in order; a damaged file is preserved as
/// .corrupt-* and loads empty; the pack and item caps hold;
/// composing produces one labelled block — file headings name the
/// file only (never its path), each file is capped, the whole block
/// is capped with a visible note, and a file or snippet that can't
/// be produced becomes a placeholder section instead of failing
/// the pack. An empty pack composes to null.
/// </summary>
public class ContextPackStoreTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 2, 12, 0, 0, TimeSpan.Zero);

    private sealed class TempDir : IDisposable
    {
        public TempDir() =>
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(), "musedesktop-tests", Guid.NewGuid().ToString("N"));

        public string Path { get; }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(Path))
                    Directory.Delete(Path, recursive: true);
            }
            catch { /* a temp folder is not worth failing a test over */ }
        }
    }

    [Fact]
    public void Store_RoundTripsPacksAndItemsInOrder()
    {
        using var temp = new TempDir();
        Directory.CreateDirectory(temp.Path);
        var path = System.IO.Path.Combine(temp.Path, "context-packs.json");
        var library = new ContextPackLibrary();
        var first = library.AddPack("Book research", Now)!;
        var second = library.AddPack("Taxes 2026", Now.AddMinutes(1))!;
        library.AddFileItem(first.Id, @"C:\Users\Sean\Notes\chapter.txt", Now);
        library.AddNoteItem(first.Id, "Angle", "Start with the river scene.", Now);
        library.AddSnippetItem(first.Id, "snippet-1", Now);
        library.AddNoteItem(second.Id, "Forms", "W-2 and 1099 are in the folder.", Now);
        ContextPackStore.Save(path, library);

        var loaded = ContextPackStore.Load(path);
        Assert.Equal(new[] { "Book research", "Taxes 2026" }, loaded.Packs.Select(p => p.Name));
        var loadedFirst = loaded.Packs[0];
        Assert.Equal(first.Id, loadedFirst.Id);
        Assert.Equal(Now, loadedFirst.CreatedUtc);
        Assert.Equal(
            new[] { ContextPackItemKind.File, ContextPackItemKind.Note, ContextPackItemKind.Snippet },
            loadedFirst.Items.Select(i => i.Kind));
        Assert.Equal(@"C:\Users\Sean\Notes\chapter.txt", loadedFirst.Items[0].Path);
        Assert.Equal("Angle", loadedFirst.Items[1].Title);
        Assert.Equal("Start with the river scene.", loadedFirst.Items[1].Text);
        Assert.Equal("snippet-1", loadedFirst.Items[2].SnippetId);
        Assert.Single(loaded.Packs[1].Items);
    }

    [Fact]
    public void Store_MissingFile_IsAnEmptyLibrary()
    {
        using var temp = new TempDir();
        var loaded = ContextPackStore.Load(System.IO.Path.Combine(temp.Path, "nope.json"));
        Assert.Empty(loaded.Packs);
    }

    [Fact]
    public void Store_CorruptFile_IsPreserved_AndAnEmptyLibraryLoads()
    {
        using var temp = new TempDir();
        Directory.CreateDirectory(temp.Path);
        var path = System.IO.Path.Combine(temp.Path, "context-packs.json");
        File.WriteAllText(path, "{ this is not json");
        var loaded = ContextPackStore.Load(path);
        Assert.Empty(loaded.Packs);
        var preserved = Directory.GetFiles(temp.Path, "context-packs.json.corrupt-*");
        Assert.Single(preserved);
        Assert.Equal("{ this is not json", File.ReadAllText(preserved[0]));
    }

    [Fact]
    public void Library_EnforcesThePackCap()
    {
        var library = new ContextPackLibrary();
        for (var i = 0; i < ContextPackLibrary.MaxPacks; i++)
            Assert.NotNull(library.AddPack($"Pack {i}", Now));
        Assert.Null(library.AddPack("One too many", Now));
        Assert.Equal(ContextPackLibrary.MaxPacks, library.Packs.Count);
    }

    [Fact]
    public void Library_EnforcesTheItemCap()
    {
        var library = new ContextPackLibrary();
        var pack = library.AddPack("Full", Now)!;
        for (var i = 0; i < ContextPackLibrary.MaxItemsPerPack; i++)
            Assert.NotNull(library.AddNoteItem(pack.Id, $"Note {i}", "Text.", Now));
        Assert.Null(library.AddNoteItem(pack.Id, "One too many", "Text.", Now));
        Assert.Null(library.AddFileItem(pack.Id, @"C:\extra.txt", Now));
        Assert.Null(library.AddSnippetItem(pack.Id, "snippet-1", Now));
        Assert.Equal(ContextPackLibrary.MaxItemsPerPack, pack.Items.Count);
    }

    [Fact]
    public void Library_RejectsUnusableNamesNotesPathsAndSnippetIds()
    {
        var library = new ContextPackLibrary();
        Assert.Null(library.AddPack("   ", Now));
        var pack = library.AddPack("  Many   spaces  ", Now)!;
        Assert.Equal("Many spaces", pack.Name);
        Assert.Null(library.AddNoteItem(pack.Id, "", "Text.", Now));
        Assert.Null(library.AddNoteItem(pack.Id, "Title", "   ", Now));
        Assert.Null(library.AddFileItem(pack.Id, " ", Now));
        Assert.Null(library.AddSnippetItem(pack.Id, "has spaces", Now));
        Assert.Empty(pack.Items);
    }

    [Fact]
    public void Library_RenameRemoveAndRestore_KeepOrder()
    {
        var library = new ContextPackLibrary();
        var a = library.AddPack("A", Now)!;
        var b = library.AddPack("B", Now)!;
        var c = library.AddPack("C", Now)!;
        Assert.True(library.RenamePack(b.Id, "Bee", Now.AddMinutes(2)));
        Assert.Equal("Bee", library.FindPack(b.Id)!.Name);
        Assert.Equal(Now.AddMinutes(2), library.FindPack(b.Id)!.UpdatedUtc);
        Assert.False(library.RenamePack(b.Id, " ", Now));

        Assert.True(library.RemovePack(b.Id));
        Assert.Equal(new[] { "A", "C" }, library.Packs.Select(p => p.Name));
        Assert.True(library.RestorePack(b, 1));
        Assert.Equal(new[] { "A", "Bee", "C" }, library.Packs.Select(p => p.Name));
        Assert.False(library.RestorePack(b, 0));   // the id is present again
        Assert.False(library.RemovePack("missing"));
        Assert.NotNull(a);
        Assert.NotNull(c);
    }

    [Fact]
    public void Sanitize_DropsInvalidEntries_CapsInStoredOrder_AndClearsUnusedFields()
    {
        var library = new ContextPackLibrary
        {
            Packs = Enumerable.Range(0, ContextPackLibrary.MaxPacks + 5)
                .Select(i => new ContextPack
                {
                    Id = $"pack-{i}",
                    Name = $"Pack {i}",
                    Items = new List<ContextPackItem>
                    {
                        new()
                        {
                            Id = $"item-{i}",
                            Kind = ContextPackItemKind.Note,
                            Title = "Kept",
                            Text = "Kept text.",
                            Path = @"C:\should-be-cleared.txt",
                            SnippetId = "should-be-cleared",
                        },
                    },
                })
                .ToList(),
        };
        library.Packs.Add(new ContextPack { Id = "pack-0", Name = "Duplicate id" });
        library.Packs.Add(new ContextPack { Id = "blank-name", Name = " " });
        library.Sanitize();
        Assert.Equal(ContextPackLibrary.MaxPacks, library.Packs.Count);
        Assert.Equal("Pack 0", library.Packs[0].Name);
        var item = library.Packs[0].Items.Single();
        Assert.Equal("", item.Path);
        Assert.Equal("", item.SnippetId);
    }
}

public class ContextPackComposeTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 2, 12, 0, 0, TimeSpan.Zero);

    private static readonly ContextPackComposeLabels Labels = new(
        Header: "Context pack: {0}",
        FileSection: "File: {0}",
        NoteSection: "Note: {0}",
        SnippetSection: "Snippet: {0}",
        SnippetUnavailableSection: "Snippet: no longer in the library",
        FileUnavailable: "(file {0} could not be read)",
        SnippetUnavailable: "This snippet could not be included because it is no longer in the snippet library.",
        ItemCutNote: "[…the rest of this item was left out]",
        TotalCutNote: "[…the rest of the pack was left out — it was longer than fits here]",
        ReadingSection: "Saved page: {0}",
        ReadingUnavailableSection: "Saved page: no longer in the reading list",
        ReadingUnavailable: "(saved page {0} could not be read)",
        ReadingMissing: "This saved page could not be included because it is no longer in the reading list.");

    private static ContextPackLibrary LibraryWithPack(out ContextPack pack)
    {
        var library = new ContextPackLibrary();
        pack = library.AddPack("Book research", Now)!;
        return library;
    }

    [Fact]
    public void Compose_EmptyOrMissingPack_IsNull()
    {
        var library = LibraryWithPack(out var pack);
        Assert.Null(ContextPackComposer.Compose(null, _ => null, _ => null, Labels));
        Assert.Null(ContextPackComposer.Compose(pack, _ => null, _ => null, Labels));
        Assert.NotNull(library);
    }

    [Fact]
    public void Compose_LabelsEverySection_AndFileHeadingsUseTheFileNameOnly()
    {
        var library = LibraryWithPack(out var pack);
        library.AddFileItem(pack.Id, @"C:\Users\Sean\Notes\chapter.txt", Now);
        library.AddNoteItem(pack.Id, "Angle", "Start with the river scene.", Now);
        library.AddSnippetItem(pack.Id, "snippet-1", Now);
        var files = new Dictionary<string, string?> { [@"C:\Users\Sean\Notes\chapter.txt"] = "Chapter one text." };
        var snippets = new Dictionary<string, PromptSnippet>
        {
            ["snippet-1"] = new() { Id = "snippet-1", Name = "Explain simply", Body = "Explain {{topic}} simply." },
        };

        var composed = ContextPackComposer.Compose(
            pack,
            path => files.TryGetValue(path, out var text) ? text : null,
            id => snippets.TryGetValue(id, out var snippet) ? snippet : null,
            Labels);

        Assert.Equal(
            "Context pack: Book research\n\n" +
            "File: chapter.txt\n\nChapter one text.\n\n" +
            "Note: Angle\n\nStart with the river scene.\n\n" +
            "Snippet: Explain simply\n\nExplain {{topic}} simply.",
            composed);
        Assert.DoesNotContain(@"C:\Users\Sean", composed);
    }

    [Fact]
    public void Compose_UnreadableOrUndecodableFiles_BecomePlaceholders_AndThePackStillComposes()
    {
        var library = LibraryWithPack(out var pack);
        library.AddFileItem(pack.Id, @"D:\gone.txt", Now);
        library.AddFileItem(pack.Id, @"D:\binary.dat", Now);
        library.AddNoteItem(pack.Id, "Still here", "The note survives.", Now);

        var composed = ContextPackComposer.Compose(
            pack,
            path => path.EndsWith("binary.dat") ? throw new InvalidOperationException("not text") : null,
            _ => null,
            Labels);

        Assert.NotNull(composed);
        Assert.Contains("File: gone.txt\n\n(file gone.txt could not be read)", composed);
        Assert.Contains("File: binary.dat\n\n(file binary.dat could not be read)", composed);
        Assert.Contains("Note: Still here\n\nThe note survives.", composed);
    }

    [Fact]
    public void Compose_MissingSnippet_BecomesAPlaceholder()
    {
        var library = LibraryWithPack(out var pack);
        library.AddSnippetItem(pack.Id, "deleted-snippet", Now);
        var composed = ContextPackComposer.Compose(pack, _ => null, _ => null, Labels);
        Assert.Equal(
            "Context pack: Book research\n\n" +
            "Snippet: no longer in the library\n\n" +
            "This snippet could not be included because it is no longer in the snippet library.",
            composed);
        Assert.NotNull(library);
    }

    [Fact]
    public void Compose_CapsAFileAtThePerFileCap_AndSaysSo()
    {
        var library = LibraryWithPack(out var pack);
        library.AddFileItem(pack.Id, @"D:\long.txt", Now);
        var text = new string('a', ContextPackComposer.MaxFileTextChars + 500);
        var composed = ContextPackComposer.Compose(pack, _ => text, _ => null, Labels);
        Assert.NotNull(composed);
        Assert.Contains(new string('a', ContextPackComposer.MaxFileTextChars), composed);
        Assert.DoesNotContain(new string('a', ContextPackComposer.MaxFileTextChars + 1), composed);
        Assert.Contains(Labels.ItemCutNote, composed);
        Assert.EndsWith(Labels.ItemCutNote, composed);
        Assert.NotNull(library);
    }

    [Fact]
    public void Compose_CapsTheWholeBlockAtTheTotalCap_AndSaysSo()
    {
        var library = LibraryWithPack(out var pack);
        for (var i = 0; i < 5; i++)
            library.AddNoteItem(pack.Id, $"Note {i}", new string('n', 6_000), Now);
        var composed = ContextPackComposer.Compose(pack, _ => null, _ => null, Labels);
        Assert.NotNull(composed);
        Assert.True(composed.Length <= ContextPackComposer.MaxTotalChars,
            $"Composed block was {composed.Length} characters.");
        Assert.EndsWith(Labels.TotalCutNote, composed);
        Assert.Contains("Note: Note 0", composed);
        Assert.DoesNotContain("Note: Note 4", composed);
        Assert.NotNull(library);
    }

    [Fact]
    public void Compose_ExactlyAtTheTotalCap_IsNotMarkedCut()
    {
        var library = LibraryWithPack(out var pack);
        // Header (27) + blank line (2) + section heading (7) +
        // blank line (2) + body = the cap exactly.
        var bodyLength = ContextPackComposer.MaxTotalChars - 38;
        library.AddNoteItem(pack.Id, "T", "x", Now);
        pack.Items[0].Text = new string('b', bodyLength);
        var composed = ContextPackComposer.Compose(pack, _ => null, _ => null, Labels);
        Assert.NotNull(composed);
        Assert.Equal(ContextPackComposer.MaxTotalChars, composed.Length);
        Assert.DoesNotContain(Labels.TotalCutNote, composed);
        Assert.NotNull(library);
    }
}

// ───────────────────── Reading items in packs (M2) ─────────────────────
//
// The fourth kind: a reference to a reading-list entry, by id,
// like the snippet kind. These tests pin the library half (add,
// validation, sanitize clearing the other kinds' fields) and the
// compose half (the entry's title heads the section; the resolved
// text obeys the per-item cap; a gone entry and an unresolvable
// page each become their one-line placeholder).

public class ContextPackReadingItemTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 3, 12, 0, 0, TimeSpan.Zero);

    private static readonly ContextPackComposeLabels Labels = new(
        Header: "Context pack: {0}",
        FileSection: "File: {0}",
        NoteSection: "Note: {0}",
        SnippetSection: "Snippet: {0}",
        SnippetUnavailableSection: "Snippet: no longer in the library",
        FileUnavailable: "(file {0} could not be read)",
        SnippetUnavailable: "This snippet could not be included because it is no longer in the snippet library.",
        ItemCutNote: "[…the rest of this item was left out]",
        TotalCutNote: "[…the rest of the pack was left out — it was longer than fits here]",
        ReadingSection: "Saved page: {0}",
        ReadingUnavailableSection: "Saved page: no longer in the reading list",
        ReadingUnavailable: "(saved page {0} could not be read)",
        ReadingMissing: "This saved page could not be included because it is no longer in the reading list.");

    private static ReadingListEntry Entry(string id, string title, string route = "/projects") =>
        new() { Id = id, Title = title, Route = route, AddedUtc = Now };

    [Fact]
    public void AddReadingItem_StoresOnlyTheEntryId()
    {
        var library = new ContextPackLibrary();
        var pack = library.AddPack("Research", Now)!;
        var item = library.AddReadingItem(pack.Id, "entry-1", Now);
        Assert.NotNull(item);
        Assert.Equal(ContextPackItemKind.Reading, item!.Kind);
        Assert.Equal("entry-1", item.ReadingEntryId);
        Assert.Equal("", item.Path);
        Assert.Equal("", item.SnippetId);
    }

    [Fact]
    public void AddReadingItem_RejectsUnusableIds()
    {
        var library = new ContextPackLibrary();
        var pack = library.AddPack("Research", Now)!;
        Assert.Null(library.AddReadingItem(pack.Id, "  ", Now));
        Assert.Null(library.AddReadingItem(pack.Id, "two words", Now));
        Assert.Null(library.AddReadingItem("no-such-pack", "entry-1", Now));
        Assert.Empty(pack.Items);
    }

    [Fact]
    public void Sanitize_ReadingItem_KeepsTheIdAndClearsOtherKindsFields()
    {
        var library = new ContextPackLibrary();
        var pack = library.AddPack("Research", Now)!;
        pack.Items.Add(new ContextPackItem
        {
            Id = "item-1",
            Kind = ContextPackItemKind.Reading,
            ReadingEntryId = "entry-1",
            Path = @"C:\smuggled.txt",
            Title = "smuggled",
            Text = "smuggled",
            SnippetId = "smuggled",
        });
        pack.Items.Add(new ContextPackItem
        {
            Id = "item-2",
            Kind = ContextPackItemKind.Reading,
            ReadingEntryId = "not a valid id",
        });
        library.Sanitize();
        var kept = Assert.Single(pack.Items);
        Assert.Equal("entry-1", kept.ReadingEntryId);
        Assert.Equal("", kept.Path);
        Assert.Equal("", kept.Title);
        Assert.Equal("", kept.Text);
        Assert.Equal("", kept.SnippetId);
    }

    [Fact]
    public void Compose_ReadingItem_ResolvesItsTextUnderTheEntrysTitle()
    {
        var library = new ContextPackLibrary();
        var pack = library.AddPack("Research", Now)!;
        library.AddReadingItem(pack.Id, "entry-1", Now);
        var entry = Entry("entry-1", "Release notes");
        var composed = ContextPackComposer.Compose(
            pack, _ => null, _ => null, Labels,
            id => id == "entry-1" ? entry : null,
            _ => "The page's own words.");
        Assert.Equal(
            "Context pack: Research\n\nSaved page: Release notes\n\nThe page's own words.",
            composed);
    }

    [Fact]
    public void Compose_ReadingItem_TextIsCappedPerItemLikeAFile()
    {
        var library = new ContextPackLibrary();
        var pack = library.AddPack("Research", Now)!;
        library.AddReadingItem(pack.Id, "entry-1", Now);
        var entry = Entry("entry-1", "Long page");
        var longText = new string('a', ContextPackComposer.MaxFileTextChars + 500);
        var composed = ContextPackComposer.Compose(
            pack, _ => null, _ => null, Labels,
            _ => entry, _ => longText);
        Assert.NotNull(composed);
        Assert.Contains(Labels.ItemCutNote, composed);
        Assert.DoesNotContain(new string('a', ContextPackComposer.MaxFileTextChars + 1), composed);
    }

    [Fact]
    public void Compose_ReadingItem_GoneEntry_BecomesTheMissingPlaceholder()
    {
        var library = new ContextPackLibrary();
        var pack = library.AddPack("Research", Now)!;
        library.AddReadingItem(pack.Id, "entry-gone", Now);
        var composed = ContextPackComposer.Compose(
            pack, _ => null, _ => null, Labels,
            _ => null, _ => "text that must not be used");
        Assert.Equal(
            "Context pack: Research\n\n"
            + "Saved page: no longer in the reading list\n\n"
            + "This saved page could not be included because it is no longer in the reading list.",
            composed);
    }

    [Fact]
    public void Compose_ReadingItem_UnresolvableText_BecomesTheUnreadablePlaceholder()
    {
        var library = new ContextPackLibrary();
        var pack = library.AddPack("Research", Now)!;
        library.AddReadingItem(pack.Id, "entry-1", Now);
        var entry = Entry("entry-1", "Release notes");
        var composed = ContextPackComposer.Compose(
            pack, _ => null, _ => null, Labels,
            _ => entry, _ => null);
        Assert.Equal(
            "Context pack: Research\n\nSaved page: Release notes\n\n(saved page Release notes could not be read)",
            composed);
    }

    [Fact]
    public void Compose_ReadingItem_NoResolversSupplied_PlaceholdersInsteadOfFailing()
    {
        // A caller that knows nothing of reading items (an older
        // compose path) still gets a composed pack, with the
        // honest placeholder where the page would have been.
        var library = new ContextPackLibrary();
        var pack = library.AddPack("Research", Now)!;
        library.AddReadingItem(pack.Id, "entry-1", Now);
        var composed = ContextPackComposer.Compose(pack, _ => null, _ => null, Labels);
        Assert.NotNull(composed);
        Assert.Contains("no longer in the reading list", composed);
    }

    [Fact]
    public void Compose_ReadingItem_ResolverThatThrows_PlaceholdersInsteadOfFailing()
    {
        var library = new ContextPackLibrary();
        var pack = library.AddPack("Research", Now)!;
        library.AddReadingItem(pack.Id, "entry-1", Now);
        var entry = Entry("entry-1", "Release notes");
        var composed = ContextPackComposer.Compose(
            pack, _ => null, _ => null, Labels,
            _ => entry,
            _ => throw new InvalidOperationException("extraction failed"));
        Assert.NotNull(composed);
        Assert.Contains("(saved page Release notes could not be read)", composed);
    }
}
