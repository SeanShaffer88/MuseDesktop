using MuseDesktop.Core;
using Xunit;

namespace MuseDesktop.Core.Tests;

// ───────────────────── R7E: the Settings rail — page model ─────────────────────

/// <summary>
/// The Settings page catalog (R7E, v1.8.6): the rail's pages and
/// which existing section each one carries. The window shows ONLY
/// the selected page's cards, so a section missing from the catalog
/// would be a section no one can ever reach — these tests pin the
/// mapping against the real section list (<see cref="SettingsSearch"/>),
/// the same drift-guard treatment the search index itself gets.
/// </summary>
public class SettingsPagesTests
{
    [Fact]
    public void Pages_AreInTheDirectedRailOrder()
    {
        Assert.Equal(
            new[]
            {
                SettingsPages.GeneralId, SettingsPages.AppearanceId, SettingsPages.ProfilesId,
                SettingsPages.WorkspacesId, SettingsPages.NotificationsId, SettingsPages.FocusId,
                SettingsPages.DownloadsId, SettingsPages.DataId, SettingsPages.LanguageId,
                SettingsPages.AccessibilityId, SettingsPages.PrivacyId,
            },
            SettingsPages.Pages.Select(p => p.Id).ToArray());
    }

    [Fact]
    public void EverySection_BelongsToExactlyOnePage()
    {
        var carried = SettingsPages.Pages.SelectMany(p => p.SectionIds).ToList();
        Assert.Equal(
            SettingsSearch.Sections.Select(s => s.Id).OrderBy(id => id),
            carried.OrderBy(id => id));
        Assert.Equal(carried.Count, carried.Distinct().Count());
    }

    [Fact]
    public void EveryPage_IsNonEmpty_WithKnownSectionsOnly()
    {
        foreach (var page in SettingsPages.Pages)
        {
            Assert.NotEmpty(page.SectionIds);
            foreach (var sectionId in page.SectionIds)
                Assert.True(SettingsSearch.IsKnownSection(sectionId),
                    $"Page {page.Id} carries unknown section {sectionId}");
        }
    }

    [Fact]
    public void PageIds_AreUnique()
    {
        var ids = SettingsPages.Pages.Select(p => p.Id).ToList();
        Assert.Equal(ids.Count, ids.Distinct().Count());
    }

    [Theory]
    [InlineData(SettingsSearch.BehaviorId, SettingsPages.GeneralId)]
    [InlineData(SettingsSearch.AppearanceId, SettingsPages.AppearanceId)]
    [InlineData(SettingsSearch.ZoomId, SettingsPages.AppearanceId)] // zoom folds into Appearance
    [InlineData(SettingsSearch.ProfilesId, SettingsPages.ProfilesId)]
    [InlineData(SettingsSearch.WorkspacesId, SettingsPages.WorkspacesId)]
    [InlineData(SettingsSearch.TrayId, SettingsPages.NotificationsId)]
    [InlineData(SettingsSearch.FocusId, SettingsPages.FocusId)]
    [InlineData(SettingsSearch.DownloadsId, SettingsPages.DownloadsId)]
    [InlineData(SettingsSearch.DataId, SettingsPages.DataId)]
    [InlineData(SettingsSearch.LanguageId, SettingsPages.LanguageId)]
    [InlineData(SettingsSearch.AccessibilityId, SettingsPages.AccessibilityId)]
    [InlineData(SettingsSearch.PrivacyId, SettingsPages.PrivacyId)]
    public void PageForSection_ResolvesEverySectionToItsPage(string sectionId, string pageId)
    {
        Assert.Equal(pageId, SettingsPages.PageForSection(sectionId).Id);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("no-such-section")]
    public void PageForSection_UnknownId_LandsOnGeneral(string? sectionId)
    {
        // The deep-link path's long-standing rule: an unrecognized
        // section is not applied, and the window opens at the top.
        Assert.Equal(SettingsPages.GeneralId, SettingsPages.PageForSection(sectionId).Id);
        Assert.Same(SettingsPages.Default, SettingsPages.PageForSection(sectionId));
    }

    [Fact]
    public void PageForId_FindsEveryPage_AndRejectsUnknownIds()
    {
        foreach (var page in SettingsPages.Pages)
            Assert.Same(page, SettingsPages.PageForId(page.Id));
        Assert.Null(SettingsPages.PageForId(null));
        Assert.Null(SettingsPages.PageForId(""));
        Assert.Null(SettingsPages.PageForId("no-such-page"));
    }

    [Fact]
    public void Default_IsGeneral_TheFirstPageInTheRail()
    {
        Assert.Equal(SettingsPages.GeneralId, SettingsPages.Default.Id);
        Assert.Same(SettingsPages.Pages[0], SettingsPages.Default);
    }

