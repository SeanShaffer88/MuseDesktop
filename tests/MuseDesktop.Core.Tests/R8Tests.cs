using System.Text.Json;
using MuseDesktop.Core;
using Xunit;

namespace MuseDesktop.Core.Tests;

// ───────────────────── R8: the approval toast's fixed texts ─────────────────────

/// <summary>
/// The toast messages (R8): the tray configuration NotifyKit is
/// handed (the app identity the toast carries) and the show
/// request (a fixed title and body — what the card said never
/// crosses into the notification). Both are pinned as parsed JSON
/// so a careless edit to a field name or the identity fails loudly
/// here instead of silently breaking toasts in the field.
/// </summary>
public class ApprovalToastMessagesTests
{
    [Fact]
    public void TrayConfig_CarriesTheAppIdentity()
    {
        using var doc = JsonDocument.Parse(ApprovalToastMessages.BuildTrayConfig());
        var root = doc.RootElement;
        Assert.Equal(0, root.GetProperty("hwnd").GetInt32());
        Assert.Equal("SeanShaffer88.MuseDesktop", root.GetProperty("app_id").GetString());
        Assert.Equal(ApprovalToastMessages.AppId, root.GetProperty("app_id").GetString());
        Assert.Equal("MuseDesktop", root.GetProperty("tooltip").GetString());
    }

    [Fact]
    public void ShowJson_HasTheExactTitleAndBody()
    {
        using var doc = JsonDocument.Parse(ApprovalToastMessages.BuildShowJson());
        var root = doc.RootElement;
        Assert.Equal("Approval needed", root.GetProperty("title").GetString());
        Assert.Equal("Muse is waiting for your approval in MuseDesktop.",
            root.GetProperty("body").GetString());
    }
}

// ───────────────────── R8: the setting ─────────────────────

/// <summary>
/// The approval-toast setting: on by default, survives the JSON
/// round-trip the settings store and the Settings window's clone
/// both use, and a settings reset returns it to on (with the beep).
/// </summary>
public class ApprovalToastSettingsTests
{
    [Fact]
    public void Default_IsOn()
    {
        Assert.True(new AppSettings().ApprovalToastEnabled);
    }

    [Fact]
    public void JsonRoundTrip_KeepsBothWays()
    {
        var off = JsonSerializer.Deserialize<AppSettings>(
            JsonSerializer.Serialize(new AppSettings { ApprovalToastEnabled = false }));
        Assert.NotNull(off);
        Assert.False(off.ApprovalToastEnabled);
        var on = JsonSerializer.Deserialize<AppSettings>(
            JsonSerializer.Serialize(new AppSettings { ApprovalToastEnabled = true }));
        Assert.NotNull(on);
        Assert.True(on.ApprovalToastEnabled);
    }

    [Fact]
    public void Clone_RoundTripsBothWays()
    {
        Assert.False(new AppSettings { ApprovalToastEnabled = false }.Clone().ApprovalToastEnabled);
        Assert.True(new AppSettings { ApprovalToastEnabled = true }.Clone().ApprovalToastEnabled);
    }

    [Fact]
    public void Reset_ReturnsTheToastToOn()
    {
        var settings = new AppSettings { ApprovalToastEnabled = false };
        SettingsReset.ApplyDefaults(settings);
        Assert.True(settings.ApprovalToastEnabled);
    }
}

// ───────────────────── R8: the wiring (shell source guards) ─────────────────────

/// <summary>
/// End-to-end wiring pins, in the R7 guard style: the shell fans a
/// policy verdict out to beep and toast channels, the Settings
/// checkbox exists and is loaded/harvested/adopted, and the
/// vendored DLL ships beside the exe. If any link is cut, the
/// feature silently dies — these tests make it fail loudly instead.
/// </summary>
public class ApprovalToastWiringTests
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(
            Path.GetDirectoryName(typeof(ApprovalToastWiringTests).Assembly.Location)!);
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
    public void Shell_AsksThePolicyWithEitherChannel_AndFansOut()
    {
        var main = ShellFile("MainWindow.xaml.cs");
        // Both call sites judge with beep-or-toast, so a toast-only
        // user still gets alerts and a beep-only user is unchanged.
        Assert.Contains("_settings.ApprovalBeepEnabled || _settings.ApprovalToastEnabled", main);
        Assert.Contains("_settings.ApprovalToastEnabled = clone.ApprovalToastEnabled", main);
        Assert.Contains("NotifyKitToastNotifier", main);
        Assert.Contains("AppUserModelId.Ensure", main);
    }

    [Fact]
    public void Shell_NotifierPinsTheVerifiedNativeSignatures()
    {
        var notifier = ShellFile("NotifyKitToastNotifier.cs");
        Assert.Contains("notify_kit.dll", notifier);
        Assert.Contains("no_tray_create", notifier);
        Assert.Contains("no_tray_set_backend", notifier);
        Assert.Contains("no_tray_show", notifier);
        Assert.Contains("no_tray_remove", notifier);
        Assert.Contains("no_last_error", notifier);
        Assert.Contains("no_free_string", notifier);
    }

    [Fact]
    public void Shell_ProjectShipsTheVendoredDll()
    {
        var csproj = ShellFile("MuseDesktop.csproj");
        Assert.Contains("Native\\notify_kit.dll", csproj);
        Assert.True(File.Exists(Path.Combine(
            RepoRoot(), "src", "MuseDesktop", "Native", "notify_kit.dll")));
    }

    [Fact]
    public void Settings_CheckboxExistsAndIsLoadedAndHarvested()
    {
        var xaml = ShellFile("SettingsWindow.xaml");
        Assert.Contains("ApprovalToastCheck", xaml);
        Assert.Contains("approval.toast_label", xaml);
        var code = ShellFile("SettingsWindow.xaml.cs");
        Assert.Contains("ApprovalToastCheck.IsChecked = _working.ApprovalToastEnabled", code);
        Assert.Contains("_working.ApprovalToastEnabled = ApprovalToastCheck.IsChecked == true", code);
        Assert.Contains("target.ApprovalToastEnabled = source.ApprovalToastEnabled", code);
    }
}
