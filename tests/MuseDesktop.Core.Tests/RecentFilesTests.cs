using MuseDesktop.Core;
using Xunit;

namespace MuseDesktop.Core.Tests;

// ───────────────────── Quick-attach tray (v2.1.0) ─────────────────────
//
// The shell owns the strip; these tests pin the collection behind
// it: the merge across source folders (newest first by last-write
// time, deduped by full path, capped), missing folders skipped
// quietly, only attachable extensions offered at all, the one
// attachable list (IsAttachable + KindOf), and the age maths behind
// each chip's "5 min ago".

public class RecentFilesTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "md-recentfiles-" + Guid.NewGuid().ToString("N"));
    private readonly string _downloads;
    private readonly string _shots;

    public RecentFilesTests()
    {
        _downloads = Path.Combine(_root, "Downloads");
        _shots = Path.Combine(_root, "Screenshots");
        Directory.CreateDirectory(_downloads);
        Directory.CreateDirectory(_shots);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (Exception) { /* temp cleanup is best-effort */ }
    }

    private string WriteFile(string folder, string name, DateTime utcModified)
    {
        var path = Path.Combine(folder, name);
        File.WriteAllText(path, "x");
        File.SetLastWriteTimeUtc(path, utcModified);
        return path;
    }

    private static readonly DateTime Base = new(2026, 11, 2, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Collect_MergesSourcesNewestFirst()
    {
        WriteFile(_downloads, "old.png", Base);
        var newest = WriteFile(_shots, "newest.pdf", Base.AddHours(2));
        var middle = WriteFile(_downloads, "middle.txt", Base.AddHours(1));

        var entries = RecentFiles.Collect(new[] { _downloads, _shots });

        Assert.Equal(new[] { newest, middle, Path.Combine(_downloads, "old.png") },
            entries.Select(e => e.FullPath).ToArray());
    }

    [Fact]
    public void Collect_CarriesNameSizeTimeAndKind()
    {
        var path = WriteFile(_shots, "shot.PNG", Base.AddMinutes(5));

        var entry = Assert.Single(RecentFiles.Collect(new[] { _shots }));

        Assert.Equal(path, entry.FullPath);
        Assert.Equal("shot.PNG", entry.Name);
        Assert.Equal("png", entry.Extension);
        Assert.Equal(1, entry.SizeBytes);
        Assert.Equal(new DateTimeOffset(Base.AddMinutes(5), TimeSpan.Zero), entry.ModifiedUtc);
        Assert.Equal(RecentFileKind.Image, entry.Kind);
    }

    [Fact]
    public void Collect_DedupesTheSameFolderListedTwice()
    {
        WriteFile(_downloads, "a.png", Base);
        WriteFile(_downloads, "b.pdf", Base.AddMinutes(1));

        var entries = RecentFiles.Collect(new[] { _downloads, _downloads });

        Assert.Equal(2, entries.Count);
    }

    [Fact]
    public void Collect_CapsAtTheLimit_NewestSurvive()
    {
        for (var i = 0; i < 15; i++)
            WriteFile(_downloads, $"f{i:D2}.png", Base.AddMinutes(i));

        var entries = RecentFiles.Collect(new[] { _downloads });

        Assert.Equal(RecentFiles.MaxEntries, entries.Count);
        Assert.Equal("f14.png", entries[0].Name);
        Assert.Equal("f03.png", entries[^1].Name);
    }

    [Fact]
    public void Collect_RespectsAnExplicitSmallerLimit()
    {
        for (var i = 0; i < 5; i++)
            WriteFile(_downloads, $"f{i}.png", Base.AddMinutes(i));

        Assert.Equal(2, RecentFiles.Collect(new[] { _downloads }, limit: 2).Count);
        Assert.Empty(RecentFiles.Collect(new[] { _downloads }, limit: 0));
    }

    [Fact]
    public void Collect_SkipsMissingFoldersQuietly()
    {
        var missing = Path.Combine(_root, "nope");
        WriteFile(_downloads, "a.png", Base);

        var entries = RecentFiles.Collect(new string?[] { missing, null, "", _downloads });

        Assert.Single(entries);
        Assert.Empty(RecentFiles.Collect(new[] { missing }));
    }

    [Fact]
    public void Collect_OffersOnlyAttachableFiles()
    {
        WriteFile(_downloads, "keep.png", Base);
        WriteFile(_downloads, "keep2.docx", Base.AddMinutes(1));
        WriteFile(_downloads, "skip.exe", Base.AddMinutes(2));
        WriteFile(_downloads, "skip.zip", Base.AddMinutes(3));
        WriteFile(_downloads, "skip.bmp", Base.AddMinutes(4));
        WriteFile(_downloads, "skip.crdownload", Base.AddMinutes(5));

        var names = RecentFiles.Collect(new[] { _downloads }).Select(e => e.Name).ToArray();

        Assert.Equal(new[] { "keep2.docx", "keep.png" }, names);
    }

    [Fact]
    public void Collect_TopLevelOnly_NoRecursionIntoSubfolders()
    {
        var sub = Path.Combine(_downloads, "sub");
        Directory.CreateDirectory(sub);
        WriteFile(sub, "nested.png", Base.AddHours(3));
        WriteFile(_downloads, "top.png", Base);

        var entries = RecentFiles.Collect(new[] { _downloads });

        Assert.Equal("top.png", Assert.Single(entries).Name);
    }

    [Theory]
    [InlineData("photo.png", true)]
    [InlineData("photo.JPG", true)]
    [InlineData("photo.jpeg", true)]
    [InlineData("anim.gif", true)]
    [InlineData("pic.webp", true)]
    [InlineData("doc.pdf", true)]
    [InlineData("notes.txt", true)]
    [InlineData("notes.md", true)]
    [InlineData("report.docx", true)]
    [InlineData(".png", true)]
    [InlineData("png", true)]
    [InlineData("photo.bmp", false)]
    [InlineData("archive.zip", false)]
    [InlineData("program.exe", false)]
    [InlineData("noextension", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsAttachable_ByExtension(string? input, bool expected) =>
        Assert.Equal(expected, RecentFiles.IsAttachable(input));

    [Theory]
    [InlineData("a.png", RecentFileKind.Image)]
    [InlineData("a.bmp", RecentFileKind.Image)]
    [InlineData("a.pdf", RecentFileKind.Document)]
    [InlineData("a.docx", RecentFileKind.Document)]
    [InlineData("a.zip", RecentFileKind.Other)]
    [InlineData("a", RecentFileKind.Other)]
    public void KindOf_ByExtension(string input, RecentFileKind expected) =>
        Assert.Equal(expected, RecentFiles.KindOf(input));

    [Fact]
    public void AttachableExtensionList_MatchesIsAttachable()
    {
        foreach (var ext in RecentFiles.AttachableExtensionList)
            Assert.True(RecentFiles.IsAttachable(ext), ext);
        Assert.Equal(
            RecentFiles.AttachableExtensionList.OrderBy(e => e).ToArray(),
            RecentFiles.AttachableExtensionList.ToArray());
    }

    [Fact]
    public void DefaultSourceFolders_AreDownloadsAndPicturesScreenshots()
    {
        var folders = RecentFiles.DefaultSourceFolders();

        Assert.Equal(AppPaths.DownloadsHint, folders[0]);
        // The Screenshots folder rides along only where the OS
        // resolves a Pictures folder at all (a bare Linux test box
        // may not); where it does, it is Pictures\Screenshots.
        Assert.InRange(folders.Count, 1, 2);
        if (folders.Count == 2)
            Assert.EndsWith("Screenshots", folders[1]);
    }

    [Fact]
    public void AgeParts_Buckets()
    {
        var now = new DateTimeOffset(Base, TimeSpan.Zero);
        Assert.Equal((RecentFileAgeUnit.Now, 0), RecentFiles.AgeParts(now, now));
        Assert.Equal((RecentFileAgeUnit.Now, 0),
            RecentFiles.AgeParts(now.AddSeconds(30), now)); // clock skew reads as "now"
        Assert.Equal((RecentFileAgeUnit.Minutes, 1),
            RecentFiles.AgeParts(now.AddMinutes(-1), now));
        Assert.Equal((RecentFileAgeUnit.Minutes, 59),
            RecentFiles.AgeParts(now.AddMinutes(-59), now));
        Assert.Equal((RecentFileAgeUnit.Hours, 1),
            RecentFiles.AgeParts(now.AddHours(-1), now));
        Assert.Equal((RecentFileAgeUnit.Hours, 23),
            RecentFiles.AgeParts(now.AddHours(-23), now));
        Assert.Equal((RecentFileAgeUnit.Days, 1),
            RecentFiles.AgeParts(now.AddDays(-1), now));
        Assert.Equal((RecentFileAgeUnit.Days, 9),
            RecentFiles.AgeParts(now.AddDays(-9.5), now));
    }
}
