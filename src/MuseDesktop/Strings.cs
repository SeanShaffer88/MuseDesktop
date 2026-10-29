using System.Globalization;
using System.Windows.Markup;
using MuseDesktop.Core;

namespace MuseDesktop;

/// <summary>
/// The shell's one string accessor (R7 #161): every user-facing
/// string in the app's own windows resolves through here (or through
/// the {local:Loc} markup extension in XAML) against the Core string
/// catalog — the active language first, English for anything a
/// translation hasn't covered yet (the fallback is counted by the
/// coverage guard, #164, and shown in Diagnostics).
///
/// Initialized once at startup from settings + the Windows display
/// language + any imported language packs. A language change in
/// Settings applies to windows opened afterwards; windows already
/// open keep the language they were built with until the next launch
/// (the Settings note says exactly that — see DECISIONS, R7).
/// </summary>
public static class Strings
{
    private static Localizer? _localizer;
    private static IReadOnlyDictionary<string, LanguagePack> _packs =
        new Dictionary<string, LanguagePack>();

    /// <summary>The active localizer; English until Initialize runs (design-time and very-early-startup safe).</summary>
    public static Localizer Current =>
        _localizer ??= StringCatalog.CreateLocalizer(null, LanguageCatalog.SourceCode);

    /// <summary>File names of pack files in the packs folder that failed validation at the last Initialize (names only — Diagnostics shows them so a broken pack is explainable, #165).</summary>
    public static IReadOnlyList<string> SkippedPackFileNames { get; private set; } = Array.Empty<string>();

    /// <summary>
    /// (Re)build the localizer from settings: the stored language
    /// preference (null = follow Windows), the OS UI culture, and the
    /// validated packs in the language-packs folder.
    /// </summary>
    public static void Initialize(AppSettings settings)
    {
        _packs = LanguagePackFile.LoadFolder(
            AppPaths.LanguagePacksDir, StringCatalog.Source, out var skipped);
        SkippedPackFileNames = skipped;
        var system = CultureInfo.CurrentUICulture.Name;
        _localizer = StringCatalog.CreateLocalizer(settings.UiLanguage, system, _packs);
        ShellLineSpacing.ScaleFactor = settings.ShellTextSpacingScale;
    }

    /// <summary>The imported packs currently overlaid on the built-in tables, by language code.</summary>
    public static IReadOnlyDictionary<string, LanguagePack> Packs => _packs;

    /// <summary>The merged tables (built-ins + packs) — the coverage report's input.</summary>
    public static IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> MergedTables() =>
        StringCatalog.Merge(_packs);

    /// <summary>The active language's display name: an imported pack's own name wins, then the catalog's.</summary>
    public static string ActiveLanguageName =>
        _packs.TryGetValue(Current.Language, out var pack)
            ? pack.DisplayName
            : LanguageCatalog.Describe(Current.Language).EnglishName;

    /// <summary>Resolve a catalog key in the active language (English fallback, then the key itself).</summary>
    public static string Get(string key) => Current.Get(key);

    /// <summary>Resolve and format a catalog key with the active language's culture.</summary>
    public static string Format(string key, params object?[] args) => Current.Format(key, args);
}

/// <summary>
/// The XAML face of <see cref="Strings"/> (R7 #161):
/// <c>Content="{local:Loc some_key}"</c>. Resolves once, when the
/// window is built — which is why a language change reaches windows
/// opened afterwards immediately and everything at the next launch.
/// </summary>
public sealed class LocExtension : MarkupExtension
{
    public LocExtension() { }

    public LocExtension(string key) => Key = key;

    /// <summary>The catalog key (the markup extension's positional argument).</summary>
    public string Key { get; set; } = "";

    // The XAML compiler probes extensions with an unset key while it
    // works out property types — ProvideValue must never throw.
    public override object ProvideValue(IServiceProvider serviceProvider) =>
        string.IsNullOrEmpty(Key) ? "" : Strings.Get(Key);

}
