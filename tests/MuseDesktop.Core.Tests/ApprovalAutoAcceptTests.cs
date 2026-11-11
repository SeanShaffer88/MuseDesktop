using MuseDesktop.Core;
using Xunit;

namespace MuseDesktop.Core.Tests;

public class ApprovalAutoAcceptTests
{
    [Fact]
    public void DefaultsOff_AndOrdinarySaveLoadAndWorkingClonePreserveExplicitOptIn()
    {
        var settings = new AppSettings();
        Assert.All(settings.Profiles, p => Assert.False(p.AutoAcceptApprovalCards));
        settings.Profiles[0].AutoAcceptApprovalCards = true;
        Assert.True(settings.Clone().Profiles[0].AutoAcceptApprovalCards);
        var path = Path.Combine(Path.GetTempPath(), $"muse-approval-{Guid.NewGuid():N}.json");
        try
        {
            SettingsStore.Save(path, settings);
            Assert.True(SettingsStore.Load(path).Profiles[0].AutoAcceptApprovalCards);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void SettingsImportAndProfileCopiesNeverSilentlyGrantApprovalAuthority()
    {
        var settings = new AppSettings();
        settings.Profiles[0].AutoAcceptApprovalCards = true;
        settings.Profiles.Add(new AccountProfile { Id = Profiles.NewId(), Name = "Other", AutoAcceptApprovalCards = true });
        Assert.True(SettingsStore.TryImport(SettingsStore.Export(settings), out var imported, out var error), error);
        Assert.All(imported!.Profiles, p => Assert.False(p.AutoAcceptApprovalCards));
        var copy = Profiles.Clone(settings, settings.Profiles[0].Id);
        Assert.NotNull(copy);
        Assert.False(copy.AutoAcceptApprovalCards);
        Assert.True(settings.Profiles[0].AutoAcceptApprovalCards);
        SettingsReset.ApplyDefaults(settings);
        Assert.All(settings.Profiles, p => Assert.False(p.AutoAcceptApprovalCards));
    }

    [Theory]
    [InlineData(false, true, true)]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    public void OptOutLockOrHiddenViewPreventsRunning(bool enabled, bool unlocked, bool visible) =>
        Assert.False(ApprovalAutoAccept.CanRun(enabled, unlocked, visible, "https://muse.ai/chat/test"));

    [Theory]
    [InlineData("http://muse.ai/chat/test")]
    [InlineData("https://evil.test/chat/test")]
    [InlineData("https://muse.ai.evil.test/chat/test")]
    [InlineData("https://user:private@muse.ai/chat/test")]
    [InlineData("https://muse.ai:8443/chat/test")]
    [InlineData("https://muse.ai/auth/callback")]
    [InlineData("file:///C:/approval.html")]
    [InlineData("data:text/html,approval")]
    [InlineData(null)]
    public void UnsafeOriginsAndNonChatRoutesNeverRun(string? url) =>
        Assert.False(ApprovalAutoAccept.CanRun(true, true, true, url));

    [Theory]
    [InlineData("https://muse.ai/")]
    [InlineData("https://muse.ai/chat/test")]
    [InlineData("https://muse.ai:443/chat/test")]
    public void OnlyExactTrustedMuseHomeAndChatOriginsCanRun(string url) =>
        Assert.True(ApprovalAutoAccept.CanRun(true, true, true, url));

    [Fact]
    public void PageActionsNeedFreshNativePermission_AndDoNotReportCardContents()
    {
        Assert.DoesNotContain("setInterval", ApprovalAutoAccept.Script);
        Assert.DoesNotContain("setTimeout", ApprovalAutoAccept.Script);
        Assert.DoesNotContain("postMessage", ApprovalAutoAccept.Script);
        Assert.DoesNotContain("localStorage", ApprovalAutoAccept.Script);
        Assert.DoesNotContain(".click()", ApprovalAutoAccept.ResetPendingScript);
        var controller = Source("src/MuseDesktop/ApprovalAutoAcceptController.cs");
        Assert.Contains("InputDesktopIsUnlocked()", controller);
        Assert.Contains("IntPtr.Zero) return false", controller);
        Assert.Contains("CanAutoAcceptApprovals(_profileId)", controller);
        Assert.Contains("WindowState.Minimized", controller);
        Assert.DoesNotContain("Application.Current.MainWindow", controller);
        Assert.Contains("_window.Closed -= OnClosed", controller);
        Assert.Contains("result.Length <= 64", controller);
        var main = Source("src/MuseDesktop/MainWindow.xaml.cs");
        Assert.Contains("!_isLocked && _pendingProfileLockRecord is null", main);
        Assert.Contains("MessageBoxResult.No", Source("src/MuseDesktop/SettingsWindow.AutoApproval.cs"));
        Assert.DoesNotContain(".click()", Source("src/MuseDesktop/PageScripts.cs"));
    }

    [Fact]
    public void EveryChatSurfaceHasTheGuardedController_ButMicrophoneTestDoesNot()
    {
        foreach (var window in new[] { "MainWindow", "DualWindow", "QuickAskWindow", "CompareWindow", "SecondaryWindow" })
            Assert.Contains("ApprovalAutoAcceptController.Attach(", Source($"src/MuseDesktop/{window}.xaml.cs"));
        Assert.DoesNotContain("ApprovalAutoAcceptController.Attach(", Source("src/MuseDesktop/MicTestWindow.xaml.cs"));
    }

    private static string Source(string file)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "MuseDesktop.sln"))) dir = dir.Parent;
        return File.ReadAllText(Path.Combine(dir?.FullName ?? throw new InvalidOperationException(), file));
    }
}
