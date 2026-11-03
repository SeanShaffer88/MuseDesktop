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
