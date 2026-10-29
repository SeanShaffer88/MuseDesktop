using System.Text.Json;
using System.Text.RegularExpressions;

namespace MuseDesktop.Core;

// ───────────────────── languages (R7 #161–#166) ─────────────────────

/// <summary>
/// One language the shell can speak (R7 #161): the BCP-47 code, its
/// English name (for logs and support), its name in itself (what the
/// picker shows), and its reading direction. Direction comes from the
/// primary subtag — a language is right-to-left because of what it
/// IS, not because a table says so per row.
/// </summary>
public sealed record LanguageInfo(string Code, string EnglishName, string NativeName, bool IsRightToLeft);

public static class LanguageCatalog
{
    public const string SourceCode = "en-US";
    public const string SpanishCode = "es-ES";

    /// <summary>
    /// Primary subtags whose languages are written right-to-left
    /// (Arabic, Hebrew, Persian, Urdu, Pashto, Sindhi, Uyghur,
    /// Yiddish, Dhivehi, Central Kurdish). The engine knows the
    /// direction the day a translation for one of them lands — #166
    /// is readiness, no RTL language ships in v1.8.0.
    /// </summary>
    private static readonly HashSet<string> RtlPrimaries = new(StringComparer.OrdinalIgnoreCase)
    {
        "ar", "he", "fa", "ur", "ps", "sd", "ug", "yi", "dv", "ckb",
    };

    /// <summary>The languages built into the app. Imported packs (#165) add more at runtime.</summary>
    public static readonly IReadOnlyList<LanguageInfo> BuiltIn = new[]
    {
        new LanguageInfo(SourceCode, "English (United States)", "English (United States)", false),
        new LanguageInfo(SpanishCode, "Spanish (Spain)", "Español (España)", false),
    };

    public static bool IsRightToLeft(string? languageCode)
    {
        if (string.IsNullOrWhiteSpace(languageCode))
            return false;
        var primary = languageCode.Trim().Split('-')[0];
        return RtlPrimaries.Contains(primary);
    }

    /// <summary>Describe any language code (built-in, packed, or future) for the picker and reports.</summary>
    public static LanguageInfo Describe(string code)
    {
        var known = BuiltIn.FirstOrDefault(l => string.Equals(l.Code, code, StringComparison.OrdinalIgnoreCase));
        if (known is not null)
            return known;
        return new LanguageInfo(code, code, code, IsRightToLeft(code));
    }

    /// <summary>A syntactically plausible BCP-47 code ("es", "es-ES", "pt-BR", "zh-Hans"): letters/digits subtags, 2–8 chars each, primary 2–3 letters.</summary>
    public static bool IsPlausibleCode(string? code)
    {
        if (string.IsNullOrWhiteSpace(code) || code.Length > 35)
            return false;
        var parts = code.Trim().Split('-');
        if (parts[0].Length is < 2 or > 3 || !parts[0].All(char.IsLetter))
            return false;
        return parts.All(p => p.Length is >= 2 and <= 8 && p.All(char.IsLetterOrDigit));
    }

    /// <summary>
    /// Resolve a language preference to one of the AVAILABLE languages:
    /// exact code match first, then a primary-subtag match ("es-MX"
    /// with only "es-ES" available lands on "es-ES"), else the source
    /// language. A null/blank preference means "the system language",
    /// so the caller passes the OS culture in as the preference.
    /// </summary>
    public static string Resolve(string? preference, IReadOnlyCollection<string> available)
    {
        if (available.Count == 0)
            return SourceCode;
        if (!string.IsNullOrWhiteSpace(preference))
        {
            var wanted = preference.Trim();
            var exact = available.FirstOrDefault(a => string.Equals(a, wanted, StringComparison.OrdinalIgnoreCase));
            if (exact is not null)
                return exact;
            var primary = wanted.Split('-')[0];
            var byPrimary = available.FirstOrDefault(a =>
                string.Equals(a.Split('-')[0], primary, StringComparison.OrdinalIgnoreCase));
            if (byPrimary is not null)
                return byPrimary;
        }
        return available.Contains(SourceCode) ? SourceCode : available.First();
    }
}

// ───────────────────── placeholders ─────────────────────

