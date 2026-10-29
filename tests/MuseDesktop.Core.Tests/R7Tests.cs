using System.Text.RegularExpressions;
using System.Xml.Linq;
using MuseDesktop.Core;
using Xunit;

namespace MuseDesktop.Core.Tests;

// ───────────────────── R7 #161/#162: the localization core ─────────────────────

/// <summary>
/// The localization framework (R7 #161): key → per-language tables,
/// the fallback chain current → en-US → the key itself, language
/// resolution for the picker (#162: system default, exact match,
/// primary-subtag match), and the placeholder shape every
/// translation must preserve (#164/#165).
/// </summary>
public class LocalizationCoreTests
{
    private static Dictionary<string, IReadOnlyDictionary<string, string>> Tables(
        params (string Code, Dictionary<string, string> Strings)[] languages) =>
        languages.ToDictionary(l => l.Code, l => (IReadOnlyDictionary<string, string>)l.Strings);

    [Fact]
    public void Localizer_FallsBackCurrentThenEnglishThenKey()
    {
        var tables = Tables(
            ("en-US", new() { ["hello"] = "Hello", ["bye"] = "Goodbye" }),
            ("es-ES", new() { ["hello"] = "Hola" }));
        var loc = new Localizer(tables, "es-ES");
        Assert.Equal("Hola", loc.Get("hello"));       // translated
        Assert.Equal("Goodbye", loc.Get("bye"));       // falls back to English
        Assert.Equal("nope", loc.Get("nope"));         // unknown key shows itself, never crashes
        Assert.Equal("", loc.Get(""));                 // empty key is empty text
        Assert.Equal(new[] { "bye" }, loc.MissingKeys); // the fallback is counted (#164)
    }

    [Fact]
    public void Localizer_EmptyTranslationCountsAsMissing()
    {
        var tables = Tables(
            ("en-US", new() { ["a"] = "A" }),
            ("es-ES", new() { ["a"] = "" }));
        var loc = new Localizer(tables, "es-ES");
        Assert.Equal("A", loc.Get("a"));
        Assert.Equal(new[] { "a" }, loc.MissingKeys);
    }

    [Fact]
    public void Localizer_FormatsInTheLanguagesOwnCulture()
    {
        var tables = Tables(
            ("en-US", new() { ["n"] = "{0:N1}" }),
            ("es-ES", new() { ["n"] = "{0:N1}" }));
        Assert.Equal("1,234.5", new Localizer(tables, "en-US").Format("n", 1234.5));
        Assert.Equal("1.234,5", new Localizer(tables, "es-ES").Format("n", 1234.5));
    }

    [Theory]
    [InlineData("es-ES", "es-ES")]     // exact
    [InlineData("es-MX", "es-ES")]     // primary-subtag match
    [InlineData("ES-es", "es-ES")]     // case-insensitive
    [InlineData("fr-FR", "en-US")]     // unknown language → the source
    [InlineData(null, "en-US")]        // no preference → the source of what's available
    [InlineData("", "en-US")]
    public void Resolve_PicksExactThenPrimaryThenSource(string? preference, string expected) =>
        Assert.Equal(expected, LanguageCatalog.Resolve(preference, new[] { "en-US", "es-ES" }));

    [Fact]
    public void CreateLocalizer_SystemLanguageIsTheDefault()
    {
        // #162: no stored preference → the Windows language decides,
        // resolved the same way (es-MX lands on the shipped es-ES).
        Assert.Equal("es-ES", StringCatalog.CreateLocalizer(null, "es-MX").Language);
        Assert.Equal("es-ES", StringCatalog.CreateLocalizer("es-ES", "en-US").Language);
        Assert.Equal("en-US", StringCatalog.CreateLocalizer("de-DE", "en-US").Language);
        Assert.Equal("en-US", StringCatalog.CreateLocalizer(null, null).Language);
    }

    [Theory]
    [InlineData("es", true)]
    [InlineData("es-ES", true)]
    [InlineData("pt-BR", true)]
    [InlineData("zh-Hans", true)]
    [InlineData("xx-XX", true)]        // the export template's placeholder code
    [InlineData("e", false)]
    [InlineData("english", false)]
    [InlineData("es ES", false)]
    [InlineData("es-", false)]
    [InlineData("not a code!!", false)]
    [InlineData(null, false)]
    [InlineData("", false)]
    public void PlausibleCode_Validation(string? code, bool expected) =>
        Assert.Equal(expected, LanguageCatalog.IsPlausibleCode(code));

    [Theory]
    [InlineData("ar-EG", true)]
    [InlineData("he-IL", true)]
    [InlineData("fa", true)]
    [InlineData("ur-PK", true)]
    [InlineData("es-ES", false)]
    [InlineData("en-US", false)]
    [InlineData(null, false)]
    [InlineData("", false)]
    public void RightToLeft_ComesFromTheLanguageItself(string? code, bool expected) =>
        Assert.Equal(expected, LanguageCatalog.IsRightToLeft(code));

    [Fact]
    public void Describe_KnownAndUnknownLanguages()
    {
        var spanish = LanguageCatalog.Describe("es-ES");
        Assert.Equal("Spanish (Spain)", spanish.EnglishName);
        Assert.Equal("Español (España)", spanish.NativeName);
        Assert.False(spanish.IsRightToLeft);
        var arabic = LanguageCatalog.Describe("ar-EG");   // not built in, still described
        Assert.Equal("ar-EG", arabic.EnglishName);
        Assert.True(arabic.IsRightToLeft);
        Assert.Equal(2, LanguageCatalog.BuiltIn.Count);   // en-US + es-ES ship in v1.8.0
    }

    [Fact]
    public void PlaceholderShape_TokensMustMatchExactly()
    {
        Assert.Equal(new[] { "{0}", "{1}" }, PlaceholderShape.Of("Download {0} of {1} files"));
        Assert.Equal(new[] { "{name}" }, PlaceholderShape.Of("Hi {name}"));
        Assert.True(PlaceholderShape.Matches("{0} files", "{0} archivos"));
        Assert.False(PlaceholderShape.Matches("{0} files", "archivos"));
        Assert.False(PlaceholderShape.Matches("{0} files", "{1} archivos"));
        // A format specifier is part of the translatable text (dates
        // localize their ORDER: "MMM d" → "d MMM"), so only the bare
        // token shape is guarded — the argument itself can't be lost.
        Assert.Empty(PlaceholderShape.Of("{0:HH:mm}"));
        Assert.True(PlaceholderShape.Matches("at {0:HH:mm}", "a las {0:HH:mm}"));
    }
}

// ───────────────────── R7 #163/#164: Spanish + the coverage guard ─────────────────────

/// <summary>
/// The Spanish shell language (R7 #163) held to the same completeness
/// standard as English, and the coverage guard (#164) that counts
/// every fallback and renders the per-language Diagnostics lines.
/// </summary>
public class SpanishCatalogTests
{
    [Fact]
    public void Spanish_CoversEveryString_100Percent()
    {
        var report = LocalizationCoverage.Report(StringCatalog.Tables);
        var spanish = report.Single(c => c.Language == LanguageCatalog.SpanishCode);
        Assert.Equal(StringCatalog.Source.Count, spanish.TotalKeys);
        Assert.Equal(spanish.TotalKeys, spanish.TranslatedKeys);
        Assert.Empty(spanish.MissingKeys);
        Assert.Equal(100.0, spanish.Percent);
        // The source defines the total and reports last, also full.
        var source = report[^1];
        Assert.Equal(LanguageCatalog.SourceCode, source.Language);
        Assert.Equal(100.0, source.Percent);
    }

