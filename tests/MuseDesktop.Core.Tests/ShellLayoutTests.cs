using MuseDesktop.Core;
using Xunit;

namespace MuseDesktop.Core.Tests;

/// <summary>
/// Shell layout (2026-11-01, SEAN's confirmed mockup B — Compact
/// Focus; 2.0.0 adds mockup C — Left Rail — and makes Compact Focus
/// the fresh-install default). The invariants: Standard is exactly
/// the long-standing density numbers (no silent change for existing
/// users), Compact Focus is slimmer than every density and trades
/// the status bar for the pill, Left Rail pins the same slim strip,
/// keeps the status bar, and takes a 56px rail — and a SAVED layout
/// always wins, because only the no-saved-value default changed.
/// </summary>
public class ShellLayoutTests
{
    [Theory]
    [InlineData(ShellDensity.Comfortable)]
    [InlineData(ShellDensity.Compact)]
    public void Standard_MatchesDensityMetricsExactly(ShellDensity density)
    {
        var densityMetrics = DensityMetrics.For(density);
        var layout = ShellLayoutMetrics.For(ShellLayout.Standard, density);
        Assert.Equal(densityMetrics.ToolbarHeight, layout.ToolbarHeight);
        Assert.Equal(densityMetrics.StatusBarHeight, layout.StatusBarHeight);
        Assert.True(layout.StatusBarVisible);
        Assert.False(layout.StatusPillVisible);
        Assert.Equal(0, layout.RailWidth);
        Assert.False(layout.RailVisible);
    }

    [Theory]
    [InlineData(ShellDensity.Comfortable)]
    [InlineData(ShellDensity.Compact)]
    public void CompactFocus_SlimToolbarBarHiddenPillShown(ShellDensity density)
    {
        var layout = ShellLayoutMetrics.For(ShellLayout.CompactFocus, density);
        Assert.Equal(30, layout.ToolbarHeight);
        Assert.True(layout.ToolbarHeight < DensityMetrics.For(density).ToolbarHeight);
        Assert.False(layout.StatusBarVisible);
        Assert.True(layout.StatusPillVisible);
        Assert.Equal(0, layout.StatusBarHeight);
        Assert.Equal(0, layout.RailWidth);
        Assert.False(layout.RailVisible);
    }

    [Theory]
    [InlineData(ShellDensity.Comfortable, 34)] // (40-30) toolbar + 24 status bar
    [InlineData(ShellDensity.Compact, 22)]     // (32-30) toolbar + 20 status bar
    public void CompactFocus_PageHeightGainIsTheMockupPromise(ShellDensity density, double expected)
    {
        Assert.Equal(expected, ShellLayoutMetrics.PageHeightGain(ShellLayout.CompactFocus, density));
        Assert.Equal(0, ShellLayoutMetrics.PageHeightGain(ShellLayout.Standard, density));
    }

    [Theory]
    [InlineData(ShellDensity.Comfortable)]
    [InlineData(ShellDensity.Compact)]
    public void LeftRail_SlimStripStatusBarKeptRail56(ShellDensity density)
    {
        var densityMetrics = DensityMetrics.For(density);
        var layout = ShellLayoutMetrics.For(ShellLayout.LeftRail, density);
        Assert.Equal(30, layout.ToolbarHeight);
        Assert.True(layout.StatusBarVisible);
        Assert.False(layout.StatusPillVisible);
        Assert.Equal(densityMetrics.StatusBarHeight, layout.StatusBarHeight);
        Assert.Equal(56, layout.RailWidth);
        Assert.True(layout.RailVisible);
    }

    [Theory]
    [InlineData(ShellDensity.Comfortable, 10)] // (40-30) toolbar; the status bar stays
    [InlineData(ShellDensity.Compact, 2)]      // (32-30) toolbar; the status bar stays
    public void LeftRail_PageHeightGainIsTheStripSavingOnly(ShellDensity density, double expected)
    {
        Assert.Equal(expected, ShellLayoutMetrics.PageHeightGain(ShellLayout.LeftRail, density));
    }

    [Theory]
    [InlineData(ShellLayout.Standard, ShellLayout.CompactFocus)]
    [InlineData(ShellLayout.CompactFocus, ShellLayout.LeftRail)]
    [InlineData(ShellLayout.LeftRail, ShellLayout.Standard)]
    [InlineData((ShellLayout)99, ShellLayout.Standard)]
    public void Cycle_StepsThroughAllThreeLayouts(ShellLayout from, ShellLayout expected)
    {
        Assert.Equal(expected, ShellLayoutCycle.Next(from));
    }

    [Fact]
    public void Settings_DefaultIsCompactFocus()
    {
        // 2.0.0 (SEAN's pick): a FRESH install — no saved settings at
        // all — starts in Compact Focus.
        Assert.Equal(ShellLayout.CompactFocus, new AppSettings().ShellLayout);
    }

    [Fact]
    public void Settings_MissingFileLoadsCompactFocus()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "settings.json");
        Assert.Equal(ShellLayout.CompactFocus, SettingsStore.Load(path).ShellLayout);
    }

    [Theory]
    [InlineData(ShellLayout.Standard)]
    [InlineData(ShellLayout.CompactFocus)]
    [InlineData(ShellLayout.LeftRail)]
    public void Settings_SavedLayoutAlwaysWins(ShellLayout saved)
    {
        // The persisted value round-trips through the real store and
        // is never overridden by the fresh-install default — existing
        // users keep the layout they saved, Standard included.
        var dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "settings.json");
        try
        {
            SettingsStore.Save(path, new AppSettings { ShellLayout = saved });
            Assert.Equal(saved, SettingsStore.Load(path).ShellLayout);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Settings_UndefinedLayoutRepairsToTheDefault()
    {
        var settings = new AppSettings { ShellLayout = (ShellLayout)99 }.Sanitized();
        Assert.Equal(ShellLayout.CompactFocus, settings.ShellLayout);
    }

    [Fact]
    public void Settings_CompactFocusSurvivesSanitize()
    {
        var settings = new AppSettings { ShellLayout = ShellLayout.CompactFocus }.Sanitized();
        Assert.Equal(ShellLayout.CompactFocus, settings.ShellLayout);
    }

    [Fact]
    public void Settings_LeftRailSurvivesSanitize()
    {
        var settings = new AppSettings { ShellLayout = ShellLayout.LeftRail }.Sanitized();
        Assert.Equal(ShellLayout.LeftRail, settings.ShellLayout);
    }
}
