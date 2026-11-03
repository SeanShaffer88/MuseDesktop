using MuseDesktop.Core;
using Xunit;

namespace MuseDesktop.Core.Tests;

// ───────────────────── Compare mode (v2.1.0) ─────────────────────
//
// The shell owns the window and the send script; these tests pin
// the decisions around them: which profiles the panes open on (two
// different ones whenever the user HAS two), what the script's
// report means (a miss on a still-loading pane is "try again", not
// "not found"), the one-status-per-outcome mapping, and the prompt
// box's clear rule (the text survives any half-failure — it is the
// user's only copy of the question).

public class CompareModeTests
{
    private static AccountProfile Profile(string id) => new() { Id = id, Name = id };

    // ── pane defaults ────────────────────────────────────────────

    [Fact]
    public void DefaultPaneProfiles_TwoProfiles_PanesDiffer()
    {
        var (left, right) = CompareMode.DefaultPaneProfiles(
            new[] { Profile("p1"), Profile("p2") }, activeProfileId: "p1");
        Assert.Equal("p1", left);
        Assert.Equal("p2", right);
    }

    [Fact]
    public void DefaultPaneProfiles_ActiveIsSecond_LeftStartsOnActive()
    {
        var (left, right) = CompareMode.DefaultPaneProfiles(
            new[] { Profile("p1"), Profile("p2"), Profile("p3") }, activeProfileId: "p2");
        Assert.Equal("p2", left);
        Assert.Equal("p1", right); // the first OTHER profile
    }

    [Fact]
    public void DefaultPaneProfiles_OneProfile_BothPanesShareIt()
    {
        var (left, right) = CompareMode.DefaultPaneProfiles(
            new[] { Profile("p1") }, activeProfileId: "p1");
        Assert.Equal("p1", left);
        Assert.Equal("p1", right);
    }

    [Fact]
    public void DefaultPaneProfiles_ActiveMissing_LeftFallsToFirstVisible()
    {
        var (left, right) = CompareMode.DefaultPaneProfiles(
            new[] { Profile("p1"), Profile("p2") }, activeProfileId: "gone");
        Assert.Equal("p1", left);
        Assert.Equal("p2", right);
    }

    [Fact]
    public void DefaultPaneProfiles_NoActiveGiven_LeftFallsToFirstVisible()
    {
        var (left, right) = CompareMode.DefaultPaneProfiles(
            new[] { Profile("p1"), Profile("p2") }, activeProfileId: null);
        Assert.Equal("p1", left);
        Assert.Equal("p2", right);
    }

    [Fact]
    public void DefaultPaneProfiles_EmptyList_DefaultProfileTwice()
    {
        var (left, right) = CompareMode.DefaultPaneProfiles(
            Array.Empty<AccountProfile>(), activeProfileId: "p1");
        Assert.Equal(Profiles.DefaultId, left);
        Assert.Equal(Profiles.DefaultId, right);
    }

    // ── reading the script's report ──────────────────────────────

    [Fact]
    public void OutcomeFor_SentToken_IsSent_EvenWhileLoading()
    {
        Assert.Equal(CompareSendOutcome.Sent, CompareMode.OutcomeFor("sent", pageStillLoading: true));
        Assert.Equal(CompareSendOutcome.Sent, CompareMode.OutcomeFor("sent", pageStillLoading: false));
    }

    [Fact]
    public void OutcomeFor_NoComposer_WhileLoading_IsStillLoading()
    {
        Assert.Equal(CompareSendOutcome.StillLoading,
            CompareMode.OutcomeFor("no-composer", pageStillLoading: true));
    }

    [Fact]
    public void OutcomeFor_NoComposer_AfterLoad_IsNoMessageBox()
    {
        Assert.Equal(CompareSendOutcome.NoMessageBox,
            CompareMode.OutcomeFor("no-composer", pageStillLoading: false));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("error")]
    [InlineData("Sent")] // the script's vocabulary is exact — case matters
    [InlineData("sent ")]
    public void OutcomeFor_AnythingElse_IsNeverSent(string? token)
    {
        Assert.Equal(CompareSendOutcome.NoMessageBox,
            CompareMode.OutcomeFor(token, pageStillLoading: false));
        Assert.Equal(CompareSendOutcome.StillLoading,
            CompareMode.OutcomeFor(token, pageStillLoading: true));
    }

    // ── status mapping + the clear rule ──────────────────────────

    [Fact]
    public void StatusKey_OneDistinctLinePerOutcome()
    {
        var keys = Enum.GetValues<CompareSendOutcome>()
            .Select(CompareMode.StatusKey).ToList();
        Assert.Equal(keys.Count, keys.Distinct().Count());
        Assert.All(keys, key => Assert.StartsWith("compare.status_", key));
    }

    [Fact]
    public void BothSent_OnlyACleanDoubleSendClears()
    {
        Assert.True(CompareMode.BothSent(CompareSendOutcome.Sent, CompareSendOutcome.Sent));
        Assert.False(CompareMode.BothSent(CompareSendOutcome.Sent, CompareSendOutcome.NoMessageBox));
        Assert.False(CompareMode.BothSent(CompareSendOutcome.NoMessageBox, CompareSendOutcome.Sent));
        Assert.False(CompareMode.BothSent(CompareSendOutcome.StillLoading, CompareSendOutcome.StillLoading));
    }
}
