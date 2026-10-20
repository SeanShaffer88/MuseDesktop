using MuseDesktop.Core;
using Xunit;

namespace MuseDesktop.Core.Tests;

public class UrlPolicyTests
{
    [Theory]
    [InlineData("https://muse.ai/")]
    [InlineData("https://muse.ai/chat/abc")]
    [InlineData("https://www.muse.ai/")]
    [InlineData("https://chat.muse.ai/")]
    [InlineData("https://accounts.google.com/signin")]
    [InlineData("https://www.facebook.com/login")]
    [InlineData("about:blank")]
    public void NewWindow_InApp_StaysInApp(string url) =>
        Assert.Equal(LinkDisposition.InApp, UrlPolicy.ClassifyNewWindow(url));

    [Theory]
    [InlineData("https://example.com/article")]
    [InlineData("http://example.com/article")]
    [InlineData("https://www.nytimes.com/")]
    [InlineData("https://github.com/SeanShaffer88")]
    public void NewWindow_OrdinaryWeb_GoesToBrowser(string url) =>
        Assert.Equal(LinkDisposition.ExternalBrowser, UrlPolicy.ClassifyNewWindow(url));

    [Theory]
    // Look-alike hosts must never count as in-app.
    [InlineData("https://muse.ai.evil.com/")]
    [InlineData("https://evilmuse.ai/")]
    [InlineData("https://muse-ai.com/")]
    [InlineData("https://accounts.google.com.evil.com/")]
    public void NewWindow_LookAlikeHosts_GoToBrowser(string url) =>
        Assert.Equal(LinkDisposition.ExternalBrowser, UrlPolicy.ClassifyNewWindow(url));

    [Theory]
    [InlineData("file:///C:/Windows/win.ini")]
    [InlineData("javascript:alert(1)")]
    [InlineData("data:text/html,<script>alert(1)</script>")]
    [InlineData("ms-settings:display")]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("not a url")]
    public void NewWindow_DangerousOrInvalid_Blocked(string? url) =>
        Assert.Equal(LinkDisposition.Block, UrlPolicy.ClassifyNewWindow(url));

    [Theory]
    [InlineData("https://muse.ai/")]
    [InlineData("https://any-provider.example/oauth/callback")]
    [InlineData("blob:https://muse.ai/6f9619ff-8b86")]
    [InlineData("about:blank")]
    public void Navigation_HttpsFamily_Allowed(string url) =>
        Assert.Equal(LinkDisposition.InApp, UrlPolicy.ClassifyNavigation(url));

    [Theory]
    [InlineData("http://muse.ai/")] // plain HTTP is never a top-level page
    [InlineData("file:///etc/passwd")]
    [InlineData("javascript:alert(1)")]
    [InlineData("")]
    [InlineData(null)]
    public void Navigation_NonHttps_Blocked(string? url) =>
        Assert.Equal(LinkDisposition.Block, UrlPolicy.ClassifyNavigation(url));

    [Theory]
    [InlineData("MUSE.AI", true)]
    [InlineData("muse.ai.", true)] // trailing-dot FQDN form of the same host
    [InlineData("sub.muse.ai", true)]
    [InlineData("notmuse.ai", false)]
    [InlineData("muse.ai.evil.com", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void InAppHost_Matching(string? host, bool expected) =>
        Assert.Equal(expected, UrlPolicy.IsInAppHost(host));

    [Theory]
    [InlineData("https://muse.ai/", "notifications", true)]
    [InlineData("https://muse.ai/chat/1", "microphone", true)]
    [InlineData("https://muse.ai/", "camera", false)]
    [InlineData("https://muse.ai/", "geolocation", false)]
    [InlineData("https://example.com/", "notifications", false)]
    [InlineData("http://muse.ai/", "notifications", false)]
    [InlineData(null, "notifications", false)]
    public void AutoGrant_OnlyMuseNotificationsAndMic(string? origin, string kind, bool expected) =>
        Assert.Equal(expected, UrlPolicy.IsAutoGrantedPermission(origin, kind));

    [Fact]
    public void RedactForLog_StripsQueryAndFragment()
    {
        var redacted = UrlPolicy.RedactForLog("https://muse.ai/chat/1?token=SECRET#frag");
        Assert.Equal("https://muse.ai/chat/1", redacted);
        Assert.DoesNotContain("SECRET", redacted);
    }
}