/// <summary>
/// The placeholder shape of a catalog string (R7 #164/#165): the set
/// of <c>{0}</c> / <c>{name}</c> tokens a translation must preserve
/// exactly. A translation that drops or renames one would print the
/// token literally (or throw at format time), so packs and the
/// Spanish table are validated against the English shape.
/// </summary>
public static class PlaceholderShape
{
    private static readonly Regex TokenPattern = new(@"\{[A-Za-z0-9_]+\}", RegexOptions.Compiled);

    public static IReadOnlyList<string> Of(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return Array.Empty<string>();
        return TokenPattern.Matches(text).Select(m => m.Value)
            .Distinct(StringComparer.Ordinal).OrderBy(t => t, StringComparer.Ordinal).ToList();
    }

    public static bool Matches(string? source, string? translation) =>
        Of(source).SequenceEqual(Of(translation));
}

// ───────────────────── the localizer ─────────────────────

/// <summary>
/// The string resolver (R7 #161): key → text in the current language,
/// falling back to the en-US source when the current language lacks
/// the key (#164 — the fallback is the design, not a failure; the
/// coverage report counts how often it would happen). Constructed
/// from plain tables (language code → key → text) so tests can build
/// any language mix — including the RTL proof language of #166 —
/// without touching the shipped catalog.
/// </summary>
public sealed class Localizer
{
    private readonly IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> _tables;

    public Localizer(
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> tables, string language)
    {
        _tables = tables;
        Language = language;
        Source = tables.TryGetValue(LanguageCatalog.SourceCode, out var source)
            ? source
            : new Dictionary<string, string>();
        Current = tables.TryGetValue(language, out var current) ? current : Source;
    }

    /// <summary>The resolved language code this localizer speaks.</summary>
    public string Language { get; }

    /// <summary>The en-US source table (the key universe).</summary>
    public IReadOnlyDictionary<string, string> Source { get; }

    /// <summary>The current language's table (the source itself when Language is en-US).</summary>
    public IReadOnlyDictionary<string, string> Current { get; }

    public bool IsRightToLeft => LanguageCatalog.IsRightToLeft(Language);

    /// <summary>Every language this localizer's tables carry.</summary>
    public IReadOnlyCollection<string> AvailableLanguages => _tables.Keys.ToList();

    /// <summary>
    /// The text for <paramref name="key"/>: current language, else
    /// the English source, else — a key nothing knows — the key
    /// itself (a drift-guard failure the tests exist to prevent; the
    /// app must still show SOMETHING rather than crash).
    /// </summary>
    public string Get(string key)
    {
        if (string.IsNullOrEmpty(key))
            return "";
        if (Current.TryGetValue(key, out var text) && !string.IsNullOrEmpty(text))
            return text;
        if (Source.TryGetValue(key, out var source))
            return source;
        return key;
    }

    /// <summary>Format a catalog template: <c>{0}</c>, <c>{1}</c>… filled from <paramref name="args"/> in the current language's culture.</summary>
    public string Format(string key, params object?[] args) =>
        string.Format(System.Globalization.CultureInfo.GetCultureInfo(Language), Get(key), args);

    /// <summary>Keys the current language has not translated (they fall back to English). Empty for en-US.</summary>
    public IReadOnlyList<string> MissingKeys =>
        Source.Keys.Where(k => !Current.TryGetValue(k, out var v) || string.IsNullOrEmpty(v))
            .OrderBy(k => k, StringComparer.Ordinal).ToList();
}

// ───────────────────── the shipped catalog (R7 #161/#163) ─────────────────────

/// <summary>
/// The built-in string catalog (R7 #161): two JSON tables embedded in
/// Core — <c>Strings.en-US.json</c> (the source; every key exists)
/// and <c>Strings.es-ES.json</c> (the full Spanish translation, #163).
/// Kept as data, not code, so a translator (or the pack exporter,
/// #165) works with the same shape the validator checks.
/// </summary>
public static class StringCatalog
{
    private static readonly Lazy<IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>>> LazyTables =
        new(LoadTables);

    /// <summary>All built-in tables: language code → (key → text).</summary>
    public static IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> Tables => LazyTables.Value;

    /// <summary>The en-US source table.</summary>
    public static IReadOnlyDictionary<string, string> Source => Tables[LanguageCatalog.SourceCode];

