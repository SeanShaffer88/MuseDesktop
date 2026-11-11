using System.Globalization;

namespace MuseDesktop.Core;

/// <summary>Accent-insensitive discovery, never URL identity or profile matching.</summary>
public static class SearchText
{
    public static bool Contains(string? text, string term) =>
        text is not null && CultureInfo.InvariantCulture.CompareInfo.IndexOf(
            text, term, CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace) >= 0;
}
