using MuseDesktop.Core;
using Xunit;

namespace MuseDesktop.Core.Tests;

// ───────────────────── Standing briefs: store, validation, compose ─────────────────────

/// <summary>
/// The standing-brief rules (v2.1.0): the store round-trips through
/// real JSON on disk; a damaged file is preserved as .corrupt-* and
/// reads as an empty library; validation is the snippet library's
/// discipline (cleaned name, section cap refused whole, all-empty
/// refused); and Compose lays out exactly the non-empty sections
/// under their labels, capped at MaxComposedLength.
/// </summary>
public class StandingBriefsTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 2, 12, 0, 0, TimeSpan.Zero);

    private static StandingBrief Sample(
        StandingBriefs briefs, string name = "Novel project",
        string background = "Mystery novel, third draft.",
        string rules = "Never rename characters without asking.",
        string preferences = "Plain words, short paragraphs.")
        => briefs.Add(name, background, rules, preferences, Now)
            ?? throw new InvalidOperationException("Sample brief was refused.");

    // ── add / validation ─────────────────────────────────────────

    [Fact]
    public void Add_KeepsTheBrief_WithStampsAndANameSortedPlace()
    {
        var briefs = new StandingBriefs();
        var zebra = Sample(briefs, "Zebra files");
        var alpha = Sample(briefs, "Alpha project");
        Assert.Equal(new[] { alpha.Id, zebra.Id }, briefs.Entries.Select(b => b.Id).ToArray());
        Assert.Equal(Now, alpha.CreatedUtc);
        Assert.Equal(Now, alpha.UpdatedUtc);
        Assert.False(alpha.IsEmpty);
    }

    [Fact]
    public void Add_CleansTheName_AndTrimsSectionsAtTheEdgesOnly()
    {
        var briefs = new StandingBriefs();
        var brief = Sample(briefs, "  Novel \n  project  ", "  line one\n\nline two  ", "", "");
        Assert.Equal("Novel project", brief.Name);
        Assert.Equal("line one\n\nline two", brief.Background);
    }

    [Fact]
    public void Add_RefusesABlankName_AnOverlongSection_AndAnAllEmptyBrief()
    {
        var briefs = new StandingBriefs();
        Assert.Null(briefs.Add("   ", "x", "", "", Now));
        Assert.Null(briefs.Add("Named", new string('a', StandingBriefs.MaxSectionLength + 1), "", "", Now));
        Assert.Null(briefs.Add("Named", " ", "", "\n", Now));
        Assert.Empty(briefs.Briefs);
    }

    [Fact]
    public void Add_AcceptsASectionExactlyAtTheCap()
    {
        var briefs = new StandingBriefs();
        var brief = briefs.Add("Named", new string('a', StandingBriefs.MaxSectionLength), "", "", Now);
        Assert.NotNull(brief);
        Assert.Equal(StandingBriefs.MaxSectionLength, brief.Background.Length);
    }

    [Fact]
    public void Add_StopsAtTheLibraryCap()
    {
        var briefs = new StandingBriefs();
        for (var i = 0; i < StandingBriefs.MaxBriefs; i++)
            Assert.NotNull(briefs.Add($"Brief {i:D2}", "body", "", "", Now));
        Assert.Null(briefs.Add("One too many", "body", "", "", Now));
        Assert.Equal(StandingBriefs.MaxBriefs, briefs.Briefs.Count);
    }

    // ── update / remove / duplicate ──────────────────────────────

    [Fact]
    public void Update_ReplacesTheWords_KeepsTheCreationStamp()
    {
        var briefs = new StandingBriefs();
        var brief = Sample(briefs);
        var later = Now.AddDays(3);
        Assert.True(briefs.Update(brief.Id, "Novel project", "New background.", "", "", later));
        var after = briefs.Find(brief.Id)!;
        Assert.Equal("New background.", after.Background);
        Assert.Equal("", after.Rules);
        Assert.Equal(Now, after.CreatedUtc);
        Assert.Equal(later, after.UpdatedUtc);
    }

    [Fact]
    public void Update_RefusesToEmptyEverySection_AndToInventABrief()
    {
        var briefs = new StandingBriefs();
        var brief = Sample(briefs);
        Assert.False(briefs.Update(brief.Id, "Novel project", "", " ", "", Now));
        Assert.False(briefs.Update("no-such-id", "Name", "body", "", "", Now));
        Assert.Equal("Mystery novel, third draft.", briefs.Find(brief.Id)!.Background);
    }

    [Fact]
    public void Remove_TakesTheBriefOut()
    {
        var briefs = new StandingBriefs();
        var brief = Sample(briefs);
        Assert.True(briefs.Remove(brief.Id));
        Assert.False(briefs.Remove(brief.Id));
        Assert.Empty(briefs.Briefs);
    }

    [Fact]
    public void Duplicate_CopiesTheSections_UnderASequencedName()
    {
        var briefs = new StandingBriefs();
        var brief = Sample(briefs);
        var first = briefs.Duplicate(brief.Id, "copy", Now)!;
        var second = briefs.Duplicate(brief.Id, "copy", Now)!;
        Assert.Equal("Novel project copy", first.Name);
        Assert.Equal("Novel project copy 2", second.Name);
        Assert.Equal(brief.Background, first.Background);
        Assert.Equal(brief.Rules, first.Rules);
        Assert.Equal(brief.Preferences, first.Preferences);
        Assert.NotEqual(brief.Id, first.Id);
    }

    [Fact]
    public void Duplicate_OfAMissingBrief_IsNull()
    {
        var briefs = new StandingBriefs();
        Assert.Null(briefs.Duplicate("no-such-id", "copy", Now));
    }

    // ── store round-trip + corrupt handling ─────────────────────

    [Fact]
    public void Store_RoundTripsThroughARealFile()
    {
        var dir = Directory.CreateDirectory(
            Path.Combine(Path.GetTempPath(), "md-briefs-" + Guid.NewGuid().ToString("N")));
        try
        {
            var path = Path.Combine(dir.FullName, "briefs.json");
            var briefs = new StandingBriefs();
            var brief = Sample(briefs);
            StandingBriefStore.Save(path, briefs);

            var loaded = StandingBriefStore.Load(path);
            var back = Assert.Single(loaded.Briefs);
            Assert.Equal(brief.Id, back.Id);
            Assert.Equal(brief.Name, back.Name);
            Assert.Equal(brief.Background, back.Background);
            Assert.Equal(brief.Rules, back.Rules);
            Assert.Equal(brief.Preferences, back.Preferences);
            Assert.Equal(brief.CreatedUtc, back.CreatedUtc);
            Assert.Equal(brief.UpdatedUtc, back.UpdatedUtc);
        }
        finally
        {
            Directory.Delete(dir.FullName, recursive: true);
        }
    }

    [Fact]
    public void Store_MissingFile_IsAnEmptyLibrary()
    {
        var path = Path.Combine(Path.GetTempPath(), "md-briefs-none-" + Guid.NewGuid().ToString("N"), "briefs.json");
        Assert.Empty(StandingBriefStore.Load(path).Briefs);
    }

    [Fact]
    public void Store_CorruptFile_IsPreservedAndReadsEmpty()
    {
        var dir = Directory.CreateDirectory(
            Path.Combine(Path.GetTempPath(), "md-briefs-" + Guid.NewGuid().ToString("N")));
        try
        {
            var path = Path.Combine(dir.FullName, "briefs.json");
            File.WriteAllText(path, "{ this is not a brief library");
            var loaded = StandingBriefStore.Load(path);
            Assert.Empty(loaded.Briefs);
            var preserved = Directory.GetFiles(dir.FullName, "briefs.json.corrupt-*");
            Assert.Single(preserved);
            Assert.Equal("{ this is not a brief library", File.ReadAllText(preserved[0]));
        }
        finally
        {
            Directory.Delete(dir.FullName, recursive: true);
        }
    }

    [Fact]
    public void Load_SanitizesBriefsThatFailValidation()
    {
        var dir = Directory.CreateDirectory(
            Path.Combine(Path.GetTempPath(), "md-briefs-" + Guid.NewGuid().ToString("N")));
        try
        {
            var path = Path.Combine(dir.FullName, "briefs.json");
            File.WriteAllText(path, """
                { "briefs": [
                  { "id": "good", "name": "Good", "background": "kept", "rules": "", "preferences": "" },
                  { "id": "empty", "name": "Empty", "background": " ", "rules": "", "preferences": "" },
                  { "id": "overlong", "name": "Overlong", "background": "BACKGROUND", "rules": "", "preferences": "" },
                  { "id": "", "name": "No id", "background": "kept", "rules": "", "preferences": "" }
                ] }
                """.Replace("BACKGROUND", new string('a', StandingBriefs.MaxSectionLength + 1)));
            var loaded = StandingBriefStore.Load(path);
            var back = Assert.Single(loaded.Briefs);
            Assert.Equal("good", back.Id);
        }
        finally
        {
            Directory.Delete(dir.FullName, recursive: true);
        }
    }

    // ── compose ──────────────────────────────────────────────────

    private const string Header = "Standing brief — “{0}”. Use it as the background for our conversation:";

    [Fact]
    public void Compose_LaysOutEverySectionUnderItsLabel()
    {
        var briefs = new StandingBriefs();
        var brief = Sample(briefs);
        var text = StandingBriefs.Compose(brief, Header, "Background", "Rules", "Preferences");
        Assert.Equal(
            "Standing brief — “Novel project”. Use it as the background for our conversation:"
            + "\n\n## Background\n\nMystery novel, third draft."
            + "\n\n## Rules\n\nNever rename characters without asking."
            + "\n\n## Preferences\n\nPlain words, short paragraphs.",
            text);
    }

    [Fact]
    public void Compose_SkipsEmptySectionsEntirely()
    {
        var briefs = new StandingBriefs();
        var brief = Sample(briefs, background: "", preferences: "");
        var text = StandingBriefs.Compose(brief, Header, "Background", "Rules", "Preferences");
        Assert.Equal(
            "Standing brief — “Novel project”. Use it as the background for our conversation:"
            + "\n\n## Rules\n\nNever rename characters without asking.",
            text);
        Assert.DoesNotContain("Background", text);
        Assert.DoesNotContain("Preferences", text);
    }

    [Fact]
    public void Compose_AllEmpty_IsNull_SoIsAMissingBrief()
    {
        var empty = new StandingBrief { Id = "x", Name = "Blank" };
        Assert.Null(StandingBriefs.Compose(empty, Header, "Background", "Rules", "Preferences"));
        Assert.Null(StandingBriefs.Compose(null, Header, "Background", "Rules", "Preferences"));
    }

    [Fact]
    public void Compose_CapsTheWholeMessage_WhenThreeFullSectionsOverflow()
    {
        var briefs = new StandingBriefs();
        var full = new string('a', StandingBriefs.MaxSectionLength);
        var brief = Sample(briefs, background: full, rules: full, preferences: full);
        var text = StandingBriefs.Compose(brief, Header, "Background", "Rules", "Preferences")!;
        Assert.Equal(StandingBriefs.MaxComposedLength, text.Length);
        Assert.EndsWith("…", text);
    }
}
