namespace MuseDesktop.Core;

// ─────────────────────── quick-attach tray (v2.1.0) ───────────────────────
//
// Concept B (SEAN delegated the pick, 2026-11-02): a slim strip in
// the shell above the message box holding the user's recent local
// files — the newest things in Downloads and in Pictures\Screenshots
// — so attaching one to the open conversation is a click (or a drag
// onto the page) instead of a file-picker hunt. The shell owns the
// strip, the clipboard and the attach; this file owns the part that
// can be judged without a window: which folders feed the strip,
// which files count as recent and attachable, in what order, and
// how old each one is in words a chip can carry.
//
// Privacy shape (see DECISIONS, 2026-11-02): the strip reads file
// NAMES and metadata (size, modified time) from two ordinary user
// folders — never file contents — and keeps them in memory only.
// Nothing here is logged or stored; the shell never logs a path.

/// <summary>What a recent file is, for the chip's icon: a picture, a document, or anything else.</summary>
public enum RecentFileKind
{
    Image,
    Document,
    Other,
}

/// <summary>One file the quick-attach strip can offer: its path (the attach needs it; it is never logged), its display name, its normalized extension (no dot, lower case), its size and last-write time, and its <see cref="RecentFileKind"/>.</summary>
public sealed record RecentFileEntry(
    string FullPath,
    string Name,
    string Extension,
    long SizeBytes,
    DateTimeOffset ModifiedUtc,
    RecentFileKind Kind);

/// <summary>The unit a chip's age is spoken in — "now", minutes, hours, or days (see <see cref="RecentFiles.AgeParts"/>).</summary>
public enum RecentFileAgeUnit
{
    Now,
    Minutes,
    Hours,
    Days,
}

/// <summary>
/// The quick-attach strip's collection rules: the source folders,
/// the merge (newest first, deduped by path, capped), the
/// attachable-extension list — the ONE place that list lives, so
/// the strip, the Add files picker, and the attach itself can
/// never drift apart — and the age maths behind each chip's
/// "5 min ago".
/// </summary>
public static class RecentFiles
{
    /// <summary>The most files the strip ever holds — a shelf, not a file manager.</summary>
    public const int MaxEntries = 12;

    private static readonly HashSet<string> ImageExtensions =
        new(StringComparer.OrdinalIgnoreCase) { "png", "jpg", "jpeg", "gif", "webp", "bmp" };

    private static readonly HashSet<string> DocumentExtensions =
        new(StringComparer.OrdinalIgnoreCase) { "pdf", "txt", "md", "docx", "doc", "rtf" };

    /// <summary>
    /// What the page's upload takes, by extension — the images the
    /// composer accepts as pictures plus the common document types.
    /// Deliberately narrower than <see cref="RecentFileKind"/>:
    /// a .bmp is recognizably an image (it gets the picture icon)
    /// but is not on the upload's list, so it never reaches the
    /// strip at all.
    /// </summary>
    private static readonly HashSet<string> AttachableExtensions =
        new(StringComparer.OrdinalIgnoreCase)
        { "png", "jpg", "jpeg", "gif", "webp", "pdf", "txt", "md", "docx" };

    /// <summary>The attachable extensions, sorted — the Add files picker's filter is built from this, so it always matches <see cref="IsAttachable"/>.</summary>
    public static IReadOnlyList<string> AttachableExtensionList =>
        AttachableExtensions.OrderBy(e => e, StringComparer.Ordinal).ToArray();

    /// <summary>
    /// An extension with the noise removed: "photo.PNG", ".png"
    /// and "png" all normalize to "png". A name with no dot
    /// normalizes to itself, lower-cased; null/blank to "".
    /// </summary>
    public static string NormalizeExtension(string? pathOrExtension)
    {
        if (string.IsNullOrWhiteSpace(pathOrExtension))
            return "";
        var trimmed = pathOrExtension.Trim();
        var dot = trimmed.LastIndexOf('.');
        var ext = dot >= 0 ? trimmed[(dot + 1)..] : trimmed;
        return ext.Trim().ToLowerInvariant();
    }