    private static IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> LoadTables()
    {
        var tables = new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        var assembly = typeof(StringCatalog).Assembly;
        foreach (var code in new[] { LanguageCatalog.SourceCode, LanguageCatalog.SpanishCode })
        {
            var name = assembly.GetManifestResourceNames()
                .FirstOrDefault(n => n.EndsWith($"Strings.{code}.json", StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException($"Embedded string catalog for {code} is missing.");
            using var stream = assembly.GetManifestResourceStream(name)!;
            using var reader = new StreamReader(stream);
            var parsed = JsonSerializer.Deserialize<Dictionary<string, string>>(reader.ReadToEnd())
                         ?? throw new InvalidOperationException($"Embedded string catalog for {code} is unreadable.");
            tables[code] = parsed;
        }
        return tables;
    }

    /// <summary>
    /// Merge imported pack tables (#165) over the built-ins: a pack
    /// for a new language adds it; a pack naming a built-in language
    /// replaces that language's table (the user's own translation of
    /// their own language wins on their own PC).
    /// </summary>
    public static IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> Merge(
        IReadOnlyDictionary<string, LanguagePack> packs)
    {
        var merged = new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (code, table) in Tables)
            merged[code] = table;
        foreach (var (code, pack) in packs)
            merged[code] = pack.Strings;
        return merged;
    }

    /// <summary>Build the localizer for a launch: preference (null = system) resolved against built-ins + packs.</summary>
    public static Localizer CreateLocalizer(
        string? preference, string? systemLanguage,
        IReadOnlyDictionary<string, LanguagePack>? packs = null)
    {
        var tables = packs is { Count: > 0 } ? Merge(packs) : Tables;
        var language = LanguageCatalog.Resolve(preference ?? systemLanguage, tables.Keys.ToList());
        return new Localizer(tables, language);
    }
}

// ───────────────────── coverage (R7 #164) ─────────────────────

/// <summary>One language's translation coverage against the source table.</summary>
public sealed record LanguageCoverage(
    string Language, int TotalKeys, int TranslatedKeys, IReadOnlyList<string> MissingKeys)
{
    public double Percent => TotalKeys == 0 ? 100.0 : 100.0 * TranslatedKeys / TotalKeys;
}

public static class LocalizationCoverage
{
    /// <summary>Coverage for every language in <paramref name="tables"/>, fullest first, source last (it defines the total).</summary>
    public static IReadOnlyList<LanguageCoverage> Report(
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> tables)
    {
        var source = tables.TryGetValue(LanguageCatalog.SourceCode, out var s)
            ? s
            : new Dictionary<string, string>();
        var result = new List<LanguageCoverage>();
        foreach (var (code, table) in tables)
        {
            if (string.Equals(code, LanguageCatalog.SourceCode, StringComparison.OrdinalIgnoreCase))
                continue;
            var missing = source.Keys
                .Where(k => !table.TryGetValue(k, out var v) || string.IsNullOrEmpty(v))
                .OrderBy(k => k, StringComparer.Ordinal).ToList();
            result.Add(new LanguageCoverage(code, source.Count, source.Count - missing.Count, missing));
        }
        result.Add(new LanguageCoverage(LanguageCatalog.SourceCode, source.Count, source.Count,
            Array.Empty<string>()));
        return result;
    }

