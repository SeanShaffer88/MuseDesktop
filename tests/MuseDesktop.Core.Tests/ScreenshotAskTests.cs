using MuseDesktop.Core;
using Xunit;

namespace MuseDesktop.Core.Tests;

// ───────────────────── Screenshot → ask (v2.1.0) ─────────────────────
//
// The shell owns the pixels; these tests pin the decisions around
// them: the drag maths (any direction normalizes; under 8×8 px on
// either side is a slip, not a selection), the temp file's name,
// the starter line, the one-status-per-outcome mapping, and the
// temp file's deletion — the guarantee the whole feature's privacy
// shape rests on.

public class ScreenshotSelectionTests
{
    [Fact]
    public void Normalize_DragDownRight_IsTheRectangle()
    {
        var rect = ScreenshotSelection.Normalize(100, 50, 300, 200);
        Assert.Equal(new PhysicalRect(100, 50, 200, 150), rect);
        Assert.Equal(300, rect.Right);
        Assert.Equal(200, rect.Bottom);
    }

    [Fact]
    public void Normalize_DragUpLeft_SameRectangle()
    {
        Assert.Equal(
            ScreenshotSelection.Normalize(100, 50, 300, 200),
            ScreenshotSelection.Normalize(300, 200, 100, 50));
    }

    [Fact]
    public void Normalize_MixedDirections_UsesMinCornerAndDistances()
    {
        Assert.Equal(
            new PhysicalRect(100, 50, 200, 150),
            ScreenshotSelection.Normalize(300, 50, 100, 200));
        Assert.Equal(
            new PhysicalRect(100, 50, 200, 150),
            ScreenshotSelection.Normalize(100, 200, 300, 50));
    }

    [Fact]
    public void Normalize_NegativeCoordinates_Survive()
    {
        // A monitor left of the primary lives at negative X; the
        // maths must not blink.
        var rect = ScreenshotSelection.Normalize(-300, 10, -100, 110);
        Assert.Equal(new PhysicalRect(-300, 10, 200, 100), rect);
    }

    [Fact]
    public void IsConfirmed_AtTheMinimum_IsASelection()
    {
        Assert.True(ScreenshotSelection.IsConfirmed(
            new PhysicalRect(0, 0,
                ScreenshotSelection.MinSelectionSize,
                ScreenshotSelection.MinSelectionSize)));
    }

    [Theory]
    [InlineData(7, 100)]
    [InlineData(100, 7)]
    [InlineData(0, 0)]
    [InlineData(7, 7)]
    public void IsConfirmed_UnderTheMinimumOnEitherSide_IsACancel(int width, int height)
    {
        Assert.False(ScreenshotSelection.IsConfirmed(new PhysicalRect(0, 0, width, height)));
    }

    [Fact]
    public void ClampPoint_PinsToTheVirtualScreenBounds()
    {
        var bounds = new PhysicalRect(-1920, 0, 5760, 1080);
        Assert.Equal((-1920, 0), ScreenshotSelection.ClampPoint(-5000, -10, bounds));
        Assert.Equal((3840, 1080), ScreenshotSelection.ClampPoint(9999, 5000, bounds));
        Assert.Equal((10, 20), ScreenshotSelection.ClampPoint(10, 20, bounds));
    }
}

public class ScreenshotAskTests
{
    [Fact]
    public void TempFileName_CarriesThePrefixTimestampAndExtension()
    {
        var now = new DateTimeOffset(2026, 11, 2, 15, 4, 5, 678, TimeSpan.FromHours(-6));
        Assert.Equal("screenshot-ask-20261102-150405-678.png", ScreenshotAsk.TempFileName(now));
    }

    [Fact]
    public void TempFileName_DifferentMoments_DifferentNames()
    {
        var first = new DateTimeOffset(2026, 11, 2, 15, 4, 5, 678, TimeSpan.Zero);
        Assert.NotEqual(
            ScreenshotAsk.TempFileName(first),
            ScreenshotAsk.TempFileName(first.AddMilliseconds(1)));
    }

    [Fact]
    public void TempFilePath_JoinsTheFolderAndTheName()
    {
        var now = new DateTimeOffset(2026, 11, 2, 15, 4, 5, 678, TimeSpan.Zero);
        var path = ScreenshotAsk.TempFilePath("some-temp-folder", now);
        Assert.Equal(
            Path.Combine("some-temp-folder", "screenshot-ask-20261102-150405-678.png"),
            path);
    }

