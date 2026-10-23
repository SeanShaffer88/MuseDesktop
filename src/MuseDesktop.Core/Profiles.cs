using System.Text.RegularExpressions;

namespace MuseDesktop.Core;

/// <summary>
/// One sign-in profile (v0.3.0): a named WebView2 user-data folder. Each
/// profile is a separate Muse account session — cookies and local storage
/// never mix. The default profile keeps the folder the app has always used
/// (%LOCALAPPDATA%\MuseDesktop\WebView2) so existing sign-ins survive.
/// </summary>
public sealed class AccountProfile
{
    public string Id { get; set; } = Profiles.DefaultId;
    public string Name { get; set; } = "Default";
}

/// <summary>Invariants and helpers for the profile list stored in settings.</summary>
public static class Profiles
{
    public const string DefaultId = "default";
    public const int MaxProfiles = 8;

    private static readonly Regex IdShape = new("^[a-z0-9][a-z0-9_-]{0,31}$", RegexOptions.Compiled);

    public static bool IsValidId(string? id) => id is not null && IdShape.IsMatch(id);

    /// <summary>A fresh unique-ish id for a new profile.</summary>
    public static string NewId() => "p" + Guid.NewGuid().ToString("N")[..10];

    /// <summary>
    /// Repair the list in place: never empty, ids valid and unique, the
    /// default profile present, the active id pointing at a real profile.
    /// Called from AppSettings.Sanitized so every load is safe.
    /// </summary>
    public static void EnsureValid(AppSettings settings)
    {
        settings.Profiles ??= new List<AccountProfile>();
        var seen = new HashSet<string>();
        settings.Profiles.RemoveAll(p =>
            p is null || !IsValidId(p.Id) || !seen.Add(p.Id));
        if (!settings.Profiles.Any(p => p.Id == DefaultId))
            settings.Profiles.Insert(0, new AccountProfile());
        foreach (var p in settings.Profiles)
        {
            if (string.IsNullOrWhiteSpace(p.Name))
                p.Name = p.Id == DefaultId ? "Default" : "Profile";
            p.Name = p.Name.Trim();
            if (p.Name.Length > 40)
                p.Name = p.Name[..40];
        }
        while (settings.Profiles.Count > MaxProfiles)
            settings.Profiles.RemoveAt(settings.Profiles.Count - 1);
        if (!settings.Profiles.Any(p => p.Id == settings.ActiveProfileId))
            settings.ActiveProfileId = DefaultId;
    }

    /// <summary>Folder name (under %LOCALAPPDATA%\MuseDesktop) for a profile's WebView2 data.</summary>
    public static string FolderName(string profileId) =>
        profileId == DefaultId ? "WebView2" : $"WebView2-{profileId}";
}