    [Fact]
    public void Spanish_EveryPlaceholderShapeMatchesEnglish()
    {
        var spanish = StringCatalog.Tables[LanguageCatalog.SpanishCode];
        var broken = StringCatalog.Source.Keys
            .Where(k => !PlaceholderShape.Matches(StringCatalog.Source[k], spanish[k]))
            .ToList();
        Assert.True(broken.Count == 0,
            "Spanish strings whose placeholders differ from English: " + string.Join(", ", broken));
    }

    [Fact]
    public void Spanish_NoEmptyOrUntranslatedSentinelValues()
    {
        var spanish = StringCatalog.Tables[LanguageCatalog.SpanishCode];
        Assert.All(spanish, pair =>
        {
            Assert.False(string.IsNullOrWhiteSpace(pair.Value), $"es-ES value for {pair.Key} is blank");
        });
        Assert.All(StringCatalog.Source, pair =>
        {
            Assert.False(string.IsNullOrWhiteSpace(pair.Value), $"en-US value for {pair.Key} is blank");
        });
    }

    [Fact]
    public void Coverage_ReportLines_AreTheDiagnosticsLines()
    {
        var lines = LocalizationCoverage.ReportLines(StringCatalog.Tables);
        var total = StringCatalog.Source.Count;
        Assert.Contains($"es-ES (Spanish (Spain)): {total}/{total} strings (100%)", lines);
        Assert.Contains($"en-US (English (United States)): {total}/{total} strings (100%)", lines);
    }

    [Fact]
    public void Coverage_PartialLanguage_CountsTheFallback()
    {
        var tables = new Dictionary<string, IReadOnlyDictionary<string, string>>
        {
            ["en-US"] = new Dictionary<string, string> { ["a"] = "A", ["b"] = "B", ["c"] = "C" },
            ["de-DE"] = new Dictionary<string, string> { ["a"] = "Ein A" },
        };
        var report = LocalizationCoverage.Report(tables);
        var german = report.Single(c => c.Language == "de-DE");
        Assert.Equal(3, german.TotalKeys);
        Assert.Equal(1, german.TranslatedKeys);
        Assert.Equal(new[] { "b", "c" }, german.MissingKeys);
        var line = LocalizationCoverage.ReportLines(tables).Single(l => l.StartsWith("de-DE"));
        Assert.Equal("de-DE (de-DE): 1/3 strings (33.3%) — 2 fall back to English", line);
    }

    [Fact]
    public void Diagnostics_RendersTheLanguageAndAnnouncementSections()
    {
        var data = new DiagnosticsData
        {
            AppVersion = "1.8.0",
            SettingsPath = "settings.json",
            LogPath = "musedesktop.log",
            ProfileDir = "profiles",
            LanguageCoverageLines = LocalizationCoverage.ReportLines(StringCatalog.Tables),
            ActiveLanguageLine = "es-ES (Español (España))",
            RecentAnnouncements = new[] { "2026-10-27 09:15 [Downloads] Download complete: f.zip" },
        };
        var text = DiagnosticsReport.Compose(data);
        Assert.Contains("Language", text);
        Assert.Contains("  Active: es-ES (Español (España))", text);
        Assert.Contains("es-ES (Spanish (Spain)):", text);
        Assert.Contains("Recent announcements (newest first)", text);
        Assert.Contains("[Downloads] Download complete: f.zip", text);
    }
}

// ───────────────────── R7 #165: language packs as files ─────────────────────

/// <summary>
/// Language packs (R7 #165): export a template, import a file, and
/// the validation that runs before a pack is ever used — every key
/// present, no unknown keys, placeholders intact, plausible code.
/// </summary>
public class LanguagePackTests
{
    private static Dictionary<string, string> CompleteStrings() =>
        StringCatalog.Source.ToDictionary(p => p.Key, p => p.Value);

    private static string PackJson(string language, Dictionary<string, string> strings, string? name = "Test")
    {
        var pack = new LanguagePack(language, name, strings);
        return LanguagePackFile.Serialize(pack);
    }

    [Fact]
    public void TryLoad_ValidPack()
    {
        var json = PackJson("fr-FR", CompleteStrings(), "Français");
        Assert.True(LanguagePackFile.TryLoad(json, StringCatalog.Source, out var pack, out var error));
        Assert.Null(error);
        Assert.NotNull(pack);
        Assert.Equal("fr-FR", pack!.Language);
        Assert.Equal("Français", pack.DisplayName);
        Assert.Equal(StringCatalog.Source.Count, pack.Strings.Count);
    }

    [Fact]
    public void TryLoad_MissingKey_IsRefused()
    {
        var strings = CompleteStrings();
        strings.Remove("zoom");
        Assert.False(LanguagePackFile.TryLoad(
            PackJson("fr-FR", strings), StringCatalog.Source, out _, out var error));
        Assert.Contains("missing", error);
        Assert.Contains("zoom", error);
    }

    [Fact]
    public void TryLoad_UnknownKey_IsRefused()
    {
        var strings = CompleteStrings();
        strings["no.such.key"] = "Bogus";
        Assert.False(LanguagePackFile.TryLoad(
            PackJson("fr-FR", strings), StringCatalog.Source, out _, out var error));
        Assert.Contains("doesn't have", error);
    }

    [Fact]
    public void TryLoad_BrokenPlaceholder_IsRefused()
    {
        var strings = CompleteStrings();
        var key = StringCatalog.Source.Keys.First(k => PlaceholderShape.Of(StringCatalog.Source[k]).Count > 0);
        strings[key] = "No placeholders here anymore";
        Assert.False(LanguagePackFile.TryLoad(
            PackJson("fr-FR", strings), StringCatalog.Source, out _, out var error));
        Assert.Contains("placeholders", error);
        Assert.Contains(key, error);
    }

    [Theory]
    [InlineData("")]                       // empty file
    [InlineData("{not json")]              // not JSON
    [InlineData("{\"strings\":{}}")]       // no language section
    public void TryLoad_NotAPack_IsRefused(string json)
    {
        Assert.False(LanguagePackFile.TryLoad(json, StringCatalog.Source, out _, out var error));
        Assert.NotNull(error);
    }

    [Fact]
    public void TryLoad_ImplausibleCode_IsRefused()
    {
        Assert.False(LanguagePackFile.TryLoad(
            PackJson("not a code!!", CompleteStrings()), StringCatalog.Source, out _, out var error));
        Assert.Contains("isn't a language code", error);
    }

    [Fact]
    public void ExportTemplate_RoundTripsThroughValidation()
    {
        // The template a translator starts from must itself pass the
        // validator once filled in — its strings are the full source.
        var template = LanguagePackFile.ExportTemplate(StringCatalog.Source);
        Assert.True(LanguagePackFile.TryLoad(template, StringCatalog.Source, out var pack, out var error));
        Assert.Equal("xx-XX", pack!.Language);
        Assert.Equal(StringCatalog.Source.Count, pack.Strings.Count);
        Assert.All(StringCatalog.Source, pair => Assert.Equal(pair.Value, pack.Strings[pair.Key]));
        Assert.Equal("pack-fr-FR.json", LanguagePackFile.FileNameFor("fr-FR"));
    }

