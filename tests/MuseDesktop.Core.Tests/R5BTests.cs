using MuseDesktop.Core;
using Xunit;

namespace MuseDesktop.Core.Tests;

/// <summary>
/// R5B (v1.6.0, items 201–206): the conversation tools — visible
/// scrollbars, message bookmarks (SEAN's design: in-app only, a
/// nickname is REQUIRED), conversation export, composer drafts,
/// reading comfort, and the prompt snippet library. The invariants
/// that matter most: no unnamed bookmark can exist on any path, no
/// store can hold a full page URL (routes only), draft and snippet
/// text is the user's own words and is treated as data (never
/// logged, capped, per profile), the page bridge refuses malformed
/// input instead of throwing, and the injected CSS is exactly what
/// the profile asks for — nothing at page defaults.
/// </summary>
public class R5BTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 26, 12, 0, 0, TimeSpan.FromHours(-5));
    private const string ChatUrl = "https://muse.ai/chat/abc-123?token=secret#frag";
    private const string ChatRoute = "/chat/abc-123";

    private sealed class TempDir : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "md-r5b-" + Guid.NewGuid().ToString("N"));

        public TempDir() => Directory.CreateDirectory(Path);

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); }
            catch { /* temp cleanup is best-effort */ }
        }
    }

    // ─────────────────────── reading comfort (#205) ───────────────────────

    [Fact]
    public void ReadingComfort_ClampsRanges_AndZeroMeansPageDefault()
    {
        Assert.Equal(50, ReadingComfort.ClampTextScale(10));
        Assert.Equal(100, ReadingComfort.ClampTextScale(100));
        Assert.Equal(200, ReadingComfort.ClampTextScale(999));
        Assert.Equal(0, ReadingComfort.ClampColumnWidth(0));
        Assert.Equal(0, ReadingComfort.ClampColumnWidth(-5));
        Assert.Equal(480, ReadingComfort.ClampColumnWidth(100));
        Assert.Equal(1600, ReadingComfort.ClampColumnWidth(99_999));
        Assert.Equal(0, ReadingComfort.ClampLineSpacing(0));
        Assert.Equal(100, ReadingComfort.ClampLineSpacing(50));
        Assert.Equal(200, ReadingComfort.ClampLineSpacing(400));
        Assert.True(ReadingComfort.IsDefault(100, 0, 0));
        Assert.False(ReadingComfort.IsDefault(110, 0, 0));
        Assert.False(ReadingComfort.IsDefault(100, 720, 0));
        Assert.False(ReadingComfort.IsDefault(100, 0, 125));
    }

    [Fact]
    public void PageStyleCss_ScrollbarOnly_AtComfortDefaults()
    {
        var css = PageStyleCss.Build(new AccountProfile());
        Assert.Contains("::-webkit-scrollbar", css);
        Assert.Contains("width: 12px", css);
        Assert.DoesNotContain("font-size", css);
        Assert.DoesNotContain("max-width", css);
        Assert.DoesNotContain("line-height", css);
    }

    [Fact]
    public void PageStyleCss_Empty_WhenScrollbarsOffAndComfortDefault()
    {
        var profile = new AccountProfile { ConversationScrollbars = false };
        Assert.Equal("", PageStyleCss.Build(profile));
        Assert.Equal("", PageStyleCss.Build(null));
    }

    [Fact]
    public void PageStyleCss_ComfortRules_OnlyForMovedValues_AndClamped()
    {
        var profile = new AccountProfile
        {
            ConversationScrollbars = false,
            ChatTextScalePercent = 125,
            ReadingColumnWidthPx = 720,
            ChatLineSpacingPercent = 150,
        };
        var css = PageStyleCss.Build(profile);
        Assert.DoesNotContain("webkit-scrollbar", css);
        Assert.Contains("font-size: 125%", css);
        Assert.Contains("max-width: 720px", css);
        Assert.Contains("line-height: 1.50", css);

        // A hand-edited profile asking for nonsense gets the clamps.
        var wild = new AccountProfile
        {
            ConversationScrollbars = false,
            ChatTextScalePercent = 900,
            ReadingColumnWidthPx = 42,
            ChatLineSpacingPercent = 999,
        };
        var clamped = PageStyleCss.Build(wild);
        Assert.Contains("font-size: 200%", clamped);
        Assert.Contains("max-width: 480px", clamped);
        Assert.Contains("line-height: 2.00", clamped);
    }

    [Fact]
    public void Profile_EnsureValid_ClampsComfort_AndCopiesAndBundleCarryIt()
    {
        var settings = new AppSettings();
        settings.Profiles.Add(new AccountProfile
        {
            Id = "work",
            Name = "Work",
            ChatTextScalePercent = 500,
            ReadingColumnWidthPx = 10,
            ChatLineSpacingPercent = 999,
            ConversationScrollbars = false,
        });
        Profiles.EnsureValid(settings);
        var work = settings.Profiles.Single(p => p.Id == "work");
        Assert.Equal(200, work.ChatTextScalePercent);
        Assert.Equal(480, work.ReadingColumnWidthPx);
        Assert.Equal(200, work.ChatLineSpacingPercent);
        Assert.False(work.ConversationScrollbars);

        var copy = Profiles.CopySettingsFrom(work, "copy", "Copy");
        Assert.Equal(200, copy.ChatTextScalePercent);
        Assert.False(copy.ConversationScrollbars);

        var text = ProfileBundle.Export(work, null);
        Assert.True(ProfileBundle.TryImport(text, null, out var imported, out var error), error);
        Assert.Equal(200, imported!.ChatTextScalePercent);
        Assert.Equal(480, imported.ReadingColumnWidthPx);
        Assert.False(imported.ConversationScrollbars);
    }

    // ─────────────────────── the nickname rule (#202) ───────────────────────

    [Fact]
    public void Bookmarks_NicknameIsRequired_OnEveryPath()
    {
        Assert.Null(MessageBookmarks.CleanNickname(null));
        Assert.Null(MessageBookmarks.CleanNickname(""));
        Assert.Null(MessageBookmarks.CleanNickname("   "));
        Assert.Equal("My mark", MessageBookmarks.CleanNickname("  My   mark "));

        var bookmarks = new MessageBookmarks();
        // No nickname — no bookmark, however good the rest is.
        Assert.Null(bookmarks.Add("p1", null, "Chat", ChatUrl, "hello", Now));
        Assert.Null(bookmarks.Add("p1", "   ", "Chat", ChatUrl, "hello", Now));
        Assert.Empty(bookmarks.EntriesFor("p1"));

        var saved = bookmarks.Add("p1", "Key answer", "Chat", ChatUrl, "hello", Now);
        Assert.NotNull(saved);
        // Rename to nothing refuses and keeps the old name.
        Assert.False(bookmarks.Rename("p1", saved!.Id, "  "));
        Assert.Equal("Key answer", bookmarks.Find("p1", saved.Id)!.Nickname);
        Assert.True(bookmarks.Rename("p1", saved.Id, "Renamed"));
        Assert.Equal("Renamed", bookmarks.Find("p1", saved.Id)!.Nickname);
    }

    [Fact]
    public void Bookmarks_StoreRoutesOnly_AndCleanTheirText()
    {
        var bookmarks = new MessageBookmarks();
        var saved = bookmarks.Add("p1", "Mark", "  My   Chat  ", ChatUrl,
            "  the   answer\nspans lines  ", Now);
        Assert.NotNull(saved);
        Assert.Equal(ChatRoute, saved!.Route);          // query + fragment are gone
        Assert.Equal("My Chat", saved.ConversationTitle);
        Assert.Equal("the answer spans lines", saved.Snippet);
        // Non-Muse pages and unroutable values refuse.
        Assert.Null(bookmarks.Add("p1", "X", "T", "https://example.com/chat/1", "s", Now));
        Assert.Null(bookmarks.Add("p1", "X", "T", null, "s", Now));
        Assert.Single(bookmarks.EntriesFor("p1"));
        // Per-profile isolation.
        Assert.Empty(bookmarks.EntriesFor("p2"));
    }

    [Fact]
    public void Bookmarks_SanitizeDropsUnnamedAndUnroutable()
    {
        var bookmarks = new MessageBookmarks
        {
            ByProfile = new()
            {
                ["p1"] = new List<MessageBookmark>
                {
                    new() { Id = "a", Nickname = "Good", Route = "/chat/1", AddedUtc = Now },
                    new() { Id = "b", Nickname = "  ", Route = "/chat/2", AddedUtc = Now },
                    new() { Id = "c", Nickname = "Bad route", Route = "https://evil.example/x", AddedUtc = Now },
                    new() { Id = "", Nickname = "No id", Route = "/chat/3", AddedUtc = Now },
                },
            },
        };
        bookmarks.Sanitize();
        var kept = bookmarks.EntriesFor("p1");
        Assert.Single(kept);
        Assert.Equal("Good", kept[0].Nickname);
    }

    [Fact]
    public void Bookmarks_StoreRoundTrips_AndRemoveWorks()
    {
        using var dir = new TempDir();
        var path = System.IO.Path.Combine(dir.Path, "bookmarks.json");
        var bookmarks = new MessageBookmarks();
        var saved = bookmarks.Add("p1", "First", "Chat", ChatUrl, "snippet", Now)!;
        bookmarks.Add("p1", "Second", "Chat", "https://muse.ai/projects", "other", Now.AddMinutes(1));
        MessageBookmarkStore.Save(path, bookmarks);

        var loaded = MessageBookmarkStore.Load(path);
        Assert.Equal(2, loaded.EntriesFor("p1").Count);
        Assert.Equal("Second", loaded.EntriesFor("p1")[0].Nickname); // newest first
        Assert.True(loaded.Remove("p1", saved.Id));
        Assert.False(loaded.Remove("p1", saved.Id));
        Assert.Single(loaded.EntriesFor("p1"));

        // A corrupt file loads as empty, never throws.
        File.WriteAllText(path, "{not json");
        Assert.Empty(MessageBookmarkStore.Load(path).EntriesFor("p1"));
    }

    [Fact]
    public void Bookmarks_PaletteProvider_UsesNicknameAndIdPayload()
    {
        var bookmarks = new MessageBookmarks();
        var saved = bookmarks.Add("p1", "Key answer", "My Chat", ChatUrl, "s", Now)!;
        var commands = new MessageBookmarksPaletteProvider(bookmarks.EntriesFor("p1"))
            .GetCommands().ToList();
        var command = Assert.Single(commands);
        Assert.Equal("Key answer", command.Title);
        Assert.Equal(PaletteCommandSource.Bookmark, command.Source);
        Assert.Equal(saved.Id, command.Payload);
        Assert.Contains("My Chat", command.Detail);
        // The action history refuses it like every payload source.
        Assert.False(new ActionHistory().Record(command));
    }

    // ─────────────────────── the page bridge ───────────────────────

    [Fact]
    public void PageBridge_ParsesKnownShapes_AndCapsText()
    {
        var bookmark = PageBridge.TryParse("{\"type\":\"md-bookmark\",\"text\":\"hello\"}");
        Assert.Equal(PageBridgeKind.Bookmark, bookmark!.Kind);
        Assert.Equal("hello", bookmark.Text);
        Assert.Equal(PageBridgeKind.Ready, PageBridge.TryParse("{\"type\":\"md-ready\"}")!.Kind);
        Assert.Equal(PageBridgeKind.DraftSent, PageBridge.TryParse("{\"type\":\"md-draft-sent\"}")!.Kind);
        var draft = PageBridge.TryParse("{\"type\":\"md-draft\",\"text\":\"typing…\"}");
        Assert.Equal(PageBridgeKind.Draft, draft!.Kind);
        Assert.Equal("typing…", draft.Text);

        var longText = new string('x', PageBridge.MaxSnippetLength + 500);
        var capped = PageBridge.TryParse($"{{\"type\":\"md-bookmark\",\"text\":\"{longText}\"}}");
        Assert.Equal(PageBridge.MaxSnippetLength, capped!.Text.Length);
    }

    [Fact]
    public void PageBridge_RefusesAnythingElse_WithoutThrowing()
    {
        Assert.Null(PageBridge.TryParse(null));
        Assert.Null(PageBridge.TryParse(""));
        Assert.Null(PageBridge.TryParse("not json"));
        Assert.Null(PageBridge.TryParse("[1,2]"));
        Assert.Null(PageBridge.TryParse("{\"type\":\"md-other\"}"));
        Assert.Null(PageBridge.TryParse("{\"text\":\"no type\"}"));
        Assert.Null(PageBridge.TryParse("{\"type\":42}"));
        // A known type with a non-string text reads as empty text.
        Assert.Equal("", PageBridge.TryParse("{\"type\":\"md-draft\",\"text\":42}")!.Text);
    }

    // ─────────────────────── drafts (#204) ───────────────────────

    [Fact]
    public void Drafts_SetGetRemove_AndEmptyTextClears()
    {
        var drafts = new ConversationDrafts();
        Assert.True(drafts.Set("p1", ChatRoute, "half-written…", Now));
        Assert.Equal("half-written…", drafts.Get("p1", ChatRoute));
        Assert.False(drafts.Set("p1", ChatRoute, "half-written…", Now.AddMinutes(1))); // unchanged
        Assert.True(drafts.Set("p1", ChatRoute, "edited", Now.AddMinutes(2)));
        Assert.Equal("edited", drafts.Get("p1", ChatRoute));

        // Sending looks like the composer emptying — the draft goes.
        Assert.True(drafts.Set("p1", ChatRoute, "", Now.AddMinutes(3)));
        Assert.Null(drafts.Get("p1", ChatRoute));
        Assert.Equal(0, drafts.CountFor("p1"));

        // Unroutable and other-profile drafts never land.
        Assert.False(drafts.Set("p1", "not a route", "text", Now));
        Assert.False(drafts.Set("p1", "//evil", "text", Now));
        drafts.Set("p2", ChatRoute, "other profile", Now);
        Assert.Null(drafts.Get("p1", ChatRoute));
        Assert.Equal("other profile", drafts.Get("p2", ChatRoute));
        Assert.True(drafts.Remove("p2", ChatRoute));
    }

    [Fact]
    public void Drafts_CapPrunesStalest_AndSanitizeDropsEmpties()
    {
        var drafts = new ConversationDrafts();
        for (var i = 0; i < ConversationDrafts.MaxPerProfile + 5; i++)
            drafts.Set("p1", $"/chat/{i}", $"draft {i}", Now.AddMinutes(i));
        Assert.Equal(ConversationDrafts.MaxPerProfile, drafts.CountFor("p1"));
        Assert.Null(drafts.Get("p1", "/chat/0")); // stalest pruned
        Assert.Equal("draft 54", drafts.Get("p1", "/chat/54"));

        var messy = new ConversationDrafts
        {
            ByProfile = new()
            {
                ["p1"] = new List<ConversationDraft>
                {
                    new() { Route = "/chat/1", Text = "", UpdatedUtc = Now },
                    new() { Route = "https://evil.example", Text = "x", UpdatedUtc = Now },
                    new() { Route = "/chat/2", Text = "kept", UpdatedUtc = Now },
                    new() { Route = "/chat/2", Text = "fresher dupe", UpdatedUtc = Now.AddHours(1) },
                },
            },
        };
        messy.Sanitize();
        Assert.Equal(1, messy.CountFor("p1"));
        Assert.Equal("fresher dupe", messy.Get("p1", "/chat/2"));
    }

    [Fact]
    public void Drafts_StoreRoundTrips()
    {
        using var dir = new TempDir();
        var path = System.IO.Path.Combine(dir.Path, "drafts.json");
        var drafts = new ConversationDrafts();
        drafts.Set("p1", ChatRoute, "my unsent words", Now);
        ConversationDraftStore.Save(path, drafts);
        var loaded = ConversationDraftStore.Load(path);
        Assert.Equal("my unsent words", loaded.Get("p1", ChatRoute));
        File.WriteAllText(path, "garbage{");
        Assert.Equal(0, ConversationDraftStore.Load(path).CountFor("p1"));
    }

    // ─────────────────────── snippets (#206) ───────────────────────

    [Fact]
    public void Snippets_RequireNameAndBody_AndSortByName()
    {
        var snippets = new PromptSnippets();
        Assert.Null(snippets.Add("p1", null, "body", Now));
        Assert.Null(snippets.Add("p1", "  ", "body", Now));
        Assert.Null(snippets.Add("p1", "Name", "   ", Now));
        Assert.Null(snippets.Add("p1", "Name", new string('x', PromptSnippets.MaxBodyLength + 1), Now));

        var b = snippets.Add("p1", "Beta", "second body", Now)!;
        snippets.Add("p1", "alpha", "first body", Now);
        var names = snippets.EntriesFor("p1").Select(s => s.Name).ToList();
        Assert.Equal(new[] { "alpha", "Beta" }, names);

        // Update validates the same way; a bad edit changes nothing.
        Assert.False(snippets.Update("p1", b.Id, "", "new body", Now));
        Assert.Equal("second body", snippets.Find("p1", b.Id)!.Body);
        Assert.True(snippets.Update("p1", b.Id, "Beta v2", "new body", Now.AddMinutes(1)));
        Assert.Equal("new body", snippets.Find("p1", b.Id)!.Body);
        Assert.True(snippets.Remove("p1", b.Id));
        Assert.Single(snippets.EntriesFor("p1"));
    }

    [Fact]
    public void Snippets_StoreRoundTrips_AndPaletteProviderResolvesById()
    {
        using var dir = new TempDir();
        var path = System.IO.Path.Combine(dir.Path, "snippets.json");
        var snippets = new PromptSnippets();
        var saved = snippets.Add("p1", "Review", "Review this for bugs:", Now)!;
        PromptSnippetStore.Save(path, snippets);

        var loaded = PromptSnippetStore.Load(path);
        var commands = new PromptSnippetsPaletteProvider(loaded.EntriesFor("p1"))
            .GetCommands().ToList();
        var command = Assert.Single(commands);
        Assert.Equal(PaletteCommandSource.Snippet, command.Source);
        Assert.Equal(saved.Id, command.Payload);
        Assert.Contains("Review", command.Title);
        Assert.False(new ActionHistory().Record(command));
    }

    // ─────────────────────── export (#203) ───────────────────────

    [Fact]
    public void Export_ParsesExtraction_AndRendersTidyMarkdown()
    {
        var json = "[{\"role\":\"user\",\"text\":\"Question?\"}," +
                   "{\"role\":\"assistant\",\"text\":\"Answer with `code` and\\n\\n```\\nblock\\n```\"}," +
                   "{\"role\":\"user\",\"text\":\"  \"}]";
        var messages = ConversationExport.TryParseExtraction(json);
        Assert.NotNull(messages);
        Assert.Equal(2, messages!.Count); // the empty one drops

        var md = ConversationExport.RenderMarkdown("My Chat", Now, messages);
        Assert.StartsWith("# My Chat", md);
        Assert.Contains("## You", md);
        Assert.Contains("## Muse", md);
        Assert.Contains("```\nblock\n```", md); // code blocks ride verbatim
        Assert.Contains("2026-10-26", md);
    }

    [Fact]
    public void Export_RefusesGarbage_AndNamesFilesSafely()
    {
        Assert.Null(ConversationExport.TryParseExtraction(null));
        Assert.Null(ConversationExport.TryParseExtraction("not json"));
        Assert.Null(ConversationExport.TryParseExtraction("{\"role\":\"user\"}"));
        Assert.Equal("You", ConversationExport.RoleHeading("user"));
        Assert.Equal("Muse", ConversationExport.RoleHeading("ASSISTANT"));
        Assert.Equal("Message", ConversationExport.RoleHeading(""));
        Assert.Equal("System", ConversationExport.RoleHeading("system"));

        var name = ConversationExport.SuggestFileName("Ideas: v2 / draft?", Now, ".md");
        Assert.DoesNotContain(":", name);
        Assert.DoesNotContain("/", name);
        Assert.EndsWith(" — 2026-10-26.md", name);
        Assert.Equal("muse-conversation — 2026-10-26.pdf",
            ConversationExport.SuggestFileName("???", Now, ".pdf"));
        var longName = ConversationExport.SuggestFileName(new string('a', 300), Now, ".md");
        Assert.True(longName.Length <= ConversationExport.MaxFileNameLength + ".md".Length);
    }

    // ─────────────────────── palette assembly + settings ───────────────────────

    [Fact]
    public void Palette_CreateDefault_IncludesBookmarksAndSnippetsWhenGiven()
    {
        var bookmarks = new MessageBookmarks();
        bookmarks.Add("p1", "Mark", "Chat", ChatUrl, "s", Now);
        var snippets = new PromptSnippets();
        snippets.Add("p1", "Review", "body", Now);

        var palette = CommandRegistry.CreateDefault(
            Array.Empty<AccountProfile>(), "default",
            Array.Empty<DownloadRecord>(), Array.Empty<WindowDescriptor>(),
            bookmarks: bookmarks.EntriesFor("p1"),
            snippets: snippets.EntriesFor("p1"));
        var all = palette.All();
        Assert.Contains(all, c => c.Source == PaletteCommandSource.Bookmark);
        Assert.Contains(all, c => c.Source == PaletteCommandSource.Snippet);
        Assert.Contains(all, c => c.Id == "action.bookmarks");
        Assert.Contains(all, c => c.Id == "action.export-conversation");

        // Without the inputs, those sections simply don't appear.
        var bare = CommandRegistry.CreateDefault(
            Array.Empty<AccountProfile>(), "default",
            Array.Empty<DownloadRecord>(), Array.Empty<WindowDescriptor>());
        Assert.DoesNotContain(bare.All(), c => c.Source == PaletteCommandSource.Bookmark);
        Assert.DoesNotContain(bare.All(), c => c.Source == PaletteCommandSource.Snippet);
    }

    [Fact]
    public void Settings_R5BDefaults_AndResetRestoresDraftsToggle()
    {
        var settings = new AppSettings();
        Assert.True(settings.DraftsAutosaveEnabled);
        var profile = settings.Profiles[0];
        Assert.True(profile.ConversationScrollbars);
        Assert.Equal(100, profile.ChatTextScalePercent);
        Assert.Equal(0, profile.ReadingColumnWidthPx);
        Assert.Equal(0, profile.ChatLineSpacingPercent);

        settings.DraftsAutosaveEnabled = false;
        SettingsReset.ApplyDefaults(settings);
        Assert.True(settings.DraftsAutosaveEnabled);
    }
}
