using System.Globalization;
using MuseDesktop.Core;
using Xunit;

namespace MuseDesktop.Core.Tests;

// ───────────────────── Voice in, voice out (v2.1.0) ─────────────────────
//
// The shell owns the engines (Windows recognition + synthesis);
// these tests pin what Core decides around them: the last answer
// is the newest assistant message; Markdown becomes speakable
// segments (headings flagged, code blocks announced but not read,
// links keep their words); an answer with nothing speakable yields
// nothing; and the five reading speeds map onto the synthesizer's
// rate scale in order, clamped at its ends.

public class SpeechTextTests
{
    private const string Marker = "Code block.";

    [Fact]
    public void LastAssistantAnswer_PicksTheNewestAssistantMessage()
    {
        var messages = new[]
        {
            new ExportedMessage("user", "First question"),
            new ExportedMessage("assistant", "First answer"),
            new ExportedMessage("user", "Second question"),
            new ExportedMessage("assistant", "Second answer"),
        };
        Assert.Equal("Second answer", SpeechText.LastAssistantAnswer(messages));
    }

    [Fact]
    public void LastAssistantAnswer_ATrailingUserMessageDoesNotReplaceTheAnswer()
    {
        var messages = new[]
        {
            new ExportedMessage("assistant", "The answer"),
            new ExportedMessage("user", "A follow-up still being answered"),
        };
        Assert.Equal("The answer", SpeechText.LastAssistantAnswer(messages));
    }

    [Fact]
    public void LastAssistantAnswer_NoneWhenNoAssistantMessage()
    {
        Assert.Null(SpeechText.LastAssistantAnswer(new[]
        {
            new ExportedMessage("user", "Only a question"),
            new ExportedMessage("", "An unjudged message"),
        }));
        Assert.Null(SpeechText.LastAssistantAnswer(Array.Empty<ExportedMessage>()));
        Assert.Null(SpeechText.LastAssistantAnswer(null));
    }

    [Fact]
    public void Segment_EmptyAnswer_YieldsNoSegments()
    {
        Assert.Empty(SpeechText.Segment(null, Marker));
        Assert.Empty(SpeechText.Segment("", Marker));
        Assert.Empty(SpeechText.Segment("  \n\n  ", Marker));
    }

    [Fact]
    public void Segment_HeadingSplitsAndIsFlagged()
    {
        var segments = SpeechText.Segment("## The short version\n\nIt works like this.", Marker);
        Assert.Equal(2, segments.Count);
        Assert.Equal(new SpeechSegment("The short version", IsHeading: true), segments[0]);
        Assert.Equal(new SpeechSegment("It works like this.", IsHeading: false), segments[1]);
    }

    [Fact]
    public void Segment_ParagraphsSplitOnBlankLines_AndLinesJoin()
    {
        var segments = SpeechText.Segment("First paragraph\ncontinues here.\n\nSecond paragraph.", Marker);
        Assert.Equal(2, segments.Count);
        Assert.Equal("First paragraph continues here.", segments[0].Text);
        Assert.Equal("Second paragraph.", segments[1].Text);
        Assert.All(segments, s => Assert.False(s.IsHeading));
    }

    [Fact]
    public void Segment_CodeFence_BecomesOneMarkerSegment_ContentsNotRead()
    {
        var segments = SpeechText.Segment(
            "Run this:\n\n```csharp\nvar x = 1;\nConsole.WriteLine(x);\n```\n\nThen check the output.",
            Marker);
        Assert.Equal(3, segments.Count);
        Assert.Equal("Run this:", segments[0].Text);
        Assert.Equal(new SpeechSegment(Marker, IsHeading: false), segments[1]);
        Assert.Equal("Then check the output.", segments[2].Text);
        Assert.DoesNotContain(segments, s => s.Text.Contains("Console"));
    }

    [Fact]
    public void Segment_UnterminatedCodeFence_StillAnnounced()
    {
        var segments = SpeechText.Segment("Here is the code:\n\n```\nlet x = 1", Marker);
        Assert.Equal(2, segments.Count);
        Assert.Equal(Marker, segments[1].Text);
    }

    [Fact]
    public void Segment_CodeOnlyAnswer_IsJustTheMarker()
    {
        var segments = SpeechText.Segment("```\ncode\n```", Marker);
        Assert.Equal(new[] { new SpeechSegment(Marker, IsHeading: false) }, segments);
    }

    [Fact]
    public void Segment_BlankMarker_SkipsTheAnnouncement()
    {
        var segments = SpeechText.Segment("Before.\n\n```\ncode\n```\n\nAfter.", "  ");
        Assert.Equal(2, segments.Count);
        Assert.Equal("Before.", segments[0].Text);
        Assert.Equal("After.", segments[1].Text);
    }

