namespace MuseDesktop.Core;

// ─────────────────────── snippet templates: fill-in blanks ───────────────────────
//
// A prompt snippet's body (PromptSnippets, ConversationTools.cs) may
// contain blanks written {{name}} — e.g. "Explain {{topic}} to a
// {{audience}}". When the user inserts such a snippet, the shell asks
// for each blank first and inserts the finished text; a snippet
// without blanks inserts exactly as it always has. This file owns
// the syntax and the fill — pure text logic, pinned by tests. Snippet
// bodies and the values typed for them are the user's own words: they
// go to the composer only, never to any log.

/// <summary>
/// The <c>{{blank}}</c> template layer over prompt snippets: which
/// blanks a body has, and filling them in. A blank is
/// <c>{{ name }}</c> — optional spaces inside the braces; the name is
/// non-empty once trimmed, at most 40 characters, and contains no
/// braces or newlines. Malformed candidates (<c>{{}}</c>,
/// <c>{{ }}</c>, an unclosed <c>{{</c>) are plain text, not blanks.
/// The same name twice (compared without case) is ONE blank, asked
/// once and filled in everywhere it appears.
/// </summary>
public static class SnippetTemplate
{
    /// <summary>The longest blank name that still counts as a blank.</summary>
    public const int MaxFieldNameLength = 40;

    /// <summary>The most blanks one snippet can ask for; further distinct names are plain text.</summary>
    public const int MaxFields = 20;

    /// <summary>The body's blank names, unique without case, in first-appearance order (first-seen spelling kept). Empty for a null, empty, or blank-free body.</summary>
    public static IReadOnlyList<string> Fields(string? body)
    {
        var fields = new List<string>();
        if (string.IsNullOrEmpty(body))
            return fields;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (_, _, name) in Scan(body))
        {
            if (seen.Add(name))
            {
                fields.Add(name);
                if (fields.Count >= MaxFields)
                    break;
            }
        }
        return fields;
    }

    /// <summary>True when the body has at least one fill-in blank.</summary>
    public static bool HasFields(string? body) => Fields(body).Count > 0;

    /// <summary>
    /// Fill the body's blanks from <paramref name="values"/> (keys
    /// matched to blank names without case). Every value is trimmed;
    /// a blank with no value, or a value that trims to nothing,
    /// counts as missing. When anything is missing, returns false,
    /// <paramref name="missing"/> lists the missing blank names
    /// (first-seen spelling, in blank order) and
    /// <paramref name="filled"/> is the original body, untouched.
    /// Text that is not a blank is never altered; blanks past the
    /// field cap are not fields and stay as written.
    /// </summary>
    public static bool TryFill(
        string? body,
        IReadOnlyDictionary<string, string> values,
        out string filled,
        out IReadOnlyList<string> missing)
    {
        var text = body ?? "";
        var fields = Fields(text);
        missing = Array.Empty<string>();
        if (fields.Count == 0)
        {
            filled = text;
            return true;
        }

        var resolved = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var missingList = new List<string>();
        foreach (var field in fields)
        {
            var value = Lookup(values, field)?.Trim();
            if (string.IsNullOrEmpty(value))
                missingList.Add(field);
            else
                resolved[field] = value;
        }
        if (missingList.Count > 0)
        {
            filled = text;
            missing = missingList;
            return false;
        }

        var builder = new System.Text.StringBuilder(text.Length);
        var cursor = 0;
        foreach (var (start, length, name) in Scan(text))
        {
            if (!resolved.TryGetValue(name, out var value))
                continue; // past the field cap: not a field, left as written
            builder.Append(text, cursor, start - cursor);
            builder.Append(value);
            cursor = start + length;
        }
        builder.Append(text, cursor, text.Length - cursor);
        filled = builder.ToString();
        return true;
    }

    private static string? Lookup(IReadOnlyDictionary<string, string> values, string field)
    {
        if (values is null)
            return null;
        if (values.TryGetValue(field, out var exact))
            return exact;
        foreach (var (key, value) in values)
            if (string.Equals(key, field, StringComparison.OrdinalIgnoreCase))
                return value;
        return null;
    }

    /// <summary>
    /// Every well-formed blank in the body, in order: its start, its
    /// full length (braces included), and its trimmed name. Malformed
    /// candidates are skipped — scanning resumes just past their
    /// opening braces, so a well-formed blank nested after one is
    /// still found.
    /// </summary>
    private static IEnumerable<(int Start, int Length, string Name)> Scan(string body)
    {
        var i = 0;
        while (i < body.Length - 1)
        {
            var open = body.IndexOf("{{", i, StringComparison.Ordinal);
            if (open < 0)
                yield break;
            var close = body.IndexOf("}}", open + 2, StringComparison.Ordinal);
            if (close < 0)
                yield break; // unclosed — no later blank can complete either
            var name = body.Substring(open + 2, close - open - 2).Trim();
            if (IsValidName(name))
            {
                yield return (open, close + 2 - open, name);
                i = close + 2;
            }
            else
            {
                i = open + 2;
            }
        }
    }

    private static bool IsValidName(string name) =>
        name.Length is > 0 and <= MaxFieldNameLength
        && !name.Contains('{') && !name.Contains('}')
        && !name.Contains('\n') && !name.Contains('\r');
}