    /// <summary>One plain line per language, for the Diagnostics report (#164).</summary>
    public static IReadOnlyList<string> ReportLines(
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> tables)
    {
        var lines = new List<string>();
        foreach (var coverage in Report(tables))
        {
            var info = LanguageCatalog.Describe(coverage.Language);
            var line = $"{coverage.Language} ({info.EnglishName}): {coverage.TranslatedKeys}/{coverage.TotalKeys} strings ({coverage.Percent:0.#}%)";
            if (coverage.MissingKeys.Count > 0)
                line += $" — {coverage.MissingKeys.Count} fall back to English";
            lines.Add(line);
        }
        return lines;
    }
}

// ───────────────────── language packs (R7 #165) ─────────────────____

/// <summary>
/// A language pack (R7 #165): one JSON file a translator writes —
/// <c>{ "language": "fr-FR", "name": "Français", "strings": { key: text, … } }</c>.
/// The app validates a pack before using it: the language code must
/// be plausible, EVERY source key must be present with a non-empty
/// translation (a half pack is a broken pack — the built-in fallback
/// covers partial languages only when they ship complete), every
/// translation's placeholders must match its source string's shape,
/// and unknown keys are refused (a typo'd key would silently never
/// show). Nothing is merged until all of that passes.
/// </summary>
public sealed record LanguagePack(string Language, string? Name, IReadOnlyDictionary<string, string> Strings)
{
    /// <summary>The display name for the picker: the pack's own name, else its language's.</summary>
    public string DisplayName => !string.IsNullOrWhiteSpace(Name) ? Name : LanguageCatalog.Describe(Language).NativeName;
}

public static class LanguagePackFile
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    /// <summary>The export template: the full English source as a pack skeleton, ready to translate.</summary>
    public static string ExportTemplate(IReadOnlyDictionary<string, string> source)
    {
        var pack = new Dictionary<string, object?>
        {
            ["language"] = "xx-XX",
            ["name"] = "",
            ["strings"] = SortedCopy(source),
        };
        return JsonSerializer.Serialize(pack, JsonOptions);
    }

    /// <summary>Serialize a validated pack (what Import writes into the packs folder).</summary>
    public static string Serialize(LanguagePack pack)
    {
        var data = new Dictionary<string, object?>
        {
            ["language"] = pack.Language,
            ["name"] = pack.Name,
            ["strings"] = SortedCopy(pack.Strings),
        };
        return JsonSerializer.Serialize(data, JsonOptions);
    }

    /// <summary>A key-sorted copy of a string table, for stable pack files.</summary>
    private static SortedDictionary<string, string> SortedCopy(IReadOnlyDictionary<string, string> table)
    {
        var sorted = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, value) in table)
            sorted[key] = value;
        return sorted;
    }

    /// <summary>The file name a pack lives under in the packs folder.</summary>
    public static string FileNameFor(string languageCode) => $"pack-{languageCode}.json";

    /// <summary>
    /// Parse + validate a pack against the source table. On failure
    /// <paramref name="error"/> says why in plain words (naming the
    /// first few offending keys) and nothing is returned.
    /// </summary>
    public static bool TryLoad(
        string? json, IReadOnlyDictionary<string, string> source,
        out LanguagePack? pack, out string? error)
    {
        pack = null;
        error = null;
        if (string.IsNullOrWhiteSpace(json))
        {
            error = "That file is empty — it isn't a language pack.";
            return false;
        }
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(json);
        }
        catch (Exception)
        {
            error = "That file couldn't be read as a language pack (it isn't valid JSON).";
            return false;
        }
        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("language", out var languageNode)
                || languageNode.ValueKind != JsonValueKind.String
                || !root.TryGetProperty("strings", out var stringsNode)
                || stringsNode.ValueKind != JsonValueKind.Object)
            {
                error = "That file isn't a language pack — it needs a \"language\" and a \"strings\" section.";
                return false;
            }
            var language = languageNode.GetString()?.Trim() ?? "";
            if (!LanguageCatalog.IsPlausibleCode(language))
            {
                error = $"\"{language}\" isn't a language code MuseDesktop can use (it should look like \"fr-FR\").";
                return false;
            }
            var strings = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var property in stringsNode.EnumerateObject())
            {
                if (property.Value.ValueKind != JsonValueKind.String)
                {
                    error = $"The translation for \"{property.Name}\" isn't text.";
                    return false;
                }
                strings[property.Name] = property.Value.GetString() ?? "";
            }
            var unknown = strings.Keys.Where(k => !source.ContainsKey(k))
                .OrderBy(k => k, StringComparer.Ordinal).ToList();
            if (unknown.Count > 0)
            {
                error = $"The pack names {unknown.Count} string(s) MuseDesktop doesn't have " +
                        $"(first: \"{unknown[0]}\") — it may be for a different version.";
                return false;
            }
            var missing = source.Keys.Where(k => !strings.TryGetValue(k, out var v) || string.IsNullOrEmpty(v))
                .OrderBy(k => k, StringComparer.Ordinal).ToList();
            if (missing.Count > 0)
            {
                error = $"The pack is missing {missing.Count} of {source.Count} strings " +
                        $"(first missing: \"{missing[0]}\"). A pack must translate every string — " +
                        "export a fresh template from Settings and fill it in completely.";
                return false;
            }
            var misshapen = source.Keys
                .Where(k => !PlaceholderShape.Matches(source[k], strings[k]))
                .OrderBy(k => k, StringComparer.Ordinal).ToList();
            if (misshapen.Count > 0)
            {
                var first = misshapen[0];
                error = $"The translation for \"{first}\" changed its placeholders — the original uses " +
                        $"{string.Join(", ", PlaceholderShape.Of(source[first]))}, the translation uses " +
                        $"{string.Join(", ", PlaceholderShape.Of(strings[first]))}. Placeholders must match exactly.";
                return false;
            }
            string? name = null;
            if (root.TryGetProperty("name", out var nameNode) && nameNode.ValueKind == JsonValueKind.String)
                name = nameNode.GetString()?.Trim();
            pack = new LanguagePack(language, string.IsNullOrEmpty(name) ? null : name, strings);
            return true;
        }
    }

    /// <summary>
    /// Load every pack in a folder (import's second half). Files that
    /// fail validation are skipped and named in
    /// <paramref name="skipped"/> — one bad file never blocks the rest,
    /// and the caller logs the names (file names only, never paths
    /// with content).
    /// </summary>
    public static IReadOnlyDictionary<string, LanguagePack> LoadFolder(
        string folder, IReadOnlyDictionary<string, string> source, out IReadOnlyList<string> skipped)
    {
        var packs = new Dictionary<string, LanguagePack>(StringComparer.OrdinalIgnoreCase);
        var bad = new List<string>();
        skipped = bad;
        if (!Directory.Exists(folder))
            return packs;
        foreach (var file in Directory.GetFiles(folder, "pack-*.json").OrderBy(f => f, StringComparer.Ordinal))
        {
            try
            {
                if (TryLoad(File.ReadAllText(file), source, out var pack, out _) && pack is not null)
                    packs[pack.Language] = pack;
                else
                    bad.Add(Path.GetFileName(file));
            }
            catch (Exception)
            {
                bad.Add(Path.GetFileName(file));
            }
        }
        return packs;
    }
}

