using System.Xml.Linq;
using MuseDesktop.Core;
using Xunit;

namespace MuseDesktop.Core.Tests;

public class DualModeTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not an address")]
    [InlineData("javascript:alert(1)")]
    [InlineData("file:///C:/secret.txt")]
    [InlineData("http://muse.ai/chat/one")]
    [InlineData("https://muse.ai.evil.test/chat/one")]
    [InlineData("https://other.muse.ai/chat/one")]
    [InlineData("https://accounts.google.com/callback?code=private")]
    [InlineData("https://user:password@muse.ai/chat/one")]
    [InlineData("https://muse.ai:8443/chat/one")]
    public void UnsafeExternalAndSignInPagesStartBothPanesAtHome(string? url)
    {
        var (left, right) = DualMode.InitialUrls(url);
        Assert.Equal(UrlPolicy.HomeUrl, left);
        Assert.Equal(UrlPolicy.HomeUrl, right);
    }

    [Theory]
    [InlineData("https://muse.ai/chat/one")]
    [InlineData("https://muse.ai/chat/two?view=latest#message")]
    [InlineData("https://muse.ai/")]
    public void CurrentMusePageOpensOnlyOnLeftWhileRightStartsFresh(string url)
    {
        var (left, right) = DualMode.InitialUrls(url);
        Assert.Equal(url, left);
        Assert.Equal(UrlPolicy.HomeUrl, right);
    }

    [Fact]
    public void CommandPaletteOffersDualAndCompareAsDifferentActions()
    {
        var palette = CommandRegistry.CreateDefault(Array.Empty<AccountProfile>(), "default",
            Array.Empty<DownloadRecord>(), Array.Empty<WindowDescriptor>());
        var dual = Assert.Single(palette.Search("dual"), r => r.Command.Id == "action.dual-mode");
        Assert.Equal(PaletteCommandSource.Action, dual.Command.Source);
        Assert.Null(dual.Command.Payload);
        Assert.Contains(palette.All(), c => c.Id == "action.compare-mode");
    }

    private static string Root()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "MuseDesktop.sln")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Repository not found.");
    }

    [Fact]
    public void ShellUsesTwoSeparateHostsAndAResizableDividerWithoutSharedComposer()
    {
        var xaml = XDocument.Load(Path.Combine(Root(), "src/MuseDesktop/DualWindow.xaml"));
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var elements = xaml.Descendants().ToList();
        Assert.Contains(elements, e => (string?)e.Attribute(x + "Name") == "LeftHost");
        Assert.Contains(elements, e => (string?)e.Attribute(x + "Name") == "RightHost");
        var splitter = Assert.Single(elements, e => e.Name.LocalName == "GridSplitter");
        Assert.Equal("PreviousAndNext", (string?)splitter.Attribute("ResizeBehavior"));
        Assert.NotNull(splitter.Attribute("AutomationProperties.Name"));
        Assert.DoesNotContain(elements, e => e.Name.LocalName == "TextBox");
        foreach (var handler in new[] { "OnBack", "OnForward", "OnReload", "OnNewChat", "OnOpenMain" })
            Assert.Equal(2, elements.Count(e => (string?)e.Attribute("Click") == handler));
    }

    [Fact]
    public void ShellHasNoBroadcastScriptAndDisposesBothViewsWhenClosed()
    {
        var source = File.ReadAllText(Path.Combine(Root(), "src/MuseDesktop/DualWindow.xaml.cs"));
        Assert.DoesNotContain("ExecuteScriptAsync", source);
        Assert.DoesNotContain("BuildSendScript", source);
        Assert.DoesNotContain("PageScripts", source);
        Assert.Contains("Task.WhenAll", source);
        Assert.Contains("new[] { _left, _right }", source);
        Assert.Contains("browser.Dispose()", source);
        Assert.Contains("UrlPolicy.ClassifyNavigation", source);
        Assert.Contains("UrlPolicy.ClassifyNewWindow", source);
        Assert.Contains("IsPasswordAutosaveEnabled = false", source);
        Assert.Contains("CoreWebView2ServerCertificateErrorAction.Cancel", source);
    }

    [Fact]
    public void EntryPointIsLockGatedAndReusesItsExistingWindow()
    {
        var source = File.ReadAllText(Path.Combine(Root(), "src/MuseDesktop/MainWindow.xaml.cs"));
        var start = source.IndexOf("private void OpenDualMode()", StringComparison.Ordinal);
        Assert.True(start >= 0);
        var end = source.IndexOf("private void OpenCompareMode()", start, StringComparison.Ordinal);
        var handler = source[start..end];
        Assert.Contains("if (_isLocked)", handler);
        Assert.Contains("PresentLockWindow()", handler);
        Assert.Contains("if (_dualWindow is not null)", handler);
        Assert.Contains("Owner = this", handler);
        Assert.Contains("window.Closed +=", handler);
    }

    [Theory]
    [InlineData("en-US")]
    [InlineData("es-ES")]
    public void DualLabelsAreTranslatedWithoutFallback(string language)
    {
        var localizer = StringCatalog.CreateLocalizer(language, "en-US");
        foreach (var key in StringCatalog.Source.Keys.Where(k => k.StartsWith("dual.", StringComparison.Ordinal)))
            Assert.NotEqual(key, localizer.Get(key));
        Assert.NotEqual(StringCatalog.CreateLocalizer("en-US", "en-US").Get("dual.menu"),
            StringCatalog.CreateLocalizer("es-ES", "en-US").Get("dual.menu"));
    }
}