    [Fact]
    public void LoadFolder_LoadsValidAndNamesTheSkipped()
    {
        var dir = Path.Combine(Path.GetTempPath(), "md-r7-packs-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "pack-fr-FR.json"), PackJson("fr-FR", CompleteStrings()));
            File.WriteAllText(Path.Combine(dir, "pack-broken.json"), "{not json");
            File.WriteAllText(Path.Combine(dir, "notes.txt"), "not a pack — ignored by the file pattern");
            var packs = LanguagePackFile.LoadFolder(dir, StringCatalog.Source, out var skipped);
            Assert.Single(packs);
            Assert.True(packs.ContainsKey("fr-FR"));
            Assert.Equal(new[] { "pack-broken.json" }, skipped);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void LoadFolder_MissingFolder_IsEmptyNotAnError()
    {
        var packs = LanguagePackFile.LoadFolder(
            Path.Combine(Path.GetTempPath(), "md-r7-no-such-" + Guid.NewGuid().ToString("N")),
            StringCatalog.Source, out var skipped);
        Assert.Empty(packs);
        Assert.Empty(skipped);
    }

    [Fact]
    public void Pack_ForABuiltInLanguage_ReplacesIt()
    {
        // #165: the user's own translation of their own language wins.
        var strings = CompleteStrings();
        strings["zoom"] = "ZOOM!";
        var packs = new Dictionary<string, LanguagePack> { ["en-US"] = new("en-US", null, strings) };
        var loc = StringCatalog.CreateLocalizer("en-US", "en-US", packs);
        Assert.Equal("ZOOM!", loc.Get("zoom"));
    }
}

// ───────────────────── R7 #166: right-to-left readiness ─────────────────────

/// <summary>
/// RTL readiness (R7 #166): no RTL language ships in v1.8.0, but the
/// machinery is proven with a synthetic Arabic pack — direction comes
/// from the language, untranslated strings fall back to English, and
/// coverage counts it like any other language. Adding a real RTL
/// translation later is translation only.
/// </summary>
public class RtlReadinessTests
{
    [Fact]
    public void ArabicPack_DirectionFallbackAndCoverage()
    {
        var partial = new Dictionary<string, string>
        {
            ["settings"] = "الإعدادات…",
            ["downloads"] = "التنزيلات",
        };
        var packs = new Dictionary<string, LanguagePack>
        {
            ["ar-EG"] = new("ar-EG", "العربية (مصر)", partial),
        };
        var tables = StringCatalog.Merge(packs);
        var loc = new Localizer(tables, "ar-EG");

        Assert.True(loc.IsRightToLeft);                       // direction from the language
        Assert.Equal("الإعدادات…", loc.Get("settings"));       // translated strings speak Arabic
        Assert.Equal(StringCatalog.Source["zoom"], loc.Get("zoom")); // the rest fall back to English
        Assert.Equal(StringCatalog.Source.Count - 2, loc.MissingKeys.Count);

        var coverage = LocalizationCoverage.Report(tables).Single(c => c.Language == "ar-EG");
        Assert.Equal(2, coverage.TranslatedKeys);
        Assert.Equal(StringCatalog.Source.Count, coverage.TotalKeys);
    }

    [Fact]
    public void ShellFlowDirection_HasAnRtlLanguageToFollow()
    {
        // The shell sets each window's FlowDirection from
        // Localizer.IsRightToLeft (App.xaml.cs class handler); this
        // pins that the signal exists and is right for both families.
        Assert.False(StringCatalog.CreateLocalizer("es-ES", null).IsRightToLeft);
        Assert.True(LanguageCatalog.Describe("ar-EG").IsRightToLeft);
        Assert.True(LanguageCatalog.Describe("he-IL").IsRightToLeft);
    }
}

// ───────────────────── R7 #170: shell text spacing ─────────────────────

/// <summary>
/// Shell text spacing (R7 #170): the line-spacing scale math. WPF
/// has no letter-spacing API, so the control is line spacing only —
/// these tests pin the honest shape: Standard leaves the font's own
/// line height unset, Relaxed/Wide multiply it by the base factor.
/// </summary>
public class ShellTextSpacingTests
{
    [Theory]
    [InlineData(1.0, 1.0)]
    [InlineData(0.5, 1.0)]     // below the range clamps to Standard
    [InlineData(1.1, 1.0)]     // nearest offered scale
    [InlineData(1.13, 1.25)]
    [InlineData(1.25, 1.25)]
    [InlineData(1.4, 1.5)]
    [InlineData(2.0, 1.5)]     // above the range clamps to Wide
    [InlineData(double.NaN, 1.0)]
    [InlineData(double.PositiveInfinity, 1.0)]
    public void Sanitize_ClampsToTheNearestOfferedScale(double input, double expected) =>
        Assert.Equal(expected, ShellTextSpacing.Sanitize(input));

    [Fact]
    public void LineHeight_StandardLeavesTheFontAlone()
    {
        Assert.Null(ShellTextSpacing.LineHeight(14, 1.0));
        Assert.Null(ShellTextSpacing.LineHeight(0, 1.5));
        Assert.Equal(14 * 1.25 * 1.25, ShellTextSpacing.LineHeight(14, 1.25));
        Assert.Equal(14 * 1.25 * 1.5, ShellTextSpacing.LineHeight(14, 1.5));
    }

    [Fact]
    public void LabelKeys_NameTheThreeChoices()
    {
        Assert.Equal("settings.text_spacing.standard", ShellTextSpacing.LabelKey(1.0));
        Assert.Equal("settings.text_spacing.relaxed", ShellTextSpacing.LabelKey(1.25));
        Assert.Equal("settings.text_spacing.wide", ShellTextSpacing.LabelKey(1.5));
        Assert.Equal("settings.text_spacing.standard", ShellTextSpacing.LabelKey(0.5));
        Assert.Equal("settings.text_spacing.wide", ShellTextSpacing.LabelKey(99));
        // …and the Settings combo's labels all exist in the catalog.
        foreach (var scale in ShellTextSpacing.Options)
            Assert.False(string.IsNullOrWhiteSpace(StringCatalog.Source[ShellTextSpacing.LabelKey(scale)]));
    }
}

// ───────────────────── R7 #169: the announcement center ─────────────────────

/// <summary>
/// The announcement center (R7 #169): per-type switches (default on,
/// the map stores only the exceptions) and the quiet visual history
/// (bounded, local-only, honest about what was NOT spoken).
/// </summary>
public class AnnouncementCenterTests
{
    [Fact]
    public void Switches_DefaultOn_AndStoreOnlyExceptions()
    {
        Assert.All(AnnouncementKinds.All, kind =>
            Assert.True(AnnouncementSwitches.IsEnabled(null, kind)));
        Assert.True(AnnouncementSwitches.IsEnabled(new Dictionary<string, bool>(), AnnouncementKind.Downloads));

        var off = AnnouncementSwitches.With(null, AnnouncementKind.Timers, false);
        Assert.False(AnnouncementSwitches.IsEnabled(off, AnnouncementKind.Timers));
        Assert.True(AnnouncementSwitches.IsEnabled(off, AnnouncementKind.Downloads));
        Assert.Equal(new Dictionary<string, bool> { ["timers"] = false }, off);

        var backOn = AnnouncementSwitches.With(off, AnnouncementKind.Timers, true);
        Assert.Empty(backOn);   // on is the default — storing it is noise
        Assert.True(AnnouncementSwitches.IsEnabled(backOn, AnnouncementKind.Timers));
    }

    [Fact]
    public void Switches_SanitizeDropsUnknownOnAndGeneralEntries()
    {
        var messy = new Dictionary<string, bool>
        {
            ["downloads"] = false,   // kept: a real exception
            ["updates"] = true,      // dropped: on is the default
            ["general"] = false,     // dropped: General has no switch
            ["bogus"] = false,       // dropped: unknown kind
        };
        Assert.Equal(new Dictionary<string, bool> { ["downloads"] = false },
            AnnouncementSwitches.Sanitize(messy));
        Assert.Empty(AnnouncementSwitches.Sanitize(null));
    }

    [Fact]
    public void Kinds_IdsLabelsAndTheSwitchableSet()
    {
        Assert.Equal(
            new[] { AnnouncementKind.Downloads, AnnouncementKind.Updates, AnnouncementKind.Timers, AnnouncementKind.Focus },
            AnnouncementKinds.All);
        Assert.DoesNotContain(AnnouncementKind.General, AnnouncementKinds.All);
        foreach (var kind in AnnouncementKinds.All)
        {
            Assert.Equal(kind, AnnouncementKinds.FromId(AnnouncementKinds.Id(kind)));
            // Every switch's Settings label is a real catalog string.
            Assert.False(string.IsNullOrWhiteSpace(StringCatalog.Source[AnnouncementKinds.LabelKey(kind)]));
        }
        Assert.Equal(AnnouncementKind.General, AnnouncementKinds.FromId("general"));
        Assert.Null(AnnouncementKinds.FromId("bogus"));
        Assert.Null(AnnouncementKinds.FromId(null));
    }

    [Fact]
    public void History_IsBoundedAndNewestFirst()
    {
        var history = new AnnouncementHistory();
        var start = new DateTimeOffset(2026, 10, 27, 12, 0, 0, TimeSpan.Zero);
        for (var i = 1; i <= AnnouncementHistory.MaxEntries + 5; i++)
            history.Record(AnnouncementKind.Downloads, $"#{i}", spoken: true, start.AddMinutes(i));
        Assert.Equal(AnnouncementHistory.MaxEntries, history.Entries.Count);
        Assert.Equal("#6", history.Entries[0].Text);   // the oldest five fell off
        var recent = history.Recent(3);
        Assert.Equal(new[] { "#105", "#104", "#103" }, recent.Select(e => e.Text).ToArray());
        Assert.Empty(history.Recent(0));
    }

    [Fact]
    public void History_DescribeLine_SaysWhenATypeWasOff()
    {
        var when = new DateTimeOffset(2026, 10, 27, 12, 0, 0, TimeSpan.Zero);
        var spoken = new AnnouncementEntry(when, "downloads", "Download complete: f.zip", Spoken: true);
        var line = AnnouncementHistory.DescribeLine(spoken);
        Assert.Contains("[Downloads]", line);
        Assert.Contains("Download complete: f.zip", line);
        Assert.DoesNotContain("not spoken", line);

        var muted = spoken with { Spoken = false };
        Assert.EndsWith("(not spoken — type turned off)", AnnouncementHistory.DescribeLine(muted));
    }

    [Fact]
    public void HistoryStore_RoundTripsAndFiltersUnknownKinds()
    {
        var dir = Path.Combine(Path.GetTempPath(), "md-r7-ann-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var path = Path.Combine(dir, "announcements.json");
            var when = new DateTimeOffset(2026, 10, 27, 12, 0, 0, TimeSpan.Zero);
            var history = new AnnouncementHistory()
                .Record(AnnouncementKind.Updates, "MuseDesktop v1.8.0 is available", spoken: true, when)
                .Record(AnnouncementKind.Focus, "Focus mode on", spoken: false, when.AddMinutes(1));
            history.Entries.Add(new AnnouncementEntry(when, "bogus", "not a real kind", true));
            AnnouncementHistoryStore.Save(path, history);

            var loaded = AnnouncementHistoryStore.Load(path);
            Assert.Equal(2, loaded.Entries.Count);
            Assert.Equal("updates", loaded.Entries[0].KindId);
            Assert.Equal("MuseDesktop v1.8.0 is available", loaded.Entries[0].Text);
            Assert.True(loaded.Entries[0].Spoken);
            Assert.False(loaded.Entries[1].Spoken);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void HistoryStore_CorruptFile_IsPreservedAndStartsEmpty()
    {
        var dir = Path.Combine(Path.GetTempPath(), "md-r7-ann-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var path = Path.Combine(dir, "announcements.json");
            File.WriteAllText(path, "{ this is not a history");
            var loaded = AnnouncementHistoryStore.Load(path);
            Assert.Empty(loaded.Entries);
            Assert.NotEmpty(Directory.GetFiles(dir, "announcements.json.corrupt-*"));
            // A missing file is just an empty history, no drama.
            Assert.Empty(AnnouncementHistoryStore.Load(Path.Combine(dir, "nope.json")).Entries);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}

// ───────────────────── R7: the new settings fields ─────────────────────

/// <summary>
/// The R7 settings fields (language preference, announcement
/// switches, text spacing): defaults, Clone round-trip, sanitize
/// repairs, and the reset contract — language packs are FILES and
/// are deliberately not removed by a settings reset.
/// </summary>
public class R7SettingsTests
{
    [Fact]
    public void Defaults_FollowTheSystemAndLeaveEverythingOn()
    {
        var fresh = new AppSettings();
        Assert.Null(fresh.UiLanguage);
        Assert.Empty(fresh.AnnouncementKindEnabled);
        Assert.Equal(1.0, fresh.ShellTextSpacingScale);
    }

    [Fact]
    public void Fields_RoundTripThroughClone()
    {
        var settings = new AppSettings
        {
            UiLanguage = "es-ES",
            AnnouncementKindEnabled = new Dictionary<string, bool> { ["timers"] = false },
            ShellTextSpacingScale = 1.5,
        };
        var clone = settings.Clone();
        Assert.Equal("es-ES", clone.UiLanguage);
        Assert.Equal(new Dictionary<string, bool> { ["timers"] = false }, clone.AnnouncementKindEnabled);
        Assert.Equal(1.5, clone.ShellTextSpacingScale);
    }

    [Fact]
    public void Sanitized_RepairsTheR7Fields()
    {
        var broken = new AppSettings
        {
            UiLanguage = "not a code!!",
            AnnouncementKindEnabled = new Dictionary<string, bool>
            {
                ["general"] = false, ["bogus"] = false, ["updates"] = true, ["timers"] = false,
            },
            ShellTextSpacingScale = double.NaN,
        }.Sanitized();
        Assert.Null(broken.UiLanguage);
        Assert.Equal(new Dictionary<string, bool> { ["timers"] = false }, broken.AnnouncementKindEnabled);
        Assert.Equal(1.0, broken.ShellTextSpacingScale);

        var kept = new AppSettings { UiLanguage = "es-ES", ShellTextSpacingScale = 1.4 }.Sanitized();
        Assert.Equal("es-ES", kept.UiLanguage);
        Assert.Equal(1.5, kept.ShellTextSpacingScale);
    }

    [Fact]
    public void SettingsReset_RestoresTheR7Defaults()
    {
        var settings = new AppSettings
        {
            UiLanguage = "es-ES",
            AnnouncementKindEnabled = new Dictionary<string, bool> { ["downloads"] = false },
            ShellTextSpacingScale = 1.5,
        };
        SettingsReset.ApplyDefaults(settings);
        var fresh = new AppSettings();
        Assert.Equal(fresh.UiLanguage, settings.UiLanguage);
        Assert.Equal(fresh.AnnouncementKindEnabled, settings.AnnouncementKindEnabled);
        Assert.Equal(fresh.ShellTextSpacingScale, settings.ShellTextSpacingScale);
    }
}

// ───────────────────── R7 #161: catalog ↔ Core composer pins ─────────────────────

/// <summary>
/// The catalog's English templates pinned against the Core composers
/// whose wording the shell used to print directly (R7 #161). The
/// shell now renders the localized twins (TimerLines, SelfTestLines,
/// DownloadLines, the tray/announcer compositions) from these
/// templates — if a template and its Core original ever drift apart,
/// one of these tests fails instead of a user noticing.
/// </summary>
public class CatalogPinTests
{
    private static readonly Localizer En = new(StringCatalog.Tables, LanguageCatalog.SourceCode);
    private static string L(string key) => En.Get(key);
    private static string F(string key, params object?[] args) => En.Format(key, args);
    private static readonly DateTimeOffset Now = new(2026, 10, 27, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Announcements_MatchCoreWording()
    {
        Assert.Equal(AnnouncementText.DownloadCompleted("f.zip"), F("announce.download.completed", "f.zip"));
        Assert.Equal(AnnouncementText.DownloadFailed("f.zip", null), F("announce.download.failed", "f.zip"));
        Assert.Equal(AnnouncementText.DownloadFailed("f.zip", "offline"),
            F("announce.download.failed_reason", "f.zip", "offline"));
        Assert.Equal(AnnouncementText.UpdateAvailable("v1.8.0"), F("announce.update.available", "v1.8.0"));
    }

    [Fact]
    public void TraySuspendLines_MatchCoreWording()
    {
        Assert.Equal(SuspendTimeouts.Label(SuspendTimeout.OneMinute), L("tray.suspend.1min"));
        Assert.Equal(SuspendTimeouts.Label(SuspendTimeout.FiveMinutes), L("tray.suspend.5min"));
        Assert.Equal(SuspendTimeouts.Label(SuspendTimeout.FifteenMinutes), L("tray.suspend.15min"));
        Assert.Equal(SuspendTimeouts.Label(SuspendTimeout.Never), L("tray.suspend.never"));
        Assert.Equal(TrayStatusText.SuspendLine(false, true, true, SuspendTimeout.Never), L("tray.webview_suspended"));
        Assert.Equal(TrayStatusText.SuspendLine(false, false, false, SuspendTimeout.Never), L("tray.webview_suspend_off"));
        Assert.Equal(TrayStatusText.SuspendLine(false, false, true, SuspendTimeout.Never), L("tray.webview_stays_awake"));
        Assert.Equal(TrayStatusText.SuspendLine(false, false, true, SuspendTimeout.FiveMinutes), L("tray.webview_awake"));
        Assert.Equal(
            TrayStatusText.SuspendLine(true, false, true, SuspendTimeout.FiveMinutes),
            F("tray.webview_suspends_after", SuspendTimeouts.Label(SuspendTimeout.FiveMinutes)));
    }

    [Fact]
    public void FocusTimerLines_MatchCoreWording()
    {
        var timer = new FocusTimer();
        Assert.Equal("", timer.StatusLine(Now));
        timer.Start(Now, 25);
        Assert.Equal(timer.StatusLine(Now), F("timer.focus_left", TimerText.Format(timer.Remaining(Now))));
        timer.Tick(Now.AddMinutes(25));
        Assert.Equal(FocusTimerState.BreakSuggested, timer.State);
        Assert.Equal(timer.StatusLine(Now), L("timer.focus_block_done"));
        Assert.True(timer.StartBreak(Now.AddMinutes(25), 5));
        Assert.Equal(timer.StatusLine(Now.AddMinutes(25)),
            F("timer.break_left", TimerText.Format(timer.Remaining(Now.AddMinutes(25)))));
    }

    [Fact]
    public void SessionTimerLines_MatchCoreWording()
    {
        var config = new SessionTimerConfig(25, 5, 15, 4);
        var timer = new SessionTimer(config);
        Assert.Equal("", timer.StatusLine(Now));
        timer.Start(Now);
        Assert.Equal(timer.StatusLine(Now),
            F("timer.session_work", TimerText.Format(timer.Remaining(Now)), timer.CycleInSet + 1, timer.CyclesBeforeLongBreak));
        timer.Pause(Now);
        Assert.Equal(timer.StatusLine(Now),
            F("timer.session_work_paused", TimerText.Format(timer.Remaining(Now)), timer.CycleInSet + 1, timer.CyclesBeforeLongBreak));
        timer.Resume(Now);
        timer.Tick(Now.AddMinutes(25));
        Assert.Equal(SessionPhase.ShortBreak, timer.Phase);
        Assert.Equal(timer.StatusLine(Now.AddMinutes(25)),
            F("timer.session_break", TimerText.Format(timer.Remaining(Now.AddMinutes(25)))));
        timer.Pause(Now.AddMinutes(25));
        Assert.Equal(timer.StatusLine(Now.AddMinutes(25)),
            F("timer.session_break_paused", TimerText.Format(timer.Remaining(Now.AddMinutes(25)))));
    }

    [Fact]
    public void SelfTestLines_MatchCoreWording()
    {
        Assert.Equal(GuidedSelfTests.MicrophoneLine(MicTestOutcome.HeardSound), L("selftest.mic.heard"));
        Assert.Equal(GuidedSelfTests.MicrophoneLine(MicTestOutcome.GrantedButSilent), L("selftest.mic.silent"));
        Assert.Equal(GuidedSelfTests.MicrophoneLine(MicTestOutcome.Denied), L("selftest.mic.denied"));
        Assert.Equal(GuidedSelfTests.MicrophoneLine(MicTestOutcome.NoDevice), L("selftest.mic.nodevice"));
        Assert.Equal(GuidedSelfTests.MicrophoneLine(MicTestOutcome.TimedOut), L("selftest.mic.timeout"));
        Assert.Equal(GuidedSelfTests.MicrophoneLine(MicTestOutcome.DeviceError), L("selftest.mic.error"));
        Assert.Equal(GuidedSelfTests.SavedMicLine(null), L("selftest.mic.saved_none"));
        Assert.Equal(GuidedSelfTests.SavedMicLine("ask"), F("selftest.mic.saved_choice", "ask"));
        Assert.Equal(GuidedSelfTests.NotificationLine(NotificationSuppression.None, true),
            L("selftest.notification.sent"));
        Assert.Equal(GuidedSelfTests.NotificationLine(NotificationSuppression.None, false),
            L("selftest.notification.not_shown"));
        Assert.Equal(NotificationPolicy.ReasonText(NotificationSuppression.WindowsDoNotDisturb), L("selftest.reason.dnd"));
        Assert.Equal(NotificationPolicy.ReasonText(NotificationSuppression.QuietHours), L("selftest.reason.quiet_hours"));
        Assert.Equal(GuidedSelfTests.NotificationLine(NotificationSuppression.WindowsDoNotDisturb, true),
            F("selftest.notification.sent_despite", L("selftest.reason.dnd")));
        Assert.Equal(GuidedSelfTests.NotificationLine(NotificationSuppression.QuietHours, true),
            F("selftest.notification.sent_despite", L("selftest.reason.quiet_hours")));
    }

    [Fact]
    public void DownloadLines_MatchCoreWording()
    {
        Assert.Equal(DownloadText.FormatEta(TimeSpan.FromSeconds(45)), F("dl.eta_sec", 45));
        Assert.Equal(DownloadText.FormatEta(TimeSpan.FromMinutes(2.5)), F("dl.eta_min", 3));

        var (text, right) = DownloadText.ProgressStatus("f.zip", 512 * 1024, 1024 * 1024, null, Now);
        Assert.Equal(F("dl.progress", "f.zip", 50), text);
        Assert.Equal(F("dl.progress_right", 512, 1024), right);

        var (etaText, _) = DownloadText.ProgressStatus("f.zip", 512 * 1024, 1024 * 1024, Now.AddMinutes(2.5), Now);
        Assert.Equal(F("dl.progress", "f.zip", 50) + F("dl.eta_suffix", F("dl.eta_min", 3)), etaText);

        var (unknownText, unknownRight) = DownloadText.ProgressStatus("f.zip", 2048, null, null, Now);
        Assert.Equal(F("dl.progress_unknown", "f.zip"), unknownText);
        Assert.Equal(F("dl.progress_right_received", 2), unknownRight);

        var paused = new ActiveDownload { Id = "1", FileName = "a", State = DownloadActivityState.Paused, BytesReceived = 250, TotalBytes = 1000 };
        Assert.Equal(
            F("dl.detail_paused_pct", 25, DownloadText.FormatBytes(250), DownloadText.FormatBytes(1000)),
            DownloadText.ActiveDetail(paused, Now));
        Assert.Equal(L("dl.detail_failed"),
            DownloadText.ActiveDetail(new ActiveDownload { State = DownloadActivityState.Failed }, Now));
        Assert.Equal(L("dl.detail_cancelled"),
            DownloadText.ActiveDetail(new ActiveDownload { State = DownloadActivityState.Cancelled }, Now));
        Assert.Equal(L("dl.detail_complete"),
            DownloadText.ActiveDetail(new ActiveDownload { State = DownloadActivityState.Completed }, Now));
        var running = new ActiveDownload { Id = "2", FileName = "b", State = DownloadActivityState.InProgress, BytesReceived = 250, TotalBytes = 1000 };
        Assert.Equal(
            F("dl.detail_pct", 25) + " · " + F("dl.of_total", DownloadText.FormatBytes(250), DownloadText.FormatBytes(1000)),
            DownloadText.ActiveDetail(running, Now));
    }

    [Fact]
    public void AggregateLines_MatchCoreWording()
    {
        var one = new ActiveDownloadSet();
        one.Add(new ActiveDownload { Id = "1", State = DownloadActivityState.Paused });
        Assert.Equal(L("dl.agg_paused_one"), DownloadText.AggregateStatus(one, Now).Text);

        var two = new ActiveDownloadSet();
        two.Add(new ActiveDownload { Id = "1", State = DownloadActivityState.Paused });
        two.Add(new ActiveDownload { Id = "2", State = DownloadActivityState.Paused });
        Assert.Equal(F("dl.agg_paused_many", 2), DownloadText.AggregateStatus(two, Now).Text);

        var running = new ActiveDownloadSet();
        running.Add(new ActiveDownload { Id = "1", State = DownloadActivityState.InProgress, BytesReceived = 250, TotalBytes = 1000 });
        running.Add(new ActiveDownload { Id = "2", State = DownloadActivityState.InProgress, BytesReceived = 250, TotalBytes = 1000 });
        var (text, right) = DownloadText.AggregateStatus(running, Now);
        Assert.Equal(F("dl.agg_downloading_pct", 2, 25), text);
        Assert.Equal(F("dl.of_total", DownloadText.FormatBytes(500), DownloadText.FormatBytes(2000)), right);
    }

    [Fact]
    public void CommandLineUsage_MatchesTheCatalog()
    {
        // --help prints the catalog text in the active language; the
        // English template must stay byte-identical to the documented
        // Core original (README excerpts quote it).
        Assert.Equal(CommandLineUsage.Text("1.8.0"), F("app.usage.text", "1.8.0"));
    }

    [Fact]
    public void ShortcutNames_MatchTheCatalog()
    {
        foreach (var action in ShortcutCatalog.All)
            Assert.Equal(action.Name, L("shortcut." + action.Id));
        Assert.Equal(ShortcutCatalog.All.Count,
            StringCatalog.Source.Keys.Count(k => k.StartsWith("shortcut.", StringComparison.Ordinal)));
    }

    [Fact]
    public void ClearDataTitles_RelateToTheCatalog()
    {
        Assert.Equal(ClearDataPlan.Title(ClearDataChoice.AllSiteData), L("settings.clear.all"));
        Assert.Equal(ClearDataPlan.Title(ClearDataChoice.Cookies), L("settings.clear.cookies") + " only");
        Assert.Equal(ClearDataPlan.Title(ClearDataChoice.Cache), L("settings.clear.cache") + " only");
    }

    [Fact]
    public void SettingsSections_LanguagePlacementAndTitlesInCatalog()
    {
        var sections = SettingsSearch.Sections;
        var language = sections.Single(s => s.Id == SettingsSearch.LanguageId);
        Assert.Equal("Language & announcements", language.Title);
        var ids = sections.Select(s => s.Id).ToList();
        Assert.True(ids.IndexOf(SettingsSearch.LanguageId) > ids.IndexOf(SettingsSearch.AccessibilityId));
        Assert.True(ids.IndexOf(SettingsSearch.LanguageId) < ids.IndexOf(SettingsSearch.DownloadsId));
        // Every section's title is a catalog value (the Settings
        // window's headers render from the catalog, #161).
        foreach (var section in sections)
            Assert.Contains(section.Title, StringCatalog.Source.Values);
    }
}

// ───────────────────── R7 #161/#167/#168: the shell drift guards ─────────────────────

/// <summary>
/// The drift guards (R7 #161) and the extended accessibility audits
/// (#167/#168). #161: every key the shell asks for — in XAML through
/// {local:Loc} and in code through Strings.Get/Format — exists in
/// the catalog, and no user-facing XAML attribute carries a literal
/// string outside the reviewed allowlist below. #167: the S6 label
/// audit's element set is extended to every control type the shell
/// has gained since round 1. #168: every interactive control outside
/// templates carries an explicit KeyboardNavigation.TabIndex,
/// strictly increasing in reading (document) order per window.
/// </summary>
public class ShellLocalizationAndAuditTests
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(
            Path.GetDirectoryName(typeof(ShellLocalizationAndAuditTests).Assembly.Location)!);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "MuseDesktop.sln"))
                && Directory.Exists(Path.Combine(dir.FullName, "src", "MuseDesktop")))
                return dir.FullName;
            dir = dir.Parent;
        }
        throw new InvalidOperationException("Could not locate the repo root from the test assembly.");
    }

    private static string ShellDir() => Path.Combine(RepoRoot(), "src", "MuseDesktop");

    private static IEnumerable<XElement> DescendantsSkippingTemplates(XElement root)
    {
        foreach (var child in root.Elements())
        {
            var name = child.Name.LocalName;
            if (name is "DataTemplate" or "ControlTemplate" or "ItemsPanelTemplate"
                || name.EndsWith(".ItemTemplate", StringComparison.Ordinal))
                continue;
            yield return child;
            foreach (var nested in DescendantsSkippingTemplates(child))
                yield return nested;
        }
    }

    [Fact]
    public void EveryLocKeyInXaml_ExistsInTheCatalog()
    {
        var pattern = new Regex(@"\{local:Loc ([A-Za-z0-9_.]+)\}");
        var missing = new List<string>();
        var used = 0;
        foreach (var file in Directory.GetFiles(ShellDir(), "*.xaml"))
        {
            foreach (Match match in pattern.Matches(File.ReadAllText(file)))
            {
                used++;
                if (!StringCatalog.Source.ContainsKey(match.Groups[1].Value))
                    missing.Add($"{Path.GetFileName(file)}: {match.Groups[1].Value}");
            }
        }
        Assert.True(used > 500, $"The guard scanned suspiciously few Loc keys ({used}) — is it looking at the shell?");
        Assert.True(missing.Count == 0,
            "XAML asks for catalog keys that don't exist:\n" + string.Join("\n", missing));
    }

    [Fact]
    public void TemplatedControls_NeverGetDisplayMemberPath()
    {
        // Regression guard for the v1.7.0 field crash: a control with an
        // ItemTemplate must never also set DisplayMemberPath — WPF throws
        // InvalidOperationException ("Cannot set both DisplayMemberPath
        // and ItemTemplate") when the items are generated, which killed
        // the Settings window on open (SettingsWindow.RefreshProfileCombo
        // set it on the templated ProfileCombo).
        var xamlNs = XNamespace.Get("http://schemas.microsoft.com/winfx/2006/xaml");
        var templated = new HashSet<string>(StringComparer.Ordinal);
        var offenders = new List<string>();
        foreach (var file in Directory.GetFiles(ShellDir(), "*.xaml"))
        {
            var doc = XDocument.Load(file);
            foreach (var template in doc.Descendants().Where(e => e.Name.LocalName.EndsWith(".ItemTemplate")))
            {
                var control = template.Parent;
                if (control is null)
                    continue;
                if (control.Attribute("DisplayMemberPath") is not null)
                    offenders.Add($"{Path.GetFileName(file)}: {control.Name.LocalName} sets DisplayMemberPath in XAML and has a template");
                var name = control.Attribute(xamlNs + "Name")?.Value;
                if (name is not null)
                    templated.Add(name);
            }
        }
        Assert.True(templated.Count > 0, "The guard found no templated controls — is it looking at the shell?");
        var assign = new Regex(@"(\w+)\.DisplayMemberPath\s*=");
        foreach (var file in Directory.GetFiles(ShellDir(), "*.cs", SearchOption.AllDirectories))
        {
            foreach (Match match in assign.Matches(File.ReadAllText(file)))
            {
                if (templated.Contains(match.Groups[1].Value))
                    offenders.Add($"{Path.GetFileName(file)}: {match.Groups[1].Value}.DisplayMemberPath is assigned in code but the control has an ItemTemplate");
            }
        }
        Assert.True(offenders.Count == 0,
            "DisplayMemberPath + ItemTemplate is a guaranteed WPF crash:\n" + string.Join("\n", offenders));
    }

    [Fact]
    public void MainWindowTitle_IsNeverComposed()
    {
        // SEAN, 2026-10-27: the main window's titlebar says
        // "MuseDesktop" and nothing else. The S1 #12 composition
        // (page title + profile name) must never come back: the
        // shell assigns the Core constant and Compose no longer
        // exists to call.
        var main = File.ReadAllText(Path.Combine(ShellDir(), "MainWindow.xaml.cs"));
        Assert.Contains("Title = WindowTitle.Text;", main);
        Assert.DoesNotContain("WindowTitle.Compose", main);
    }

    [Fact]
    public void EveryStringKeyInShellCode_ExistsInTheCatalog()
    {
        var pattern = new Regex(@"Strings\.(?:Get|Format)\(""([A-Za-z0-9_.]+)""");
        var missing = new List<string>();
        var used = 0;
        foreach (var file in Directory.GetFiles(ShellDir(), "*.cs", SearchOption.AllDirectories))
        {
            foreach (Match match in pattern.Matches(File.ReadAllText(file)))
            {
                used++;
                var key = match.Groups[1].Value;
                if (key.EndsWith('.'))
                {
                    // A prefix composed at runtime ("shortcut." + id):
                    // the family must exist; the ShortcutCatalog pin
                    // covers every concrete member.
                    if (!StringCatalog.Source.Keys.Any(k => k.StartsWith(key, StringComparison.Ordinal)))
                        missing.Add($"{Path.GetFileName(file)}: prefix {key} (no catalog keys)");
                }
                else if (!StringCatalog.Source.ContainsKey(key))
                {
                    missing.Add($"{Path.GetFileName(file)}: {key}");
                }
            }
        }
        Assert.True(used > 300, $"The guard scanned suspiciously few code keys ({used}) — is it looking at the shell?");
        Assert.True(missing.Count == 0,
            "Shell code asks for catalog keys that don't exist:\n" + string.Join("\n", missing));
    }

    /// <summary>
    /// The reviewed allowlist for literal XAML text (R7 #161): brand
    /// names, icon glyphs (their accessible names ARE localized), the
    /// zoom readout's format, and a 24-hour time example. Anything
    /// else literal in a user-facing attribute is a hard-coded string
    /// that bypasses the catalog — add the string to the catalog, or
    /// extend this list only with a reason recorded in DECISIONS.md.
    /// </summary>
    private static readonly HashSet<string> LiteralAllowlist = new()
    {
        "Muse", "MuseDesktop",                         // brand names are not translated
        "+", "−", "100%",                                // zoom controls + readout format
        "09:00",                                         // 24-hour time example (a format, not prose)
        "←", "→", "↑", "↓", "↗", "⋯", "⌂", "✕", "⟳", "📌", "🔖", // icon glyphs
    };

    private static readonly string[] UserFacingAttributes =
    {
        "Content", "Header", "Text", "ToolTip", "Title",
        "AutomationProperties.Name", "AutomationProperties.HelpText",
    };

    [Fact]
    public void NoHardCodedUserFacingStringsInXaml()
    {
        var failures = new List<string>();
        foreach (var file in Directory.GetFiles(ShellDir(), "*.xaml"))
        {
            var doc = XDocument.Load(file);
            Assert.NotNull(doc.Root);
            foreach (var element in DescendantsSkippingTemplates(doc.Root!))
            {
                foreach (var attr in element.Attributes())
                {
                    if (!UserFacingAttributes.Contains(attr.Name.LocalName))
                        continue;
                    var value = attr.Value;
                    if (string.IsNullOrWhiteSpace(value) || value.StartsWith('{'))
                        continue;   // a binding / markup extension — the catalog feeds it
                    if (!LiteralAllowlist.Contains(value))
                        failures.Add($"{Path.GetFileName(file)}: <{element.Name.LocalName}> {attr.Name.LocalName}=\"{value}\"");
                }
                // Literal inner text is prose too — the shell keeps
                // ALL of it in attributes, so any is a bypass.
                var innerText = string.Concat(element.Nodes().OfType<XText>().Select(t => t.Value)).Trim();
                if (innerText.Length > 0 && !LiteralAllowlist.Contains(innerText))
                    failures.Add($"{Path.GetFileName(file)}: <{element.Name.LocalName}> inner text \"{innerText}\"");
            }
        }
        Assert.True(failures.Count == 0,
            "Hard-coded user-facing strings in shell XAML (externalize them to the catalog):\n"
            + string.Join("\n", failures));
    }

    private static readonly string[] InteractiveElements =
    {
        // The S6 set…
        "Button", "MenuItem", "TextBox", "ComboBox", "CheckBox",
        "RadioButton", "Hyperlink", "ToggleButton", "ListBox",
        // …extended for #167 with the control types added since.
        "PasswordBox", "Slider", "ListView", "TreeView", "RichTextBox",
    };

    private static bool HasAccessibleLabel(XElement element)
    {
        string? Attr(string name) => element.Attribute(name)?.Value;
        if (!string.IsNullOrWhiteSpace(Attr("AutomationProperties.Name")))
            return true;
        if (!string.IsNullOrWhiteSpace(Attr("ToolTip")))
            return true;
        if (!string.IsNullOrWhiteSpace(Attr("Content")))
            return true;
        if (!string.IsNullOrWhiteSpace(Attr("Header")))
            return true;
        // Text anywhere inside (a TextBlock child, a Run's Text…).
        var innerText = string.Concat(element.DescendantNodes().OfType<XText>().Select(t => t.Value)).Trim();
        if (innerText.Length > 0)
            return true;
        return element.DescendantsAndSelf()
            .Any(e => !string.IsNullOrWhiteSpace(e.Attribute("Text")?.Value));
    }

    private static string ElementName(XElement element) =>
        element.Attribute("{http://schemas.microsoft.com/winfx/2006/xaml}Name")?.Value
        ?? element.Attribute("Name")?.Value ?? "(unnamed)";

    [Fact]
    public void ExtendedAudit_EveryControlHasAnAccessibleLabel()
    {
        var xamlFiles = Directory.GetFiles(ShellDir(), "*.xaml");
        Assert.True(xamlFiles.Length >= 25,
            $"The audit scanned suspiciously few windows ({xamlFiles.Length}) — is it looking at the shell?");
        var failures = new List<string>();
        foreach (var file in xamlFiles)
        {
            var doc = XDocument.Load(file);
            Assert.NotNull(doc.Root);
            foreach (var element in DescendantsSkippingTemplates(doc.Root!))
            {
                if (!InteractiveElements.Contains(element.Name.LocalName))
                    continue;
                if (!HasAccessibleLabel(element))
                    failures.Add($"{Path.GetFileName(file)}: <{element.Name.LocalName}> '{ElementName(element)}'");
            }
        }
        Assert.True(failures.Count == 0,
            "Interactive shell controls without an AutomationProperties.Name, ToolTip, or text content:\n"
            + string.Join("\n", failures));
    }

    [Fact]
    public void TabOrder_EveryControlHasAnExplicitIncreasingTabIndex()
    {
        var failures = new List<string>();
        foreach (var file in Directory.GetFiles(ShellDir(), "*.xaml"))
        {
            var doc = XDocument.Load(file);
            Assert.NotNull(doc.Root);
            var previous = 0;
            foreach (var element in DescendantsSkippingTemplates(doc.Root!))
            {
                if (!InteractiveElements.Contains(element.Name.LocalName))
                    continue;
                if (element.Attribute("Focusable")?.Value == "False")
                    continue;   // deliberately out of the tab chain
                var raw = element.Attribute("KeyboardNavigation.TabIndex")?.Value;
                if (raw is null || !int.TryParse(raw, out var index))
                {
                    failures.Add($"{Path.GetFileName(file)}: <{element.Name.LocalName}> '{ElementName(element)}' has no explicit TabIndex");
                    continue;
                }
                if (index <= previous)
                    failures.Add($"{Path.GetFileName(file)}: <{element.Name.LocalName}> '{ElementName(element)}' TabIndex {index} does not follow {previous} in reading order");
                previous = index;
            }
        }
        Assert.True(failures.Count == 0,
            "Tab-order problems (R7 #168 — a deliberate order in every window):\n"
            + string.Join("\n", failures));
    }
}

/// <summary>
/// R7 #161, the code-side sinks: the string a user reads most often
/// arrives through a TextPromptWindow title/body, a MessageBox, or a
/// direct .Text/.Content assignment. After the R7 sweep none of those
/// sinks may carry a hard-coded prose literal — the text must come
/// from the catalog through Strings.Get/Format. (Log lines, Core
/// composer output, and punctuation/number-only compositions are not
/// sinks; the checks below only fire on literal prose.)
/// </summary>
public class ShellSinkGuardTests
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(
            Path.GetDirectoryName(typeof(ShellSinkGuardTests).Assembly.Location)!);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "MuseDesktop.sln"))
                && Directory.Exists(Path.Combine(dir.FullName, "src", "MuseDesktop")))
                return dir.FullName;
            dir = dir.Parent;
        }
        throw new InvalidOperationException("Could not locate the repo root from the test assembly.");
    }

    private static IEnumerable<(string File, string Source)> ShellSources()
    {
        var dir = Path.Combine(RepoRoot(), "src", "MuseDesktop");
        foreach (var path in Directory.EnumerateFiles(dir, "*.cs", SearchOption.TopDirectoryOnly))
            yield return (Path.GetFileName(path), File.ReadAllText(path));
    }

    private static string StripHoles(string literal)
    {
        var sb = new System.Text.StringBuilder();
        var depth = 0;
        foreach (var c in literal)
        {
            if (c == '{') { depth++; continue; }
            if (c == '}') { depth = Math.Max(0, depth - 1); continue; }
            if (depth == 0) sb.Append(c);
        }
        return sb.ToString();
    }

    private static bool IsProse(string literal)
    {
        var text = StripHoles(literal);
        return text.Contains(' ') && Regex.IsMatch(text, "[A-Za-z]{3}");
    }

    [Fact]
    public void NoLiteralTextPromptTitles()
    {
        var failures = new List<string>();
        foreach (var (file, source) in ShellSources())
            if (source.Contains("new TextPromptWindow(\"", StringComparison.Ordinal))
                failures.Add(file);
        Assert.True(failures.Count == 0,
            "TextPromptWindow built with a literal title (R7 #161 — use Strings.Get): "
            + string.Join(", ", failures));
    }

    [Fact]
    public void NoLiteralDisplayPropertyAssignments()
    {
        var pattern = new Regex(
            "\\.(Text|Content|Title|Header|ToolTip)\\s*=\\s*\\$?\"((?:[^\"\\\\]|\\\\.)*)\"");
        var failures = new List<string>();
        foreach (var (file, source) in ShellSources())
            foreach (Match m in pattern.Matches(source))
                if (Regex.IsMatch(StripHoles(m.Groups[2].Value), "[A-Za-z]"))
                    failures.Add($"{file}: .{m.Groups[1].Value} = \"{m.Groups[2].Value}\"");
        Assert.True(failures.Count == 0,
            "Display property assigned a literal string (R7 #161 — use Strings.Get/Format):\n"
            + string.Join("\n", failures));
    }

    [Fact]
    public void NoProseLiteralsInsideMessageBoxCalls()
    {
        var literal = new Regex("\"((?:[^\"\\\\]|\\\\.)*)\"");
        var failures = new List<string>();
        foreach (var (file, source) in ShellSources())
        {
            var at = 0;
            while ((at = source.IndexOf("MessageBox.Show(", at, StringComparison.Ordinal)) >= 0)
            {
                var depth = 1;
                var i = at + "MessageBox.Show(".Length;
                while (depth > 0 && i < source.Length)
                {
                    if (source[i] == '(') depth++;
                    else if (source[i] == ')') depth--;
                    i++;
                }
                var block = source[at..i];
                foreach (Match m in literal.Matches(block))
                    if (IsProse(m.Groups[1].Value))
                        failures.Add($"{file}: \"{m.Groups[1].Value}\"");
                at = i;
            }
        }
        Assert.True(failures.Count == 0,
            "Prose literal inside a MessageBox.Show call (R7 #161 — use Strings.Get/Format):\n"
            + string.Join("\n", failures));
    }
}
