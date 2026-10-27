using MuseDesktop.Core;
using Xunit;

namespace MuseDesktop.Core.Tests;

// ───────────────────── R7B: the token-usage ring's model ─────────────────────

/// <summary>
/// Token usage resolution (R7B): the preference order IS the
/// honesty rule — the page's own counts beat the page's own
/// percent, which beats the local estimate — and the source label
/// travels with the reading so no surface can show a number
/// without knowing where it came from.
/// </summary>
public class TokenUsageResolveTests
{
    [Fact]
    public void Resolve_PrefersThePagesOwnCounts()
    {
        var probe = new TokenUsageProbe(50_000, 200_000, 90.0, 999_999);
        var usage = TokenUsage.Resolve(probe, 123_456);
        Assert.NotNull(usage);
        Assert.Equal(TokenUsageSource.PageReported, usage!.Source);
        Assert.False(usage.IsEstimate);
        Assert.Equal(50_000, usage.UsedTokens);
        Assert.Equal(200_000, usage.LimitTokens);
        Assert.Equal(25.0, usage.Percent);
    }

    [Fact]
    public void Resolve_FallsToThePagesOwnPercent()
    {
        var probe = new TokenUsageProbe(null, null, 62.5, 999_999);
        var usage = TokenUsage.Resolve(probe, 200_000);
        Assert.NotNull(usage);
        Assert.Equal(TokenUsageSource.PageReported, usage!.Source);
        Assert.Null(usage.UsedTokens);
        Assert.Null(usage.LimitTokens);
        Assert.Equal(62.5, usage.Percent);
    }

    [Fact]
    public void Resolve_FallsToTheEstimate()
    {
        // 40,000 characters ÷ 4 = 10,000 estimated tokens of 200,000.
        var probe = new TokenUsageProbe(null, null, null, 40_000);
        var usage = TokenUsage.Resolve(probe, 200_000);
        Assert.NotNull(usage);
        Assert.Equal(TokenUsageSource.Estimated, usage!.Source);
        Assert.True(usage.IsEstimate);
        Assert.Equal(10_000, usage.UsedTokens);
        Assert.Equal(200_000, usage.LimitTokens);
        Assert.Equal(5.0, usage.Percent);
    }

    [Fact]
    public void Resolve_UselessPageCountsFallThrough()
    {
        // A "limit" of zero is not data — the percent is used instead.
        var probe = new TokenUsageProbe(500, 0, 30.0, 100);
        var usage = TokenUsage.Resolve(probe, 200_000);
        Assert.NotNull(usage);
        Assert.Equal(TokenUsageSource.PageReported, usage!.Source);
        Assert.Equal(30.0, usage.Percent);
        Assert.Null(usage.UsedTokens);
    }

    [Fact]
    public void Resolve_NullProbeMeansNoReading()
    {
        Assert.Null(TokenUsage.Resolve(null, 200_000));
    }

    [Fact]
    public void Resolve_EmptyConversationIsAZeroEstimate()
    {
        var usage = TokenUsage.Resolve(new TokenUsageProbe(null, null, null, 0), 200_000);
        Assert.NotNull(usage);
        Assert.True(usage!.IsEstimate);
        Assert.Equal(0, usage.UsedTokens);
        Assert.Equal(0.0, usage.Percent);
    }

    [Fact]
    public void Resolve_EstimateUsesTheSanitizedLimit()
    {
        var usage = TokenUsage.Resolve(new TokenUsageProbe(null, null, null, 4_000), 0);
        Assert.NotNull(usage);
        Assert.Equal(TokenEstimator.DefaultLimitTokens, usage!.LimitTokens);
    }
}

/// <summary>
/// Percent math (R7B): clamped at both ends — an over-limit
/// conversation is a full ring, never 137%.
/// </summary>
public class TokenUsagePercentTests
{
    [Theory]
    [InlineData(0, 200_000, 0.0)]
    [InlineData(100_000, 200_000, 50.0)]
    [InlineData(200_000, 200_000, 100.0)]
    [InlineData(250_000, 200_000, 100.0)]   // over limit clamps
    [InlineData(50_000, 0, 0.0)]            // no limit, no percent
    public void FromCounts_PercentIsClamped(int used, int limit, double expected)
    {
        var usage = TokenUsage.FromCounts(used, limit, TokenUsageSource.PageReported);
        Assert.Equal(expected, usage.Percent);
    }

    [Theory]
    [InlineData(-5.0, 0.0)]
    [InlineData(42.4, 42.4)]
    [InlineData(137.0, 100.0)]
    public void FromPercent_Clamps(double input, double expected)
    {
        var usage = TokenUsage.FromPercent(input, TokenUsageSource.PageReported);
        Assert.Equal(expected, usage.Percent);
        Assert.Null(usage.UsedTokens);
        Assert.Null(usage.LimitTokens);
    }

