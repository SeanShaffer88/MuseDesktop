using MuseDesktop.Core;
using Xunit;

namespace MuseDesktop.Core.Tests;

/// <summary>
/// Shell layout (2026-11-01, SEAN's confirmed mockup B — Compact
/// Focus). The invariants: Standard is exactly the long-standing
/// density numbers (no silent change for existing users), Compact
/// Focus is slimmer than every density and trades the status bar for
/// the pill, the page-height gain the mockup promised is the gain the
/// metrics compute, and a corrupt layout value in a settings file
/// repairs to Standard instead of breaking the shell.
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
    }

    [Theory]
    [InlineData(ShellDensity.Comfortable, 34)] // (40-30) toolbar + 24 status bar
    [InlineData(ShellDensity.Compact, 22)]     // (32-30) toolbar + 20 status bar
    public void CompactFocus_PageHeightGainIsTheMockupPromise(ShellDensity density, double expected)
    {
        Assert.Equal(expected, ShellLayoutMetrics.PageHeightGain(ShellLayout.CompactFocus, density));
        Assert.Equal(0, ShellLayoutMetrics.PageHeightGain(ShellLayout.Standard, density));
    }

    [Fact]
    public void Settings_DefaultIsStandard()
    {
        Assert.Equal(ShellLayout.Standard, new AppSettings().ShellLayout);
    }

    [Fact]
    public void Settings_UndefinedLayoutRepairsToStandard()
    {
        var settings = new AppSettings { ShellLayout = (ShellLayout)99 }.Sanitized();
        Assert.Equal(ShellLayout.Standard, settings.ShellLayout);
    }

    [Fact]
    public void Settings_CompactFocusSurvivesSanitize()
    {
        var settings = new AppSettings { ShellLayout = ShellLayout.CompactFocus }.Sanitized();
        Assert.Equal(ShellLayout.CompactFocus, settings.ShellLayout);
    }
}