    [Fact]
    public void EveryNavTitleKey_ExistsInTheCatalog_WithAGlyph()
    {
        foreach (var page in SettingsPages.Pages)
        {
            Assert.True(StringCatalog.Source.ContainsKey(page.NavTitleKey),
                $"Page {page.Id} names catalog key {page.NavTitleKey}, which doesn't exist");
            Assert.False(string.IsNullOrWhiteSpace(StringCatalog.Source[page.NavTitleKey]),
                $"Page {page.Id}'s nav title is blank");
            // One icon codepoint, from the private-use area the
            // Segoe icon fonts live in — never a letter or a word.
            Assert.Single(page.IconGlyph);
            var c = page.IconGlyph[0];
            Assert.True(c >= '\uE000' && c <= '\uF8FF',
                $"Page {page.Id}'s glyph U+{(int)c:X4} is outside the icon fonts' private-use area");
        }
    }

    [Fact]
    public void NavGlyphs_AreDistinctPerPage()
    {
        var glyphs = SettingsPages.Pages.Select(p => p.IconGlyph).ToList();
        Assert.Equal(glyphs.Count, glyphs.Distinct().Count());
    }
}
/// <summary>
/// R7E shell wiring, in the R7C style: the Settings window must
/// actually be built on the Core page model (rail + cards), not
/// merely coexist with it.
/// </summary>
public class SettingsRailShellTests
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(
            Path.GetDirectoryName(typeof(SettingsRailShellTests).Assembly.Location)!);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "MuseDesktop.sln"))
                && Directory.Exists(Path.Combine(dir.FullName, "src", "MuseDesktop")))
                return dir.FullName;
            dir = dir.Parent;
        }
        throw new InvalidOperationException("Could not locate the repo root from the test assembly.");
    }

    private static string ShellFile(string name) =>
        File.ReadAllText(Path.Combine(RepoRoot(), "src", "MuseDesktop", name));

    [Fact]
    public void SettingsWindow_RailAndCards_AreWired()
    {
        var xaml = ShellFile("SettingsWindow.xaml");
        Assert.Contains("x:Name=\"NavList\"", xaml);
        Assert.Contains("x:Name=\"PageTitleText\"", xaml);
        // Every section rides in a card named for it (the code-behind
        // registry keys page/search visibility off these borders).
        foreach (var card in new[]
                 {
                     "CardBehavior", "CardTray", "CardProfiles", "CardWorkspaces",
                     "CardAppearance", "CardZoom", "CardAccessibility", "CardLanguage",
                     "CardDownloads", "CardData", "CardFocus", "CardPrivacy",
                 })
            Assert.Contains($"x:Name=\"{card}\"", xaml);
        var code = ShellFile("SettingsWindow.xaml.cs");
        Assert.Contains("SettingsPages.Pages", code);
        Assert.Contains("SettingsPages.PageForSection", code);
        Assert.Contains("Strings.Get(page.NavTitleKey)", code);
    }

    [Fact]
    public void SettingsWindow_RailItems_UseTheThemeTextBrush()
    {
        var xaml = ShellFile("SettingsWindow.xaml");
        // R7E2 field fix (SEAN, on v1.8.6): the rail's topic labels and
        // icons rendered BLACK on the near-black rail. WPF's built-in
        // ListBox style sets the control's text color to the system
        // window-text color, which outranks the color the list would
        // inherit from the window — so the text brush must be pinned
        // on the list itself (the pattern every other list in the
        // shell follows) AND on the item template's icon and label,
        // so no state of the item can fall back to the system color.
        var navStart = xaml.IndexOf("x:Name=\"NavList\"", StringComparison.Ordinal);
        Assert.True(navStart >= 0, "NavList not found in SettingsWindow.xaml");
        var navEnd = xaml.IndexOf("</ListBox>", navStart, StringComparison.Ordinal);
        Assert.True(navEnd > navStart, "NavList element is not closed");
        var nav = xaml[navStart..navEnd];
        // One pin on the list element, one on the glyph, one on the title.
        Assert.True(CountOccurrences(nav, "Foreground=\"{StaticResource TextBrush}\"") >= 3,
            "NavList, its icon glyph, and its title must all pin the theme text brush");
        Assert.Contains("Text=\"{Binding Glyph}\"", nav);
        Assert.Contains("Text=\"{Binding Title}\"", nav);
        // The item container style must never re-darken the text in any
        // state (hover, selected, disabled): it sets no Foreground at
        // all — its triggers only repaint the pill's background.
        var styleStart = xaml.IndexOf("x:Key=\"NavItemStyle\"", StringComparison.Ordinal);
        Assert.True(styleStart >= 0, "NavItemStyle not found in SettingsWindow.xaml");
        var styleEnd = xaml.IndexOf("</Style>", styleStart, StringComparison.Ordinal);
        var style = xaml[styleStart..styleEnd];
        Assert.DoesNotContain("Foreground", style);
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        var count = 0;
        var at = 0;
        while ((at = haystack.IndexOf(needle, at, StringComparison.Ordinal)) >= 0)
        {
            count++;
            at += needle.Length;
        }
        return count;
    }
}