    [Theory]
    [InlineData(42.4, 42)]
    [InlineData(42.5, 42)]   // Math.Round: banker's rounding at the midpoint
    [InlineData(99.6, 100)]
    public void PercentRounded_Rounds(double percent, int expected)
    {
        Assert.Equal(expected, TokenUsage.FromPercent(percent, TokenUsageSource.PageReported).PercentRounded);
    }
}

/// <summary>
/// The estimator (R7B): a documented rule of thumb — ceiling
/// division at ~4 characters per token — plus the limit repairs.
/// </summary>
public class TokenEstimatorTests
{
    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 1)]       // any text at all estimates to ≥ 1 token
    [InlineData(4, 1)]
    [InlineData(5, 2)]       // ceiling, not floor
    [InlineData(400, 100)]
    [InlineData(401, 101)]
    [InlineData(-10, 0)]
    public void EstimateTokens_CeilingDivision(int chars, int expected)
    {
        Assert.Equal(expected, TokenEstimator.EstimateTokens(chars));
    }

    [Theory]
    [InlineData(0, TokenEstimator.DefaultLimitTokens)]
    [InlineData(-1, TokenEstimator.DefaultLimitTokens)]
    [InlineData(5, TokenEstimator.MinLimitTokens)]
    [InlineData(999, TokenEstimator.MinLimitTokens)]
    [InlineData(200_000, 200_000)]
    [InlineData(99_000_000, TokenEstimator.MaxLimitTokens)]
    public void SanitizeLimit_Repairs(int input, int expected)
    {
        Assert.Equal(expected, TokenEstimator.SanitizeLimit(input));
    }
}

/// <summary>
/// The probe parser (R7B): strict like every bridge parser — the
/// page is untrusted, so negatives and absurdities drop to null
/// fields, and garbage drops the whole probe.
/// </summary>
public class TokenUsageProbeTests
{
    [Fact]
    public void TryParse_CountsPercentAndLength()
    {
        var probe = TokenUsageProbe.TryParse(
            """{"used": 12400, "limit": 200000, "percent": 6.2, "textLength": 51234}""");
        Assert.NotNull(probe);
        Assert.Equal(12_400, probe!.ReportedUsedTokens);
        Assert.Equal(200_000, probe.ReportedLimitTokens);
        Assert.Equal(6.2, probe.ReportedPercent);
        Assert.Equal(51_234, probe.ConversationTextLength);
    }

    [Fact]
    public void TryParse_NullsMeanThePageShowedNothing()
    {
        var probe = TokenUsageProbe.TryParse(
            """{"used": null, "limit": null, "percent": null, "textLength": 800}""");
        Assert.NotNull(probe);
        Assert.Null(probe!.ReportedUsedTokens);
        Assert.Null(probe.ReportedLimitTokens);
        Assert.Null(probe.ReportedPercent);
        Assert.Equal(800, probe.ConversationTextLength);
    }

    [Fact]
    public void TryParse_DropsImplausibleValues()
    {
        var probe = TokenUsageProbe.TryParse(
            """{"used": -3, "limit": 99999999, "percent": 5000, "textLength": -1}""");
        Assert.NotNull(probe);
        Assert.Null(probe!.ReportedUsedTokens);          // negative
        Assert.Null(probe.ReportedLimitTokens);           // beyond plausible
        Assert.Null(probe.ReportedPercent);               // absurd percent
        Assert.Equal(0, probe.ConversationTextLength);    // negative length → 0
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[1,2,3]")]
    [InlineData("42")]
    public void TryParse_GarbageIsNull(string? json)
    {
        Assert.Null(TokenUsageProbe.TryParse(json));
    }

    [Fact]
    public void TryParse_MissingFieldsAreEmptyNotAnError()
    {
        var probe = TokenUsageProbe.TryParse("""{}""");
        Assert.NotNull(probe);
        Assert.Null(probe!.ReportedUsedTokens);
        Assert.Equal(0, probe.ConversationTextLength);
    }
}

/// <summary>
/// Formatting (R7B): grouped counts, and the ring label whose
/// tilde is the estimate's honesty mark at a glance.
/// </summary>
public class TokenUsageTextTests
{
    [Theory]
    [InlineData(0, "0")]
    [InlineData(999, "999")]
    [InlineData(12_400, "12,400")]
    [InlineData(1_234_567, "1,234,567")]
    public void FormatCount_GroupsThousands(int value, string expected)
    {
        Assert.Equal(expected, TokenUsageText.FormatCount(value));
    }

