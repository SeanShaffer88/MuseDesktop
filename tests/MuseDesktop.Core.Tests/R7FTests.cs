using MuseDesktop.Core;
using Xunit;

namespace MuseDesktop.Core.Tests;

// ───────────────────── R7F: passwords are never saved ─────────────────────

/// <summary>
/// The settings-model half of R7F (v1.8.8): password autosave is a
/// rule, not a default — a settings file written by an older version
/// with the old toggle ON must load with the value false, on every
/// load path. General autofill is different by design: only its
/// fresh-install default changed (to off); a stored choice is the
/// user's and is respected exactly as stored.
/// </summary>
public class PasswordNeverSavedSettingsTests
{
    [Fact]
    public void Defaults_PasswordAutosaveAndAutofill_AreBothOff()
    {
        var fresh = new AppSettings();
        Assert.False(fresh.PasswordAutosaveEnabled);
        Assert.False(fresh.GeneralAutofillEnabled);
    }

    [Fact]
    public void StoredPasswordTrue_IsNeverHonored_OnAnyLoadPath()
    {
        // Direct sanitize of a hand-built object.
        var built = new AppSettings { PasswordAutosaveEnabled = true };
        Assert.False(built.Sanitized().PasswordAutosaveEnabled);

        // Import of an old settings file (both load paths sanitize).
        const string oldJson = """{ "passwordAutosaveEnabled": true, "generalAutofillEnabled": true }""";
        Assert.True(SettingsStore.TryImport(oldJson, out var imported, out _));
        Assert.False(imported!.PasswordAutosaveEnabled);
        // …while the stored autofill choice IS respected.
        Assert.True(imported.GeneralAutofillEnabled);
    }

    [Fact]
    public void StoredAutofillChoices_RoundTripExactly()
    {
        Assert.True(SettingsStore.TryImport("""{ "generalAutofillEnabled": true }""", out var on, out _));
        Assert.True(on!.GeneralAutofillEnabled);
        Assert.True(SettingsStore.TryImport("""{ "generalAutofillEnabled": false }""", out var off, out _));
        Assert.False(off!.GeneralAutofillEnabled);
    }
}

// ───────────────────── R7F: the saved-password sweep ─────────────────────

/// <summary>
/// The sweep's safety shape (Core <see cref="SavedPasswordSweep"/>):
/// the delete set is exactly the two stored-password file names, in
/// the two places Chromium keeps them inside a profile's WebView2
/// folder — and nothing else, however the tree is planted. Cookies
/// (sign-ins) are the canary: any test that finds one deleted fails
/// the slice's core promise.
/// </summary>
public class SavedPasswordSweepTests
{
    private static DirectoryInfo NewRoot() =>
        Directory.CreateTempSubdirectory("musedesktop-r7f");

    private static string Plant(string root, string relative)
    {
        // Normalize the planted path to this OS's separator: the sweep
        // builds its paths with Path.Combine (backslashes on Windows),
        // so an expected path written with forward slashes would differ
        // as a STRING on Windows even though it names the same file.
        // (This is why this test passed on Linux and failed on the
        // Windows CI runner — v1.8.8, PR #31's first run.)
        var path = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "planted");
        return path;
    }

    [Fact]
    public void Selection_IsExactlyThePasswordFiles_InProfileFoldersOnly()
    {
        var root = NewRoot();
        var expected = new[]
        {
            Plant(root.FullName, "WebView2/Login Data"),
            Plant(root.FullName, "WebView2/Login Data-journal"),
            Plant(root.FullName, "WebView2/Default/Login Data"),
            Plant(root.FullName, "WebView2/Default/Login Data-journal"),
            Plant(root.FullName, "WebView2-work/Login Data"),
            Plant(root.FullName, "WebView2-work/Default/Login Data-journal"),
        };
        // Everything below must survive: cookies in every known
        // place, other browser files, a same-named file in a folder
        // that is not a profile folder, and a same-named file buried
        // deeper inside a profile folder than the sweep ever looks.
        var survivors = new[]
        {
            Plant(root.FullName, "WebView2/Cookies"),
            Plant(root.FullName, "WebView2/Default/Cookies"),
            Plant(root.FullName, "WebView2/Default/Network/Cookies"),
            Plant(root.FullName, "WebView2/Default/History"),
            Plant(root.FullName, "WebView2-work/Default/Network/Cookies"),
            Plant(root.FullName, "SomethingElse/Login Data"),
            Plant(root.FullName, "WebView2/Default/Deep/Nest/Login Data"),
        };

        var selected = SavedPasswordSweep.FindFilesToDelete(root.FullName);
        Assert.Equal(
            expected.OrderBy(p => p, StringComparer.Ordinal),
            selected.OrderBy(p => p, StringComparer.Ordinal));

        var outcome = SavedPasswordSweep.Run(root.FullName);
        Assert.Equal(new SavedPasswordSweepOutcome(expected.Length, expected.Length, 0), outcome);
        Assert.All(expected, path => Assert.False(File.Exists(path)));
        Assert.All(survivors, path => Assert.True(File.Exists(path)));
    }

    [Fact]
    public void MissingOrEmptyTrees_AreTolerated()
    {
        var missing = Path.Combine(Path.GetTempPath(), "musedesktop-r7f-missing-" + Guid.NewGuid().ToString("N"));
        Assert.Empty(SavedPasswordSweep.FindFilesToDelete(missing));
        Assert.Equal(new SavedPasswordSweepOutcome(0, 0, 0), SavedPasswordSweep.Run(missing));

        var root = NewRoot();
        Assert.Empty(SavedPasswordSweep.FindFilesToDelete(root.FullName));
        Assert.Equal(new SavedPasswordSweepOutcome(0, 0, 0), SavedPasswordSweep.Run(root.FullName));
    }

    [Fact]
    public void ProfileFolderNames_MatchTheAppPathsLayout()
    {
        // The names AppPaths/Profiles actually produce — and the
        // near-misses that must NOT count as profile folders.
        Assert.True(SavedPasswordSweep.IsProfileFolderName(Profiles.FolderName(Profiles.DefaultId)));
        Assert.True(SavedPasswordSweep.IsProfileFolderName(Profiles.FolderName("work")));
        Assert.False(SavedPasswordSweep.IsProfileFolderName("WebView2Backup"));
        Assert.False(SavedPasswordSweep.IsProfileFolderName("webview2"));
        Assert.False(SavedPasswordSweep.IsProfileFolderName(null));
        Assert.False(SavedPasswordSweep.IsProfileFolderName(""));
    }

    [Fact]
    public void ADirectoryNamedLoginData_IsNotAFileToDelete()
    {
        var root = NewRoot();
        Directory.CreateDirectory(Path.Combine(root.FullName, "WebView2", "Default", "Login Data"));
        Assert.Empty(SavedPasswordSweep.FindFilesToDelete(root.FullName));
    }
}

