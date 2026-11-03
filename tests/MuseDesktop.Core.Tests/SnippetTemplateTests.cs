using MuseDesktop.Core;
using Xunit;

namespace MuseDesktop.Core.Tests;

// ───────────────────── Snippet templates: {{blank}} fields + fill ─────────────────────

/// <summary>
/// The snippet-template rules (v2.1.0): a blank is {{ name }} with
/// optional inner spaces; the name is non-empty once trimmed, at most
/// 40 characters, brace- and newline-free. Malformed candidates are
/// plain text. The same name without case is one field, asked once
/// and filled everywhere. A fill with any blank missing (or blank
/// after trimming) fails whole — the body comes back untouched with
/// the missing names listed — so the shell can never insert a
/// half-finished snippet.
/// </summary>
public class SnippetTemplateFieldTests
{
    [Fact]
    public void Fields_InFirstAppearanceOrder()
    {
        var fields = SnippetTemplate.Fields("Explain {{topic}} to a {{audience}} about {{topic}}");
        Assert.Equal(new[] { "topic", "audience" }, fields);
    }

    [Fact]
    public void Fields_DedupeIsCaseInsensitiveAndKeepsFirstSpelling()
    {
        var fields = SnippetTemplate.Fields("{{Topic}} then {{topic}} then {{TOPIC}}");
        Assert.Equal(new[] { "Topic" }, fields);
    }

    [Fact]
    public void Fields_TolerateInnerWhitespace()
    {
        var fields = SnippetTemplate.Fields("{{topic}} {{ topic }} {{\ttopic\t}} {{  two words  }}");
        Assert.Equal(new[] { "topic", "two words" }, fields);
    }

    [Theory]
    [InlineData("{{}}")]
    [InlineData("{{ }}")]
    [InlineData("{{   }}")]
    [InlineData("{{unclosed")]
    [InlineData("no blanks at all")]
    [InlineData("{{a}b}}")] // a brace inside the name — not a field
    [InlineData("{{line\nbreak}}")] // a newline inside the name — not a field
    public void Fields_MalformedCandidatesAreIgnored(string body)
    {
        Assert.Empty(SnippetTemplate.Fields(body));
        Assert.False(SnippetTemplate.HasFields(body));
    }

    [Fact]
    public void Fields_NameLengthCap()
    {
        var longest = new string('a', SnippetTemplate.MaxFieldNameLength);
        var tooLong = new string('a', SnippetTemplate.MaxFieldNameLength + 1);
        Assert.Equal(new[] { longest }, SnippetTemplate.Fields("{{" + longest + "}}"));
        Assert.Empty(SnippetTemplate.Fields("{{" + tooLong + "}}"));
    }

    [Fact]
    public void Fields_CappedAtMaxFields()
    {
        var body = string.Join(" ", Enumerable.Range(1, SnippetTemplate.MaxFields + 5)
            .Select(n => "{{f" + n + "}}"));
        var fields = SnippetTemplate.Fields(body);
        Assert.Equal(SnippetTemplate.MaxFields, fields.Count);
        Assert.Equal("f1", fields[0]);
        Assert.Equal("f" + SnippetTemplate.MaxFields, fields[^1]);
    }

    [Fact]
    public void Fields_NullAndEmptyBodiesHaveNone()
    {
        Assert.Empty(SnippetTemplate.Fields(null));
        Assert.Empty(SnippetTemplate.Fields(""));
        Assert.False(SnippetTemplate.HasFields(null));
    }

    [Fact]
    public void HasFields_TrueForARealBlank()
    {
        Assert.True(SnippetTemplate.HasFields("Write about {{topic}}"));
    }
}

public class SnippetTemplateFillTests
{
    private static Dictionary<string, string> Values(params (string Key, string Value)[] pairs) =>
        pairs.ToDictionary(p => p.Key, p => p.Value);

    [Fact]
    public void Fill_ReplacesEveryOccurrence()
    {
        var ok = SnippetTemplate.TryFill(
            "{{topic}} is great. I love {{topic}}!",
            Values(("topic", "Rust")),
            out var filled, out var missing);
        Assert.True(ok);
        Assert.Equal("Rust is great. I love Rust!", filled);
        Assert.Empty(missing);
    }

    [Fact]
    public void Fill_KeyMatchIsCaseInsensitive()
    {
        var ok = SnippetTemplate.TryFill(
            "Dear {{Name}}, hello {{name}}",
            Values(("NAME", "Jo")),
            out var filled, out _);
        Assert.True(ok);
        Assert.Equal("Dear Jo, hello Jo", filled);
    }

    [Fact]
    public void Fill_ValuesAreTrimmed()
    {
        var ok = SnippetTemplate.TryFill(
            "[{{topic}}]",
            Values(("topic", "   Rust\t")),
            out var filled, out _);
        Assert.True(ok);
        Assert.Equal("[Rust]", filled);
    }

    [Fact]
    public void Fill_MissingValueFailsWithMissingListAndOriginalBody()
    {
        const string body = "{{topic}} for {{audience}}";
        var ok = SnippetTemplate.TryFill(body, Values(("topic", "Rust")),
            out var filled, out var missing);
        Assert.False(ok);
        Assert.Equal(new[] { "audience" }, missing);
        Assert.Equal(body, filled);
    }

    [Fact]
    public void Fill_BlankAndWhitespaceValuesCountAsMissing()
    {
        const string body = "{{a}} {{b}} {{c}}";
        var ok = SnippetTemplate.TryFill(body,
            Values(("a", ""), ("b", "   "), ("c", "ok")),
            out var filled, out var missing);
        Assert.False(ok);
        Assert.Equal(new[] { "a", "b" }, missing);
        Assert.Equal(body, filled);
    }

    [Fact]
    public void Fill_MissingListKeepsFieldOrderAndFirstSpelling()
    {
        var ok = SnippetTemplate.TryFill("{{Zulu}} {{alpha}} {{ZULU}}",
            Values(), out _, out var missing);
        Assert.False(ok);
        Assert.Equal(new[] { "Zulu", "alpha" }, missing);
    }

    [Fact]
    public void Fill_PlainBodyFillsToItself()
    {
        var ok = SnippetTemplate.TryFill("just text, no blanks", Values(),
            out var filled, out var missing);
        Assert.True(ok);
        Assert.Equal("just text, no blanks", filled);
        Assert.Empty(missing);
    }

    [Fact]
    public void Fill_MalformedCandidatesAreLeftAlone()
    {
        var ok = SnippetTemplate.TryFill("{{}} and {{real}} and {{unclosed",
            Values(("real", "yes")), out var filled, out _);
        Assert.True(ok);
        Assert.Equal("{{}} and yes and {{unclosed", filled);
    }

    [Fact]
    public void Fill_NonPlaceholderTextIsNeverAltered()
    {
        var ok = SnippetTemplate.TryFill("  spaced  {{a}}  text }} {{ ",
            Values(("a", "X")), out var filled, out _);
        Assert.True(ok);
        Assert.Equal("  spaced  X  text }} {{ ", filled);
    }

    [Fact]
    public void Fill_NullBodyFillsToEmpty()
    {
        var ok = SnippetTemplate.TryFill(null, Values(), out var filled, out var missing);
        Assert.True(ok);
        Assert.Equal("", filled);
        Assert.Empty(missing);
    }
}
