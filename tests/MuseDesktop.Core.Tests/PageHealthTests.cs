using MuseDesktop.Core;
using Xunit;

namespace MuseDesktop.Core.Tests;

// ───────────────────── Page health: probe parsing + verdicts ─────────────────────

/// <summary>
/// The Page health model (Diagnostics): the bridge's probe is
/// untrusted input parsed as strictly as the bridge protocol itself,
/// and the verdicts follow the features' real shapes — no messages
/// on the page makes bookmarks/export Not applicable (not broken),
/// a missing composer makes drafts Not recognised, and no approval
/// card showing is the watcher's healthy state, never a failure.
/// A bridge that does not answer at all fails every feature: none
/// of them can see the page.
/// </summary>
public class PageHealthProbeParsingTests
{
    [Fact]
    public void Probe_FullResult_Parses()
    {
        var probe = PageHealth.TryParseProbe(
            """{"messages":true,"bookmarkButtons":true,"composer":true,"approval":"allow-prefix"}""");
        Assert.NotNull(probe);
        Assert.True(probe!.MessagesPresent);
        Assert.True(probe.BookmarkButtonsPresent);
        Assert.True(probe.ComposerPresent);
        Assert.True(probe.ApprovalCardPresent);
        Assert.Equal(ApprovalDetectionSignal.AllowPrefix, probe.ApprovalSignal);
    }

    [Fact]
    public void Probe_PairSignal_Parses()
    {
        var probe = PageHealth.TryParseProbe("""{"approval":"pair"}""");
        Assert.NotNull(probe);
        Assert.True(probe!.ApprovalCardPresent);
        Assert.Equal(ApprovalDetectionSignal.ButtonPair, probe.ApprovalSignal);
        Assert.False(probe.MessagesPresent);
    }

    [Fact]
    public void Probe_NullApproval_NoCard()
    {
        var probe = PageHealth.TryParseProbe("""{"messages":true,"approval":null}""");
        Assert.NotNull(probe);
        Assert.False(probe!.ApprovalCardPresent);
        Assert.Equal(ApprovalDetectionSignal.Unknown, probe.ApprovalSignal);
    }

    [Fact]
    public void Probe_UnknownApprovalString_MapsToNoCardNeverEchoed()
    {
        var probe = PageHealth.TryParseProbe("""{"approval":"Allow everything forever"}""");
        Assert.NotNull(probe);
        Assert.False(probe!.ApprovalCardPresent);
        Assert.Equal(ApprovalDetectionSignal.Unknown, probe.ApprovalSignal);
    }

    [Fact]
    public void Probe_FlagsCountOnlyWhenLiterallyTrue()
    {
        var probe = PageHealth.TryParseProbe(
            """{"messages":"true","bookmarkButtons":1,"composer":true}""");
        Assert.NotNull(probe);
        Assert.False(probe!.MessagesPresent);
        Assert.False(probe.BookmarkButtonsPresent);
        Assert.True(probe.ComposerPresent);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[1,2,3]")]
    [InlineData("42")]
    public void Probe_Garbage_ReturnsNull(string? json) =>
        Assert.Null(PageHealth.TryParseProbe(json));
}

public class PageHealthEvaluateTests
{
    private static PageHealthFinding Finding(
        IReadOnlyList<PageHealthFinding> findings, PageHealthFeature feature) =>
        findings.Single(f => f.Feature == feature);

    [Fact]
    public void Evaluate_AlwaysCoversTheFourFeaturesInOrder()
    {
        var findings = PageHealth.Evaluate(PageProbeResult.None);
        Assert.Equal(
            new[]
            {
                PageHealthFeature.MessageBookmarks,
                PageHealthFeature.Drafts,
                PageHealthFeature.ConversationExport,
                PageHealthFeature.ApprovalAlerts,
            },
            findings.Select(f => f.Feature).ToArray());
    }