    [Fact]
    public void FormatRingPercent_MarksEstimatesOnly()
    {
        var estimate = TokenUsage.FromCounts(84_000, 200_000, TokenUsageSource.Estimated);
        var reported = TokenUsage.FromCounts(84_000, 200_000, TokenUsageSource.PageReported);
        Assert.Equal("~42%", TokenUsageText.FormatRingPercent(estimate));
        Assert.Equal("42%", TokenUsageText.FormatRingPercent(reported));
    }
}

/// <summary>
/// Ring geometry (R7B): clockwise from the top, the progress-ring
/// convention; the large-arc flag flips exactly past halfway.
/// </summary>
public class TokenRingGeometryTests
{
    [Fact]
    public void Point_CardinalPercents()
    {
        // Center 13, radius 11.5: 0% top, 25% right, 50% bottom, 100% top again.
        AssertPoint(0, 13.0, 1.5);
        AssertPoint(25, 24.5, 13.0);
        AssertPoint(50, 13.0, 24.5);
        AssertPoint(100, 13.0, 1.5);
    }

    private static void AssertPoint(double percent, double x, double y)
    {
        var (px, py) = TokenRingGeometry.Point(percent, 13.0, 11.5);
        Assert.Equal(x, px, precision: 9);
        Assert.Equal(y, py, precision: 9);
    }

    [Theory]
    [InlineData(0.0, false)]
    [InlineData(50.0, false)]
    [InlineData(50.1, true)]
    [InlineData(100.0, true)]
    [InlineData(140.0, true)]   // clamped first
    public void IsLargeArc_PastHalfwayOnly(double percent, bool expected)
    {
        Assert.Equal(expected, TokenRingGeometry.IsLargeArc(percent));
    }
}

/// <summary>
/// The setting (R7B): default, clone round-trip, sanitize repair,
/// and the reset — the same four guarantees every round's fields
/// pin for themselves.
/// </summary>
public class R7BSettingsTests
{
    [Fact]
    public void Default_IsTheCommonLargeWindow()
    {
        Assert.Equal(200_000, new AppSettings().EstimatedContextLimitTokens);
        Assert.Equal(TokenEstimator.DefaultLimitTokens, new AppSettings().EstimatedContextLimitTokens);
    }

    [Fact]
    public void Field_RoundTripsThroughClone()
    {
        var clone = new AppSettings { EstimatedContextLimitTokens = 100_000 }.Clone();
        Assert.Equal(100_000, clone.EstimatedContextLimitTokens);
    }

    [Theory]
    [InlineData(0, TokenEstimator.DefaultLimitTokens)]
    [InlineData(-42, TokenEstimator.DefaultLimitTokens)]
    [InlineData(5, TokenEstimator.MinLimitTokens)]
    [InlineData(150_000, 150_000)]
    public void Sanitized_RepairsTheLimit(int input, int expected)
    {
        var settings = new AppSettings { EstimatedContextLimitTokens = input }.Sanitized();
        Assert.Equal(expected, settings.EstimatedContextLimitTokens);
    }

    [Fact]
    public void SettingsReset_RestoresTheDefault()
    {
        var settings = new AppSettings { EstimatedContextLimitTokens = 100_000 };
        SettingsReset.ApplyDefaults(settings);
        Assert.Equal(new AppSettings().EstimatedContextLimitTokens, settings.EstimatedContextLimitTokens);
    }
}

/// <summary>
/// The slice's strings (R7B): every token.* key the shell asks for
/// exists in the source catalog (the XAML/code drift guards scan
/// for usages; this pins the full intended set, so a renamed key
/// fails here even before a guard notices a missing one).
/// </summary>
public class TokenCatalogTests
{
    private static readonly string[] TokenKeys =
    {
        "token.usage",
        "token.ring_tooltip_page",
        "token.ring_tooltip_page_percent_only",
        "token.ring_tooltip_estimate",
        "token.ring_tooltip_unknown",
        "token.used_of_limit",
        "token.percent_only",
        "token.source_page",
        "token.source_estimate",
        "token.estimate_limit_note",
        "token.no_data",
        "token.estimated_limit_label",
        "token.estimated_limit_tooltip",
        "token.estimated_limit_note",
    };

    [Fact]
    public void EveryTokenKey_ExistsInTheSourceCatalog()
    {
        var missing = TokenKeys.Where(k => !StringCatalog.Source.ContainsKey(k)).ToList();
        Assert.True(missing.Count == 0, "Missing token.* keys: " + string.Join(", ", missing));
    }
}
