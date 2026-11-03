using MuseDesktop.Core;
using Xunit;

namespace MuseDesktop.Core.Tests;

// ───────────────────── Pinned conversations: model + store ─────────────────────

/// <summary>
/// The pinned-conversation rules: only Muse conversation
/// addresses pin (the PageContext route rule); one pin per
/// conversation, frozen at its pinned-time title; the 50-per-profile
/// cap refuses whole; per-profile lists are independent; and the
/// store round-trips through real JSON on disk with the house
/// corrupt-file contract (preserved as .corrupt-*, read as empty).
/// </summary>
public class PinnedConversationsTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 2, 12, 0, 0, TimeSpan.Zero);
    private const string ChatA = "https://muse.ai/chat/aaaaaaaa-1111";
    private const string ChatB = "https://muse.ai/chat/bbbbbbbb-2222";

    // ── pin / unpin ──────────────────────────────────────────────

    [Fact]
    public void Pin_AddsTheConversation_NewestFirst_WithItsStamps()
    {
        var pins = new PinnedConversations();
        var outcome = pins.Pin("default", "First chat", ChatA, Now, out var first);
        Assert.Equal(PinOutcome.Pinned, outcome);
        Assert.NotNull(first);
        Assert.Equal("First chat", first!.Title);
        Assert.Equal(ChatA, first.Url);
        Assert.Equal(Now, first.PinnedUtc);

        var later = Now.AddMinutes(5);
        Assert.Equal(PinOutcome.Pinned, pins.Pin("default", "Second chat", ChatB, later, out var second));
        Assert.Equal(new[] { second!.Id, first.Id },
            pins.EntriesFor("default").Select(p => p.Id).ToArray());
    }

    [Fact]
    public void Pin_StripsTheAppSuffix_AndCollapsesTheTitle()
    {
        var pins = new PinnedConversations();
        pins.Pin("default", "  My   chat\n title  — MuseDesktop", ChatA, Now, out var pin);
        Assert.Equal("My chat title", pin!.Title);
    }

    [Fact]
    public void Pin_AcceptsOnlyConversationAddresses()
    {
        var pins = new PinnedConversations();
        Assert.Equal(PinOutcome.NotAConversation, pins.Pin("default", "Home", "https://muse.ai/", Now, out _));
        Assert.Equal(PinOutcome.NotAConversation, pins.Pin("default", "Projects", "https://muse.ai/projects", Now, out _));
        Assert.Equal(PinOutcome.NotAConversation, pins.Pin("default", "Elsewhere", "https://example.com/chat/x", Now, out _));
        Assert.Equal(PinOutcome.NotAConversation, pins.Pin("default", "Http", "http://muse.ai/chat/x", Now, out _));
        Assert.Equal(PinOutcome.NotAConversation, pins.Pin("default", "Lookalike", "https://muse.ai.evil.com/chat/x", Now, out _));
        Assert.Equal(PinOutcome.NotAConversation, pins.Pin("default", "Junk", "not a url", Now, out _));
        Assert.Equal(PinOutcome.NotAConversation, pins.Pin("default", "None", null, Now, out _));
        Assert.Empty(pins.EntriesFor("default"));
    }

    [Fact]
    public void Pin_DropsTheQueryAndFragment_FromTheStoredAddress()
    {
        var pins = new PinnedConversations();
        pins.Pin("default", "Chat", ChatA + "?source=share#frag", Now, out var pin);
        Assert.Equal(ChatA, pin!.Url);
    }

    [Fact]
    public void Pin_AnAlreadyPinnedConversation_IsANoOp_ThatKeepsTheOriginalPin()
    {
        var pins = new PinnedConversations();
        pins.Pin("default", "Original title", ChatA, Now, out var first);
        var outcome = pins.Pin("default", "Renamed later", ChatA, Now.AddDays(1), out var again);
        Assert.Equal(PinOutcome.AlreadyPinned, outcome);
        Assert.Equal(first!.Id, again!.Id);
        Assert.Equal("Original title", again.Title);
        Assert.Equal(Now, again.PinnedUtc);
        Assert.Single(pins.EntriesFor("default"));
    }

    [Fact]
    public void Pin_AtTheCap_IsRefusedWhole()
    {
        var pins = new PinnedConversations();
        for (var i = 0; i < PinnedConversations.MaxPerProfile; i++)
            Assert.Equal(PinOutcome.Pinned,
                pins.Pin("default", $"Chat {i}", $"https://muse.ai/chat/{i}", Now.AddSeconds(i), out _));
        Assert.Equal(PinOutcome.Full, pins.Pin("default", "One too many", ChatA, Now, out var refused));
        Assert.Null(refused);
        Assert.Equal(PinnedConversations.MaxPerProfile, pins.EntriesFor("default").Count);
        // Unpinning makes room again.
        var oldest = pins.EntriesFor("default")[^1];
        Assert.True(pins.Unpin("default", oldest.Id));
        Assert.Equal(PinOutcome.Pinned, pins.Pin("default", "One too many", ChatA, Now, out _));
    }

    [Fact]
    public void Unpin_ByIdAndByUrl_AndIsPinned()
    {
        var pins = new PinnedConversations();
        pins.Pin("default", "Chat", ChatA, Now, out var pin);
        Assert.True(pins.IsPinned("default", ChatA));
        Assert.True(pins.UnpinByUrl("default", ChatA + "?ignored=1"));
        Assert.False(pins.IsPinned("default", ChatA));
        Assert.False(pins.UnpinByUrl("default", ChatA));

        pins.Pin("default", "Chat", ChatA, Now, out pin);
        Assert.True(pins.Unpin("default", pin!.Id));
        Assert.False(pins.Unpin("default", pin.Id));
        Assert.Empty(pins.EntriesFor("default"));
    }

    [Fact]
    public void Pins_ArePerProfile()
    {
        var pins = new PinnedConversations();
        pins.Pin("work", "Work chat", ChatA, Now, out _);
        Assert.Empty(pins.EntriesFor("default"));
        Assert.False(pins.IsPinned("default", ChatA));
        // The same conversation can be pinned under another profile.
        Assert.Equal(PinOutcome.Pinned, pins.Pin("default", "Same chat", ChatA, Now, out _));
        Assert.Single(pins.EntriesFor("work"));
        Assert.Single(pins.EntriesFor("default"));
    }

    [Fact]
    public void CleanTitle_CapsAFloodedTitle()
    {
        var pins = new PinnedConversations();
        pins.Pin("default", new string('a', 500), ChatA, Now, out var pin);
        Assert.Equal(PinnedConversations.MaxTitleLength, pin!.Title.Length);
    }

    // ── sanitize (load repair) ───────────────────────────────────

    [Fact]
    public void Sanitize_DropsUnusablePins_DedupesAndCaps()
    {
        var pins = new PinnedConversations
        {
            ByProfile = new Dictionary<string, List<PinnedConversation>>
            {
                ["default"] = new()
                {
                    new() { Id = "a", Title = "A", Url = ChatA, PinnedUtc = Now },
                    new() { Id = "a2", Title = "A dupe", Url = ChatA, PinnedUtc = Now.AddMinutes(-1) },
                    new() { Id = "", Title = "No id", Url = ChatB, PinnedUtc = Now },
                    new() { Id = "bad", Title = "Not a chat", Url = "https://muse.ai/projects", PinnedUtc = Now },
                    new() { Id = "b", Title = "B", Url = ChatB, PinnedUtc = Now.AddMinutes(-2) },
                },
            },
        };
        pins.Sanitize();
        var kept = pins.EntriesFor("default");
        Assert.Equal(new[] { "a", "b" }, kept.Select(p => p.Id).ToArray());
    }

    // ── store round-trip + corrupt handling ─────────────────────

    [Fact]
    public void Store_RoundTripsThroughARealFile()
    {
        var dir = Directory.CreateDirectory(
            Path.Combine(Path.GetTempPath(), "md-pins-" + Guid.NewGuid().ToString("N")));
        try
        {
            var path = Path.Combine(dir.FullName, "pins.json");
            var pins = new PinnedConversations();
            pins.Pin("default", "First chat", ChatA, Now, out var first);
            pins.Pin("work", "Work chat", ChatB, Now, out _);
            PinnedConversationStore.Save(path, pins);

            var loaded = PinnedConversationStore.Load(path);
            var back = Assert.Single(loaded.EntriesFor("default"));
            Assert.Equal(first!.Id, back.Id);
            Assert.Equal(first.Title, back.Title);
            Assert.Equal(first.Url, back.Url);
            Assert.Equal(first.PinnedUtc, back.PinnedUtc);
            Assert.Single(loaded.EntriesFor("work"));
        }
        finally
        {
            Directory.Delete(dir.FullName, recursive: true);
        }
    }

    [Fact]
    public void Store_MissingFile_IsAnEmptyList()
    {
        var path = Path.Combine(Path.GetTempPath(), "md-pins-none-" + Guid.NewGuid().ToString("N"), "pins.json");
        Assert.Empty(PinnedConversationStore.Load(path).EntriesFor("default"));
    }

    [Fact]
    public void Store_CorruptFile_IsPreservedAndReadsEmpty()
    {
        var dir = Directory.CreateDirectory(
            Path.Combine(Path.GetTempPath(), "md-pins-" + Guid.NewGuid().ToString("N")));
        try
        {
            var path = Path.Combine(dir.FullName, "pins.json");
            File.WriteAllText(path, "{ this is not a pin list");
            var loaded = PinnedConversationStore.Load(path);
            Assert.Empty(loaded.EntriesFor("default"));
            var preserved = Directory.GetFiles(dir.FullName, "pins.json.corrupt-*");
            Assert.Single(preserved);
            Assert.Equal("{ this is not a pin list", File.ReadAllText(preserved[0]));
        }
        finally
        {
            Directory.Delete(dir.FullName, recursive: true);
        }
    }

    [Fact]
    public void AppPaths_PinsLiveInTheirOwnFile()
    {
        Assert.EndsWith("pins.json", AppPaths.PinsPath);
        Assert.NotEqual(AppPaths.BookmarksPath, AppPaths.PinsPath);
        Assert.NotEqual(AppPaths.SettingsPath, AppPaths.PinsPath);
    }
}