// ───────────────────── R7F: the support bundle never carries browser data ─────────────────────

/// <summary>
/// The bundle audit (R7F): the support bundle's contents are a
/// closed list fixed in Core, and the shell writes exactly that
/// list. This guard plants a profile-shaped tree full of browser
/// data and proves none of it can be in the bundle: no file name
/// from the tree appears among the bundle's names, and the
/// sanitized settings JSON carries no stored-password trace.
/// </summary>
public class SupportBundleBrowserDataTests
{
    [Fact]
    public void NoBrowserFile_FromAPlantedProfileTree_IsInTheBundle()
    {
        var root = Directory.CreateTempSubdirectory("musedesktop-r7f-bundle");
        var planted = new[]
        {
            "WebView2/Default/Login Data",
            "WebView2/Default/Login Data-journal",
            "WebView2/Default/Network/Cookies",
            "WebView2/Default/Cookies",
            "WebView2/Default/History",
            "WebView2/Default/Web Data",
            "WebView2-work/Default/Login Data",
        };
        foreach (var relative in planted)
        {
            var path = Path.Combine(root.FullName, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "browser data");
        }

        var bundleNames = SupportBundle.Contents(includeScreenshot: false)
            .Concat(SupportBundle.Contents(includeScreenshot: true))
            .Select(f => f.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var relative in planted)
            Assert.DoesNotContain(Path.GetFileName(relative), bundleNames);

        // The statement the user reads says cookies stay home.
        Assert.Contains("cookies", SupportBundle.ExclusionsStatement);
    }

    [Fact]
    public void BundleSettingsJson_CarriesNoPasswordMaterial()
    {
        // Even with the legacy field planted true in the source
        // object, the sanitized bundle copy carries it false —
        // and, as ever, no lock record rides along.
        var settings = new AppSettings
        {
            PasswordAutosaveEnabled = true,
            AppLockEnabled = true,
            AppLockSalt = "AAECAwQFBgcICQoLDA0ODw==",
            AppLockHash = "AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh8=",
            AppLockIterations = 210000,
        };
        var json = SupportBundle.SettingsJson(settings);
        Assert.DoesNotContain("\"passwordAutosaveEnabled\": true", json);
        Assert.DoesNotContain("AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh8=", json);
    }
}

// ───────────────────── R7F: the shell wiring (drift guards) ─────────────────────

/// <summary>
/// The wiring guards, in the R7C style: the rule lives or dies on
/// three shell facts — the web view's password autosave is set OFF
/// outright (never from the setting), the startup sweep actually
/// runs, and the Settings toggle is really gone while the
/// stored-where explainer is really there. If any link is cut, the
/// feature silently dies — these tests make it fail loudly.
/// </summary>
public class PasswordHardeningWiringTests
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(
            Path.GetDirectoryName(typeof(PasswordHardeningWiringTests).Assembly.Location)!);
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
    public void MainWindow_AppliesPasswordAutosaveOff_Unconditionally()
    {
        var main = ShellFile("MainWindow.xaml.cs");
        // Both application points (init + live apply) hard-code off,
        // and NOTHING in the shell applies the stored setting.
        Assert.Equal(2, main.Split("IsPasswordAutosaveEnabled = false").Length - 1);
        Assert.DoesNotContain("IsPasswordAutosaveEnabled = _settings", main);
        Assert.DoesNotContain("IsPasswordAutosaveEnabled = clone", main);
    }

    [Fact]
    public void AppStartup_RunsTheSweep()
    {
        var app = ShellFile("App.xaml.cs");
        Assert.Contains("SavedPasswordSweep.Run(AppPaths.ProfilesDataRoot)", app);
    }

    [Fact]
    public void SettingsWindow_ToggleIsGone_ExplainerIsThere()
    {
        var xaml = ShellFile("SettingsWindow.xaml");
        Assert.DoesNotContain("PasswordAutosaveCheck", xaml);
        Assert.Contains("musedesktop_never_saves_passwords_the_web_view", xaml);
        var code = ShellFile("SettingsWindow.xaml.cs");
        Assert.DoesNotContain("PasswordAutosaveCheck", code);
    }
}
