using System.Globalization;
using MuseDesktop.Core;
using Xunit;

namespace MuseDesktop.Core.Tests;

// ───────────────────── Draft meter + tidy paste (v2.1.0) ─────────────────────

/// <summary>
/// The draft meter's rules: characters as typed, words as
/// whitespace-separated runs, rough tokens as characters ÷ 4 rounded
/// up, and bands at the documented thresholds (Long above 1,500
/// words or 8,000 characters; VeryLong above 4,000 words or 20,000
/// characters — a threshold itself is still the lower band). A
/// whitespace-only box measures Empty: nothing there, nothing to
/// meter.
/// </summary>
public class DraftMeterMeasureTests
{
    [Fact]
    public void Measure_NullAndEmpty_AreEmpty()
    {
        foreach (var text in new string?[] { null, "" })
        {
            var m = DraftMeter.Measure(text);
            Assert.Equal(0, m.Characters);
            Assert.Equal(0, m.Words);
            Assert.Equal(0, m.RoughTokens);
            Assert.Equal(DraftBand.Empty, m.Band);
        }
    }

    [Fact]
    public void Measure_WhitespaceOnly_IsEmptyButCountsCharacters()
    {
        var m = DraftMeter.Measure("   \n\t ");
        Assert.Equal(6, m.Characters);
        Assert.Equal(0, m.Words);
        Assert.Equal(DraftBand.Empty, m.Band);
    }

    [Fact]
    public void Measure_CountsWordsAcrossWhitespaceKinds()
    {
        var m = DraftMeter.Measure("one two  three\nfour\tfive");
        Assert.Equal(24, m.Characters);
        Assert.Equal(5, m.Words);
        Assert.Equal(DraftBand.Normal, m.Band);
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(4, 1)]
    [InlineData(5, 2)]
    [InlineData(8, 2)]
    [InlineData(9, 3)]
    public void Measure_RoughTokensAreCharactersDividedByFourRoundedUp(int chars, int tokens)
    {
        var m = DraftMeter.Measure(new string('a', chars));
        Assert.Equal(chars, m.Characters);
        Assert.Equal(1, m.Words);
        Assert.Equal(tokens, m.RoughTokens);
    }

    [Fact]
    public void Measure_CharacterBandBoundaries()
    {
        Assert.Equal(DraftBand.Normal,
            DraftMeter.Measure(new string('a', DraftMeter.LongCharacterThreshold)).Band);
        Assert.Equal(DraftBand.Long,
            DraftMeter.Measure(new string('a', DraftMeter.LongCharacterThreshold + 1)).Band);
        Assert.Equal(DraftBand.Long,
            DraftMeter.Measure(new string('a', DraftMeter.VeryLongCharacterThreshold)).Band);
        Assert.Equal(DraftBand.VeryLong,
            DraftMeter.Measure(new string('a', DraftMeter.VeryLongCharacterThreshold + 1)).Band);
    }

    [Fact]
    public void Measure_WordBandBoundaries()
    {
        static string Words(int count) => string.Join(" ", Enumerable.Repeat("word", count));
        Assert.Equal(DraftBand.Normal, DraftMeter.Measure(Words(DraftMeter.LongWordThreshold)).Band);
        Assert.Equal(DraftBand.Long, DraftMeter.Measure(Words(DraftMeter.LongWordThreshold + 1)).Band);
        Assert.Equal(DraftBand.Long, DraftMeter.Measure(Words(DraftMeter.VeryLongWordThreshold)).Band);
        Assert.Equal(DraftBand.VeryLong, DraftMeter.Measure(Words(DraftMeter.VeryLongWordThreshold + 1)).Band);
    }
}

/// <summary>
/// The meter's status line: the localized template filled with the
/// measurement's words and rough tokens in the given culture, the
/// band's hint appended for the long bands only, and "" for an empty
/// draft.
/// </summary>
public class DraftMeterFormatTests
{
    private const string Template = "Draft: {0:N0} words · ~{1:N0} tokens";
    private const string LongHint = "— long for one message";
    private const string VeryLongHint = "— very long; consider splitting or a context pack";

    private static string Format(DraftMeasurement m) =>
        DraftMeter.Format(m, Template, LongHint, VeryLongHint, CultureInfo.InvariantCulture);

    [Fact]
    public void Format_NormalBand_IsJustTheCounts()
    {
        var text = Format(DraftMeter.Measure("hello world"));
        Assert.Equal("Draft: 2 words · ~3 tokens", text);
    }

    [Fact]
    public void Format_GroupsThousands()
    {
        var text = Format(new DraftMeasurement(20_500, 5_100, 5_125, DraftBand.VeryLong));
        Assert.StartsWith("Draft: 5,100 words · ~5,125 tokens", text);
    }

    [Fact]
    public void Format_LongBand_AppendsTheLongHint()
    {
        var text = Format(new DraftMeasurement(8_001, 10, 2_001, DraftBand.Long));
        Assert.Equal("Draft: 10 words · ~2,001 tokens — long for one message", text);
    }

    [Fact]
    public void Format_VeryLongBand_AppendsTheVeryLongHint()
    {
        var text = Format(new DraftMeasurement(20_001, 10, 5_001, DraftBand.VeryLong));
        Assert.Equal("Draft: 10 words · ~5,001 tokens — very long; consider splitting or a context pack", text);
    }