    [Fact]
    public void ComposeStarterLine_LocalizedLineWins_Trimmed()
    {
        Assert.Equal("¿Qué pasa aquí?", ScreenshotAsk.ComposeStarterLine("  ¿Qué pasa aquí?  "));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ComposeStarterLine_NoLocalizedLine_FallsBackToTheDefault(string? localized)
    {
        Assert.Equal(ScreenshotAsk.DefaultStarterLine, ScreenshotAsk.ComposeStarterLine(localized));
        Assert.Equal("What is happening in this screenshot?", ScreenshotAsk.DefaultStarterLine);
    }

    [Fact]
    public void StatusKey_EveryOutcomeHasItsOwnLine()
    {
        var keys = Enum.GetValues<ScreenshotAskOutcome>()
            .Select(ScreenshotAsk.StatusKey)
            .ToList();
        Assert.Equal(keys.Count, keys.Distinct().Count());
        Assert.All(keys, key => Assert.StartsWith("status.screenshot_", key));
        Assert.Equal("status.screenshot_attached", ScreenshotAsk.StatusKey(ScreenshotAskOutcome.Attached));
        Assert.Equal("status.screenshot_clipboard", ScreenshotAsk.StatusKey(ScreenshotAskOutcome.ClipboardFallback));
        Assert.Equal("status.screenshot_cancelled", ScreenshotAsk.StatusKey(ScreenshotAskOutcome.Cancelled));
        Assert.Equal("status.screenshot_capture_failed", ScreenshotAsk.StatusKey(ScreenshotAskOutcome.CaptureFailed));
        Assert.Equal("status.screenshot_no_message_box", ScreenshotAsk.StatusKey(ScreenshotAskOutcome.NoMessageBox));
    }

    [Fact]
    public void TryDeleteTempFile_ExistingFile_IsRemoved()
    {
        var path = Path.Combine(
            Path.GetTempPath(), $"md-screenshot-ask-test-{Guid.NewGuid():N}.png");
        File.WriteAllText(path, "not a real png — a deletion test");
        try
        {
            Assert.True(ScreenshotAsk.TryDeleteTempFile(path));
            Assert.False(File.Exists(path));
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void TryDeleteTempFile_NoPath_IsAQuietFalse(string? path)
    {
        Assert.False(ScreenshotAsk.TryDeleteTempFile(path));
    }

    [Fact]
    public void TryDeleteTempFile_MissingFile_IsAQuietFalse()
    {
        var path = Path.Combine(
            Path.GetTempPath(), $"md-screenshot-ask-missing-{Guid.NewGuid():N}.png");
        Assert.False(ScreenshotAsk.TryDeleteTempFile(path));
    }
}

// ───────────────────── One monitor's region (M1) ─────────────────────
//
// The single-monitor overlay drags in its own DIP space; these
// tests pin the conversion to the physical rectangle the capture
// takes: each edge scaled by the monitor's own factor, laid over
// the monitor's physical origin, clamped at the monitor's edges,
// and nothing (null) when the drag leaves the monitor entirely or
// collapses to a line.

public class ScreenshotRegionTests
{
    // A 2560×1440 physical monitor at the origin, 150% scaling
    // (its DIP size is 1706.67 × 960).
    private static readonly PhysicalRect Monitor150 = new(0, 0, 2560, 1440);

    [Fact]
    public void ToPhysicalRect_ScaleOne_IsTheIdentityInsideTheMonitor()
    {
        var monitor = new PhysicalRect(0, 0, 1920, 1080);
        var rect = ScreenshotRegion.ToPhysicalRect(new ScreenRect(100, 50, 200, 150), monitor, 1.0);
        Assert.Equal(new PhysicalRect(100, 50, 200, 150), rect);
    }

    [Fact]
    public void ToPhysicalRect_ScalesEachEdgeByTheMonitorFactor()
    {
        var rect = ScreenshotRegion.ToPhysicalRect(new ScreenRect(100, 100, 200, 100), Monitor150, 1.5);
        Assert.Equal(new PhysicalRect(150, 150, 300, 150), rect);
    }

    [Fact]
    public void ToPhysicalRect_ReversedDrag_NormalizesFirst()
    {
        var forward = ScreenshotRegion.ToPhysicalRect(new ScreenRect(100, 100, 200, 100), Monitor150, 1.5);
        var backward = ScreenshotRegion.ToPhysicalRect(new ScreenRect(300, 200, -200, -100), Monitor150, 1.5);
        Assert.Equal(forward, backward);
    }

    [Fact]
    public void ToPhysicalRect_NegativeOriginMonitor_OffsetsFromItsOwnCorner()
    {
        // A 150% monitor LEFT of the primary: physical X starts at
        // −2560; a local DIP drag must land in negative space.
        var monitor = new PhysicalRect(-2560, 0, 2560, 1440);
        var rect = ScreenshotRegion.ToPhysicalRect(new ScreenRect(100, 0, 100, 100), monitor, 1.5);
        Assert.Equal(new PhysicalRect(-2410, 0, 150, 150), rect);
    }

    [Fact]
    public void ToPhysicalRect_DragPastTheEdges_ClampsToTheMonitor()
    {
        var rect = ScreenshotRegion.ToPhysicalRect(new ScreenRect(-50, -20, 4000, 2000), Monitor150, 1.5);
        Assert.Equal(Monitor150, rect);
    }

    [Fact]
    public void ToPhysicalRect_WhollyOutsideTheMonitor_IsNull()
    {
        // A DIP selection that starts past the monitor's DIP width
        // (2560 / 1.5 ≈ 1707) names no pixel of this monitor.
        Assert.Null(ScreenshotRegion.ToPhysicalRect(new ScreenRect(2000, 0, 100, 100), Monitor150, 1.5));
        Assert.Null(ScreenshotRegion.ToPhysicalRect(new ScreenRect(-500, 0, 100, 100), Monitor150, 1.5));
    }

    [Fact]
    public void ToPhysicalRect_ZeroAreaSelection_IsNull()
    {
        Assert.Null(ScreenshotRegion.ToPhysicalRect(new ScreenRect(10, 10, 0, 50), Monitor150, 1.5));
        Assert.Null(ScreenshotRegion.ToPhysicalRect(new ScreenRect(10, 10, 50, 0), Monitor150, 1.5));
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(-1.5)]
    [InlineData(double.NaN)]
    public void ToPhysicalRect_UnusableScale_ReadsAsScaleOne(double scale)
    {
        var monitor = new PhysicalRect(0, 0, 1920, 1080);
        var rect = ScreenshotRegion.ToPhysicalRect(new ScreenRect(10, 10, 100, 50), monitor, scale);
        Assert.Equal(new PhysicalRect(10, 10, 100, 50), rect);
    }
}