    [Fact]
    public void Segment_LinksKeepTheirWords_AddressesAndMarkupFallAway()
    {
        var segments = SpeechText.Segment(
            "See [the guide](https://example.com/guide) and ![a diagram](img.png) — it is **bold** and `code`-ish.",
            Marker);
        var text = Assert.Single(segments).Text;
        Assert.Contains("the guide", text);
        Assert.Contains("a diagram", text);
        Assert.DoesNotContain("example.com", text);
        Assert.DoesNotContain("**", text);
        Assert.DoesNotContain("`", text);
        Assert.Contains("code-ish", text);
    }

    [Fact]
    public void Segment_ListMarkersAndQuotesAreNotSpoken()
    {
        var segments = SpeechText.Segment("- First item\n- Second item\n\n> A quoted line.", Marker);
        Assert.Equal(2, segments.Count);
        Assert.Equal("First item Second item", segments[0].Text);
        Assert.Equal("A quoted line.", segments[1].Text);
    }

    [Fact]
    public void Segment_LongParagraph_SplitsUnderTheCap_AtSentenceEnds()
    {
        var sentence = "This is one full sentence of the answer. ";
        var answer = string.Concat(Enumerable.Repeat(sentence, 40)).Trim();
        var segments = SpeechText.Segment(answer, Marker);
        Assert.True(segments.Count > 1);
        Assert.All(segments, s => Assert.True(s.Text.Length <= SpeechText.MaxSegmentChars));
        Assert.Equal(answer, string.Join(" ", segments.Select(s => s.Text)));
    }

    [Fact]
    public void Segment_HugeAnswer_StopsAtTheSegmentCap()
    {
        var answer = string.Join("\n\n",
            Enumerable.Range(0, SpeechText.MaxSegments + 50).Select(i => $"Paragraph number {i}."));
        var segments = SpeechText.Segment(answer, Marker);
        Assert.Equal(SpeechText.MaxSegments, segments.Count);
    }
}

public class SpeechSpeedTests
{
    [Fact]
    public void All_IsTheFiveStopsInOrder_WithDefaultInTheMiddle()
    {
        Assert.Equal(new[] { 0.75, 1.0, 1.25, 1.5, 2.0 }, SpeechSpeed.All);
        Assert.Equal(1.0, SpeechSpeed.Default);
        Assert.Contains(SpeechSpeed.Default, SpeechSpeed.All);
    }

    [Theory]
    [InlineData(0.75, -3)]
    [InlineData(1.0, 0)]
    [InlineData(1.25, 2)]
    [InlineData(1.5, 4)]
    [InlineData(2.0, 6)]
    public void ToSapiRate_MapsTheFiveStops(double speed, int expectedRate)
    {
        Assert.Equal(expectedRate, SpeechSpeed.ToSapiRate(speed));
    }

    [Fact]
    public void ToSapiRate_IsMonotoneAcrossTheStops_AndClampsOutsideTheScale()
    {
        var rates = SpeechSpeed.All.Select(SpeechSpeed.ToSapiRate).ToList();
        Assert.Equal(rates.OrderBy(r => r), rates);
        Assert.Equal(-10, SpeechSpeed.ToSapiRate(0.1));
        Assert.Equal(10, SpeechSpeed.ToSapiRate(9.0));
        Assert.Equal(-10, SpeechSpeed.ToSapiRate(0));
    }

    [Theory]
    [InlineData(0.75, 1.0)]
    [InlineData(1.0, 1.25)]
    [InlineData(1.25, 1.5)]
    [InlineData(1.5, 2.0)]
    [InlineData(2.0, 0.75)]   // wraps
    [InlineData(1.1, 1.25)]   // between stops moves up
    [InlineData(3.0, 0.75)]   // past the fastest wraps
    public void CycleNext_WalksTheStops(double current, double expected)
    {
        Assert.Equal(expected, SpeechSpeed.CycleNext(current));
    }

    [Fact]
    public void Format_IsTheMultiplierWithTheReadersDecimalSeparator()
    {
        Assert.Equal("1×", SpeechSpeed.Format(1.0, CultureInfo.InvariantCulture));
        Assert.Equal("1.25×", SpeechSpeed.Format(1.25, CultureInfo.InvariantCulture));
        Assert.Equal("0.75×", SpeechSpeed.Format(0.75, CultureInfo.InvariantCulture));
        Assert.Equal("1,5×", SpeechSpeed.Format(1.5, CultureInfo.GetCultureInfo("es-ES")));
    }
}
