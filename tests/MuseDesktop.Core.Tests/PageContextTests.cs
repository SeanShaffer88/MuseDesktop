using MuseDesktop.Core;
using Xunit;

namespace MuseDesktop.Core.Tests;

// ───────────────────── Ask about this page: routes, parsing, compose ─────────────────────

/// <summary>
/// The page-context rules (v2.1.0): the command is offered on any
/// page that is NOT a Muse conversation (conversations live under
/// /chat on muse.ai exactly — Core's AppRoute knowledge); the
/// extractor's JSON answer parses defensively; the composed block
/// is a labelled header over the page text, capped at
/// MaxPageTextChars with a visible cut note, and whitespace text
/// composes to null — the shell's "nothing readable" stop.
/// </summary>
public class PageContextRouteTests
{
    [Theory]
    [InlineData("https://muse.ai/chat")]
    [InlineData("https://muse.ai/chat/abc123")]
    [InlineData("https://muse.ai/chat/abc123?ref=link#top")]
    public void ConversationRoutes_AreConversations(string url) =>
        Assert.True(PageContext.IsConversationRoute(url));

    [Theory]
    [InlineData("https://muse.ai/")]                 // home: a fresh conversation, not one in progress
    [InlineData("https://muse.ai/projects")]
    [InlineData("https://muse.ai/projects/abc")]
    [InlineData("https://muse.ai/chatroom")]         // a prefix is not the route
    [InlineData("https://example.com/chat/abc")]     // another host's chat is not Muse's
    [InlineData("https://sub.muse.ai/chat/abc")]     // not the exact host
    [InlineData("http://muse.ai/chat/abc")]          // not https
    [InlineData("about:blank")]
    [InlineData("not a url")]
    public void NonConversationRoutes_AreNotConversations(string url) =>
        Assert.False(PageContext.IsConversationRoute(url));

    [Fact]
    public void NullAndEmpty_AreNotConversations()
    {
        Assert.False(PageContext.IsConversationRoute(null));
        Assert.False(PageContext.IsConversationRoute(""));
        Assert.False(PageContext.IsConversationRoute("   "));
    }
}

public class PageContextParseTests
{
    [Fact]
    public void Parse_TitleAndText()
    {
        var page = PageContext.TryParseExtraction(
            "{\"title\":\"An article\",\"text\":\"First line.\\nSecond line.\"}");
        Assert.NotNull(page);
        Assert.Equal("An article", page.Title);
        Assert.Equal("First line.\nSecond line.", page.Text);
    }

    [Fact]
    public void Parse_MissingTitle_IsEmptyNotFailure()
    {
        var page = PageContext.TryParseExtraction("{\"text\":\"Body.\"}");
        Assert.NotNull(page);
        Assert.Equal("", page.Title);
        Assert.Equal("Body.", page.Text);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not json")]
    [InlineData("[1,2,3]")]                       // not an object
    [InlineData("{\"title\":\"No text here\"}")]  // text property required
    [InlineData("{\"text\":42}")]                 // text must be a string
    public void Parse_Junk_IsNull(string? json) =>
        Assert.Null(PageContext.TryParseExtraction(json));
}

public class PageContextComposeTests
{
    private const string Header = "Context from a page I was reading — “{0}” ({1}):";
    private const string HeaderNoTitle = "Context from a page I was reading ({0}):";
    private const string CutNote = "[…the rest of the page was left out — it was longer than fits here]";

    [Fact]
    public void Compose_HeaderNamesTitleAndHost_BodyPreserved()
    {
        var composed = PageContext.Compose(
            "The Long Read", "example.com", "First paragraph.\n\nSecond paragraph.",
            Header, HeaderNoTitle, CutNote);
        Assert.Equal(
            "Context from a page I was reading — “The Long Read” (example.com):\n\n" +
            "First paragraph.\n\nSecond paragraph.",
            composed);
    }

    [Fact]
    public void Compose_NoTitle_UsesTheNoTitleHeader()
    {
        var composed = PageContext.Compose("", "example.com", "Body.", Header, HeaderNoTitle, CutNote);
        Assert.Equal("Context from a page I was reading (example.com):\n\nBody.", composed);
    }

    [Fact]
    public void Compose_CutsAtTheCap_AndSaysSo()
    {
        var text = new string('a', PageContext.MaxPageTextChars + 5_000);
        var composed = PageContext.Compose("T", "h.example", text, Header, HeaderNoTitle, CutNote);
        Assert.NotNull(composed);
        var head = "Context from a page I was reading — “T” (h.example):";
        Assert.Equal(head + "\n\n" + new string('a', PageContext.MaxPageTextChars) + "\n\n" + CutNote, composed);
        // Total length stays bounded by header + cap + note + the two blank lines.
        Assert.True(composed.Length <= head.Length + PageContext.MaxPageTextChars + CutNote.Length + 4);
    }

    [Fact]
    public void Compose_ExactlyAtTheCap_IsNotCut()
    {
        var text = new string('b', PageContext.MaxPageTextChars);
        var composed = PageContext.Compose("T", "h.example", text, Header, HeaderNoTitle, CutNote);
        Assert.NotNull(composed);
        Assert.DoesNotContain(CutNote, composed);
        Assert.EndsWith(text, composed);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(" \n\t\r\n ")]
    public void Compose_NothingReadable_IsNull(string? text) =>
        Assert.Null(PageContext.Compose("T", "h.example", text, Header, HeaderNoTitle, CutNote));

    [Fact]
    public void Compose_TitleCleaning_StripsNewlinesAndCollapsesSpaces()
    {
        var composed = PageContext.Compose(
            "  A title\nwith\r\n  extra   space  ", "example.com", "Body.",
            Header, HeaderNoTitle, CutNote);
        Assert.NotNull(composed);
        Assert.StartsWith("Context from a page I was reading — “A title with extra space” (example.com):", composed);
    }

    [Fact]
    public void Compose_TitleCleaning_CapsLength()
    {
        var longTitle = new string('x', PageContext.MaxTitleChars + 50);
        var composed = PageContext.Compose(longTitle, "example.com", "Body.", Header, HeaderNoTitle, CutNote);
        Assert.NotNull(composed);
        Assert.Contains("“" + new string('x', PageContext.MaxTitleChars) + "”", composed);
        Assert.DoesNotContain(new string('x', PageContext.MaxTitleChars + 1), composed);
    }

    [Fact]
    public void Compose_HostCleaning_RemovesWhitespaceAndControl()
    {
        var composed = PageContext.Compose("T", "exa\nmple .com", "Body.", Header, HeaderNoTitle, CutNote);
        Assert.NotNull(composed);
        Assert.Contains("(example.com)", composed);
    }

    [Fact]
    public void Compose_NormalizesLineEndingsAndTrimsEdges()
    {
        var composed = PageContext.Compose("T", "h.example", "\r\nLine one.\r\nLine two.\r\n", Header, HeaderNoTitle, CutNote);
        Assert.NotNull(composed);
        Assert.EndsWith("Line one.\nLine two.", composed);
        Assert.DoesNotContain("\r", composed);
    }
}
