namespace MuseDesktop.Core;

/// <summary>The outcome of one <see cref="SavedPasswordSweep"/> run: how many saved-password files were found, deleted, and couldn't be deleted.</summary>
public sealed record SavedPasswordSweepOutcome(int Found, int Deleted, int Failed);

/// <summary>
/// The saved-password sweep (v1.8.8, R7F — the owner's decision:
/// passwords are never saved in this app). Versions before v1.8.8
/// let WebView2 save passwords; those live in a per-profile SQLite
/// database named exactly <c>Login Data</c> (plus its <c>-journal</c>
/// sidecar). This sweep deletes exactly those files — nothing else,
/// ever:
///
/// - Only the two file names above. Cookies (what keeps the user
///   signed in), history, caches, and every other file are never in
///   the set — the names are constants, never patterns, so no
///   wildcard can widen the sweep.
/// - Only inside a profile's WebView2 folder (<c>WebView2</c> /
///   <c>WebView2-&lt;id&gt;</c> under the profiles root — see
///   <see cref="AppPaths.ProfilesDataRoot"/>), and inside such a
///   folder only in the two places Chromium keeps the database:
///   the folder itself and its <c>Default</c> profile subfolder.
///   The sweep never recurses anywhere else.
/// - A profile folder that doesn't exist, a missing file, and a
///   file that can't be deleted (still in use) are all tolerated:
///   the run counts what it couldn't do and moves on — the next
///   launch (or the installer) tries again.
///
/// The shell runs it once at startup, before any WebView2 exists
/// (it covers portable-zip copies, which never run an installer);
/// the installer performs the same wipe with the same file set.
/// </summary>
public static class SavedPasswordSweep
{
    /// <summary>The stored-password database's file name (Chromium's, exact).</summary>
    public const string LoginDataFileName = "Login Data";

    /// <summary>The stored-password database's journal sidecar (exact).</summary>
    public const string LoginDataJournalFileName = "Login Data-journal";

    /// <summary>The subfolder Chromium keeps a WebView2 profile's databases in.</summary>
    public const string ProfileSubfolderName = "Default";

    /// <summary>True when a folder name under the profiles root is a profile's WebView2 folder (see Profiles.FolderName).</summary>
    public static bool IsProfileFolderName(string? name) =>
        name is not null
        && (name == "WebView2"
            || name.StartsWith("WebView2-", StringComparison.Ordinal));

    /// <summary>
    /// The exact paths to delete under <paramref name="profilesRoot"/>:
    /// the two saved-password files, in the folder itself and in its
    /// <c>Default</c> subfolder, for every profile folder present.
    /// Only files that exist are listed. Never throws — a missing or
    /// unreadable root simply yields an empty list.
    /// </summary>
    public static IReadOnlyList<string> FindFilesToDelete(string profilesRoot)
    {
        var result = new List<string>();
        try
        {
            if (string.IsNullOrWhiteSpace(profilesRoot) || !Directory.Exists(profilesRoot))
                return result;
            var profileDirs = Directory.GetDirectories(profilesRoot)
                .Where(dir => IsProfileFolderName(Path.GetFileName(dir)))
                .OrderBy(dir => dir, StringComparer.Ordinal);
            foreach (var profileDir in profileDirs)
            {
                foreach (var folder in new[] { profileDir, Path.Combine(profileDir, ProfileSubfolderName) })
                {
                    foreach (var name in new[] { LoginDataFileName, LoginDataJournalFileName })
                    {
                        var path = Path.Combine(folder, name);
                        if (File.Exists(path))
                            result.Add(path);
                    }
                }
            }
        }
        catch
        {
            // Selection must never take startup down — what was
            // gathered before the failure still stands.
        }
        return result;
    }

    /// <summary>
    /// Delete exactly what <see cref="FindFilesToDelete"/> selects
    /// under <paramref name="profilesRoot"/>. Each file is attempted
    /// individually; a locked or stubborn file counts as failed and
    /// is left for the next run. Never throws.
    /// </summary>
    public static SavedPasswordSweepOutcome Run(string profilesRoot)
    {
        var files = FindFilesToDelete(profilesRoot);
        var deleted = 0;
        var failed = 0;
        foreach (var path in files)
        {
            try
            {
                File.Delete(path);
                deleted++;
            }
            catch
            {
                failed++;
            }
        }
        return new SavedPasswordSweepOutcome(files.Count, deleted, failed);
    }
}