    [Fact]
    public void Format_EmptyBand_IsEmpty()
    {
        Assert.Equal("", Format(DraftMeter.Measure("")));
        Assert.Equal("", Format(DraftMeter.Measure(null)));
    }
}

/// <summary>
/// The tidy offer's threshold and the tidied form: over 2,000
/// characters and not already tidied. The tidied form is a
/// "Pasted text (N words):" header (plain digits, so the header is
/// recognisable anywhere) over the text as a Markdown blockquote —
/// every line "&gt; "-prefixed, an empty line just "&gt;", trailing
/// blank lines trimmed. Tidying is idempotent: its own output never
/// needs tidying again.
/// </summary>
public class PasteTidierTests
{
    private static string LongText(int chars) => new('a', chars);

    [Fact]
    public void NeedsTidying_ThresholdBoundary()
    {
        Assert.False(PasteTidier.NeedsTidying(LongText(PasteTidier.LongPasteThreshold)));
        Assert.True(PasteTidier.NeedsTidying(LongText(PasteTidier.LongPasteThreshold + 1)));
    }

    [Fact]
    public void NeedsTidying_NullEmptyAndShort_AreFalse()
    {
        Assert.False(PasteTidier.NeedsTidying(null));
        Assert.False(PasteTidier.NeedsTidying(""));
        Assert.False(PasteTidier.NeedsTidying("a short note"));
    }

    [Fact]
    public void NeedsTidying_AlreadyTidied_IsFalse()
    {
        var tidied = PasteTidier.Tidy(LongText(3_000));
        Assert.True(tidied.Length > PasteTidier.LongPasteThreshold);
        Assert.True(PasteTidier.IsTidied(tidied));
        Assert.False(PasteTidier.NeedsTidying(tidied));
    }

    [Fact]
    public void IsTidied_RecognisesOnlyTheRealHeader()
    {
        Assert.True(PasteTidier.IsTidied("Pasted text (12 words):\n> anything"));
        Assert.False(PasteTidier.IsTidied("Pasted text (many words):\n> anything"));
        Assert.False(PasteTidier.IsTidied("Pasted text (12 words)\n> anything"));
        Assert.False(PasteTidier.IsTidied("Some text\nPasted text (12 words):"));
        Assert.False(PasteTidier.IsTidied(null));
        Assert.False(PasteTidier.IsTidied(""));
    }

    [Fact]
    public void Tidy_ShortText_StillWorksWhenCalledDirectly()
    {
        Assert.Equal(
            "Pasted text (3 words):\n> one two three",
            PasteTidier.Tidy("one two three"));
    }

    [Fact]
    public void Tidy_HeaderCountsThePastesWords()
    {
        var tidied = PasteTidier.Tidy("alpha beta\ngamma");
        Assert.StartsWith("Pasted text (3 words):\n", tidied);
    }

    [Fact]
    public void Tidy_QuotesEveryLineAndBareQuotesEmptyLines()
    {
        var tidied = PasteTidier.Tidy("first\n\nthird");
        Assert.Equal(
            "Pasted text (2 words):\n> first\n>\n> third",
            tidied);
    }

    [Fact]
    public void Tidy_TrimsTrailingBlankLines()
    {
        var tidied = PasteTidier.Tidy("only line\n\n   \n");
        Assert.Equal("Pasted text (2 words):\n> only line", tidied);
    }

    [Fact]
    public void Tidy_NormalisesCrLfLineEndings()
    {
        var tidied = PasteTidier.Tidy("one\r\ntwo");
        Assert.Equal("Pasted text (2 words):\n> one\n> two", tidied);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   \n \t ")]
    public void Tidy_NothingThere_TidiesToEmpty(string? text)
    {
        Assert.Equal("", PasteTidier.Tidy(text));
    }
}

/// <summary>
/// The bridge's long-paste report (v2.1.0): {type:"md-long-paste",
/// length:N} parses to a LongPaste message whose Text is the length
/// as an integer string — a count, never content. Malformed reports
/// (no length, a string length, a negative length) are ignored
/// whole, like every other bridge message the page gets wrong.
/// </summary>
public class PageBridgeLongPasteTests
{
    [Fact]
    public void LongPaste_ParsesTheLength()
    {
        var message = PageBridge.TryParse("{\"type\":\"md-long-paste\",\"length\":5231}");
        Assert.NotNull(message);
        Assert.Equal(PageBridgeKind.LongPaste, message!.Kind);
        Assert.Equal("5231", message.Text);
    }

    [Fact]
    public void LongPaste_ClampsToTheDraftCap()
    {
        var message = PageBridge.TryParse("{\"type\":\"md-long-paste\",\"length\":999999999}");
        Assert.NotNull(message);
        Assert.Equal(ConversationDrafts.MaxTextLength.ToString(CultureInfo.InvariantCulture), message!.Text);
    }

    [Theory]
    [InlineData("{\"type\":\"md-long-paste\"}")]
    [InlineData("{\"type\":\"md-long-paste\",\"length\":\"5231\"}")]
    [InlineData("{\"type\":\"md-long-paste\",\"length\":-5}")]
    [InlineData("{\"type\":\"md-long-paste\",\"length\":52.5}")]
    public void LongPaste_Malformed_IsIgnored(string json)
    {
        Assert.Null(PageBridge.TryParse(json));
    }
}
