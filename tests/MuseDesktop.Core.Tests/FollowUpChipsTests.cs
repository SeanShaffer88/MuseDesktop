using MuseDesktop.Core;
using Xunit;

namespace MuseDesktop.Core.Tests;

// ───────────────────── Follow-up chips (v2.1.0) ─────────────────────

/// <summary>
/// The chip set as data: the six fixed ids, in the bar's display
/// order, each naming the catalog keys for its label and its prompt.
/// The words themselves live in the catalog (both built-in languages
/// carry every key — the catalog parity guard pins that); Core pins
/// only the wiring between an id and its keys.
/// </summary>
public class FollowUpChipsSetTests
{
    [Fact]
    public void All_SixChips_InDisplayOrder()
    {
        Assert.Equal(
            new[] { "summarize", "example", "steps", "table", "pros-cons", "simpler" },
            FollowUpChips.All.Select(chip => chip.Id).ToArray());
    }

    [Fact]
    public void All_EachChipNamesItsLabelAndPromptKeys()
    {
        foreach (var chip in FollowUpChips.All)
        {
            Assert.Equal($"chip.{chip.Id.Replace("-", "_")}", chip.LabelKey);
            Assert.Equal($"chip.{chip.Id.Replace("-", "_")}_prompt", chip.PromptKey);
        }
    }

    [Fact]
    public void Find_KnownIdsResolve_UnknownOnesDoNot()
    {
        foreach (var chip in FollowUpChips.All)
        {
            Assert.True(FollowUpChips.IsKnownId(chip.Id));
            Assert.Same(chip, FollowUpChips.Find(chip.Id));
        }
        Assert.False(FollowUpChips.IsKnownId("translate"));
        Assert.False(FollowUpChips.IsKnownId("Summarize")); // ids are exact — a fixed vocabulary
        Assert.False(FollowUpChips.IsKnownId(""));
        Assert.False(FollowUpChips.IsKnownId(null));
        Assert.Null(FollowUpChips.Find("nope"));
        Assert.Null(FollowUpChips.Find(null));
    }
}

/// <summary>
/// The offer rule: an answer is long at or over 150 words (149 is
/// not, 150 is), and an answer that has had its offer — used or
/// ignored — never gets a second one.
/// </summary>
public class FollowUpChipsOfferTests
{
    [Theory]
    [InlineData(0, false)]
    [InlineData(149, false)]
    [InlineData(150, true)]
    [InlineData(151, true)]
    [InlineData(5000, true)]
    public void ShouldOffer_LongStartsAtTheThreshold(int words, bool expected)
    {
        Assert.Equal(FollowUpChips.LongAnswerWordThreshold, 150);
        Assert.Equal(expected, FollowUpChips.ShouldOffer(words, alreadyOfferedForThisAnswer: false));
    }

    [Theory]
    [InlineData(150)]
    [InlineData(5000)]
    public void ShouldOffer_AlreadyOffered_NeverAgain(int words)
    {
        Assert.False(FollowUpChips.ShouldOffer(words, alreadyOfferedForThisAnswer: true));
    }
}

/// <summary>
/// The bridge's chip click (v2.1.0): {type:"md-followup-chip",
/// chip:"&lt;id&gt;"} parses to a FollowUpChip message whose Text is
/// the id — the only thing that ever crosses. A missing, non-string,
/// or unknown id is ignored whole, like every other bridge message
/// the page gets wrong: the page is untrusted, and the shell acts
/// on known chips only.
/// </summary>
public class PageBridgeFollowUpChipTests
{
    [Theory]
    [InlineData("summarize")]
    [InlineData("example")]
    [InlineData("steps")]
    [InlineData("table")]
    [InlineData("pros-cons")]
    [InlineData("simpler")]
    public void FollowUpChip_EveryKnownIdParses(string id)
    {
        var message = PageBridge.TryParse($"{{\"type\":\"md-followup-chip\",\"chip\":\"{id}\"}}");
        Assert.NotNull(message);
        Assert.Equal(PageBridgeKind.FollowUpChip, message!.Kind);
        Assert.Equal(id, message.Text);
    }

    [Theory]
    [InlineData("{\"type\":\"md-followup-chip\"}")]
    [InlineData("{\"type\":\"md-followup-chip\",\"chip\":\"translate\"}")]
    [InlineData("{\"type\":\"md-followup-chip\",\"chip\":\"Summarize\"}")]
    [InlineData("{\"type\":\"md-followup-chip\",\"chip\":\"\"}")]
    [InlineData("{\"type\":\"md-followup-chip\",\"chip\":3}")]
    [InlineData("{\"type\":\"md-followup-chip\",\"chip\":null}")]
    [InlineData("{\"type\":\"md-followup-chip\",\"text\":\"summarize\"}")]
    public void FollowUpChip_Malformed_IsIgnored(string json)
    {
        Assert.Null(PageBridge.TryParse(json));
    }
}