    [Fact]
    public void Evaluate_HealthyConversationPage_AllRecognisedAndWatcherQuiet()
    {
        var probe = new PageProbeResult(
            MessagesPresent: true, BookmarkButtonsPresent: true, ComposerPresent: true,
            ApprovalCardPresent: false, ApprovalSignal: ApprovalDetectionSignal.Unknown);
        var findings = PageHealth.Evaluate(probe);
        Assert.Equal(PageHealthStatus.Recognised,
            Finding(findings, PageHealthFeature.MessageBookmarks).Status);
        Assert.Equal(PageHealthStatus.Recognised,
            Finding(findings, PageHealthFeature.Drafts).Status);
        Assert.Equal(PageHealthStatus.Recognised,
            Finding(findings, PageHealthFeature.ConversationExport).Status);
        // No card showing is the watcher's healthy state — NOT a failure.
        Assert.Equal(PageHealthStatus.NoCardShowing,
            Finding(findings, PageHealthFeature.ApprovalAlerts).Status);
    }

    [Fact]
    public void Evaluate_CardShowing_RecognisedAndNamesTheRule()
    {
        var probe = new PageProbeResult(true, true, true, true, ApprovalDetectionSignal.AllowPrefix);
        var approval = Finding(PageHealth.Evaluate(probe), PageHealthFeature.ApprovalAlerts);
        Assert.Equal(PageHealthStatus.Recognised, approval.Status);
        Assert.Contains("allow-button rule", approval.Explanation);

        var pairProbe = new PageProbeResult(true, true, true, true, ApprovalDetectionSignal.ButtonPair);
        var pairApproval = Finding(PageHealth.Evaluate(pairProbe), PageHealthFeature.ApprovalAlerts);
        Assert.Equal(PageHealthStatus.Recognised, pairApproval.Status);
        Assert.Contains("button-pair rule", pairApproval.Explanation);
    }

    [Fact]
    public void Evaluate_NoConversationOpen_BookmarksAndExportNotApplicable()
    {
        // The home / new-chat page: composer present, no messages yet.
        var probe = new PageProbeResult(false, false, true, false, ApprovalDetectionSignal.Unknown);
        var findings = PageHealth.Evaluate(probe);
        Assert.Equal(PageHealthStatus.NotApplicable,
            Finding(findings, PageHealthFeature.MessageBookmarks).Status);
        Assert.Equal(PageHealthStatus.NotApplicable,
            Finding(findings, PageHealthFeature.ConversationExport).Status);
        Assert.Equal(PageHealthStatus.Recognised,
            Finding(findings, PageHealthFeature.Drafts).Status);
    }

    [Fact]
    public void Evaluate_MessagesWithoutButtons_BookmarksNotRecognisedExportStillRecognised()
    {
        var probe = new PageProbeResult(true, false, true, false, ApprovalDetectionSignal.Unknown);
        var findings = PageHealth.Evaluate(probe);
        var bookmarks = Finding(findings, PageHealthFeature.MessageBookmarks);
        Assert.Equal(PageHealthStatus.NotRecognised, bookmarks.Status);
        Assert.Contains("update catches up", bookmarks.Advice);
        Assert.Equal(PageHealthStatus.Recognised,
            Finding(findings, PageHealthFeature.ConversationExport).Status);
    }

    [Fact]
    public void Evaluate_ComposerMissing_DraftsNotRecognised()
    {
        var probe = new PageProbeResult(true, true, false, false, ApprovalDetectionSignal.Unknown);
        var drafts = Finding(PageHealth.Evaluate(probe), PageHealthFeature.Drafts);
        Assert.Equal(PageHealthStatus.NotRecognised, drafts.Status);
        Assert.Contains("update catches up", drafts.Advice);
    }

    [Fact]
    public void Evaluate_BridgeSilent_EveryFeatureNotRecognised()
    {
        var findings = PageHealth.Evaluate(null);
        Assert.All(findings, f => Assert.Equal(PageHealthStatus.NotRecognised, f.Status));
        Assert.All(findings, f => Assert.Contains("did not answer", f.Explanation));
    }

    [Fact]
    public void RenderLines_OnePlainLinePerFindingWithStatusWords()
    {
        var lines = PageHealth.RenderLines(PageHealth.Evaluate(PageProbeResult.None));
        Assert.Equal(4, lines.Count);
        Assert.StartsWith("Message bookmarks — Not applicable.", lines[0]);
        Assert.StartsWith("Drafts — Not recognised.", lines[1]);
        Assert.StartsWith("Conversation export — Not applicable.", lines[2]);
        Assert.StartsWith("Approval alerts — No card showing.", lines[3]);
    }
}