    /// <summary>True when a file (by path or bare extension) is one the page's upload takes — the strip's membership test and the attach's last check.</summary>
    public static bool IsAttachable(string? pathOrExtension) =>
        AttachableExtensions.Contains(NormalizeExtension(pathOrExtension));

    /// <summary>The chip-icon kind for an extension (by path or bare extension): image, document, or other.</summary>
    public static RecentFileKind KindOf(string? pathOrExtension)
    {
        var ext = NormalizeExtension(pathOrExtension);
        if (ImageExtensions.Contains(ext))
            return RecentFileKind.Image;
        if (DocumentExtensions.Contains(ext))
            return RecentFileKind.Document;
        return RecentFileKind.Other;
    }

    /// <summary>
    /// The folders that feed the strip: the user's Downloads, and
    /// Pictures\Screenshots when the user keeps screenshots there.
    /// Existence is the caller's question — <see cref="Collect"/>
    /// skips a missing folder quietly. (The app's own screenshot
    /// temp folder is NOT a source: slice 5 deletes those files by
    /// design the moment they are attached or abandoned.)
    /// </summary>
    public static IReadOnlyList<string> DefaultSourceFolders()
    {
        var folders = new List<string> { AppPaths.DownloadsHint };
        var pictures = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
        if (!string.IsNullOrWhiteSpace(pictures))
            folders.Add(Path.Combine(pictures, "Screenshots"));
        return folders;
    }

    /// <summary>
    /// Merge the sources' attachable files into the strip's list:
    /// top level of each folder only, newest first by last-write
    /// time (ties break by path, so the order is stable), deduped
    /// by full path (the same folder listed twice, or a file two
    /// sources both name, appears once), capped at
    /// <paramref name="limit"/>. A folder that is missing, unreadable,
    /// or vanishes mid-scan is skipped quietly, and so is any file
    /// that vanishes or can't be stated — the strip is a
    /// convenience, never an error.
    /// </summary>
    public static IReadOnlyList<RecentFileEntry> Collect(
        IEnumerable<string?> sources, int limit = MaxEntries)
    {
        if (limit <= 0)
            return Array.Empty<RecentFileEntry>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var entries = new List<RecentFileEntry>();
        foreach (var source in sources)
        {
            if (string.IsNullOrWhiteSpace(source))
                continue;
            try
            {
                if (!Directory.Exists(source))
                    continue;
                foreach (var file in Directory.EnumerateFiles(source))
                {
                    try
                    {
                        var info = new FileInfo(file);
                        if (!info.Exists)
                            continue;
                        var extension = NormalizeExtension(info.Extension);
                        if (!AttachableExtensions.Contains(extension))
                            continue;
                        if (!seen.Add(info.FullName))
                            continue;
                        entries.Add(new RecentFileEntry(
                            info.FullName,
                            info.Name,
                            extension,
                            info.Length,
                            new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero),
                            KindOf(extension)));
                    }
                    catch (Exception)
                    {
                        // A file that vanished or can't be
                        // stated simply isn't offered.
                    }
                }
            }
            catch (Exception)
            {
                // A folder that can't be read is skipped, like a
                // missing one.
            }
        }
        return entries
            .OrderByDescending(e => e.ModifiedUtc)
            .ThenBy(e => e.FullPath, StringComparer.OrdinalIgnoreCase)
            .Take(limit)
            .ToArray();
    }

    /// <summary>
    /// A file's age in chip words: under a minute (or in the
    /// future — a clock disagreement is not worth a negative
    /// number) is "now"; then whole minutes, whole hours, and
    /// whole days past those.
    /// </summary>
    public static (RecentFileAgeUnit Unit, int Count) AgeParts(
        DateTimeOffset modifiedUtc, DateTimeOffset nowUtc)
    {
        var age = nowUtc - modifiedUtc;
        if (age < TimeSpan.FromMinutes(1))
            return (RecentFileAgeUnit.Now, 0);
        if (age < TimeSpan.FromHours(1))
            return (RecentFileAgeUnit.Minutes, (int)age.TotalMinutes);
        if (age < TimeSpan.FromDays(1))
            return (RecentFileAgeUnit.Hours, (int)age.TotalHours);
        return (RecentFileAgeUnit.Days, (int)age.TotalDays);
    }
}