// ───────────────────── shell text spacing (R7 #170) ─────────────────────

/// <summary>
/// Shell text spacing (R7 #170): a line-spacing scale for the app's
/// own windows, the shell sibling of the page reading-comfort
/// controls (#205) — for low-vision readers, wider line spacing is
/// the change that matters most and the one WPF exposes
/// (TextBlock.LineHeight). WPF has no letter-spacing API (that's a
/// WinUI/UWP surface), so the control is honestly line spacing only —
/// see DECISIONS.md. The shell applies LineHeight = font size ×
/// <see cref="BaseLineFactor"/> × scale; the factor math lives here
/// so it is pinned by tests.
/// </summary>
public static class ShellTextSpacing
{
    /// <summary>The untreated line height as a multiple of font size (WPF's single spacing is font-dependent; 1.25 is the working figure the shell has always effectively used).</summary>
    public const double BaseLineFactor = 1.25;

    /// <summary>The offered scales: Standard, Relaxed, Wide.</summary>
    public static readonly IReadOnlyList<double> Options = new[] { 1.0, 1.25, 1.5 };

    /// <summary>Clamp a stored value to the nearest offered scale (a hand-edited file degrades to Standard).</summary>
    public static double Sanitize(double value)
    {
        if (double.IsNaN(value) || double.IsInfinity(value))
            return 1.0;
        return Options.OrderBy(o => Math.Abs(o - value)).First();
    }

    /// <summary>The label key suffix for a scale, for the Settings combo.</summary>
    public static string LabelKey(double scale) => Sanitize(scale) switch
    {
        1.25 => "settings.text_spacing.relaxed",
        1.5 => "settings.text_spacing.wide",
        _ => "settings.text_spacing.standard",
    };

    /// <summary>The explicit line height for a font size at a scale; at Standard the shell leaves LineHeight unset (the font's own), so this returns null.</summary>
    public static double? LineHeight(double fontSize, double scale)
    {
        var clean = Sanitize(scale);
        if (clean <= 1.0 || fontSize <= 0)
            return null;
        return fontSize * BaseLineFactor * clean;
    }
}
