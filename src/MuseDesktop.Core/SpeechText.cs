using System.Globalization;
using System.Text.RegularExpressions;

namespace MuseDesktop.Core;

// ─────────────────────── voice in, voice out (v2.1.0) ───────────────────────
//
// The app's own voice paths, alongside quick dictate's hand-off to
// Muse's voice button (which is untouched — see DECISIONS,
// 2026-11-02). The shell owns the engines: Windows speech
// recognition writes dictated words into the composer's box through
// the ordinary snippet insert path, and Windows speech synthesis
// reads the last answer aloud from segments this file prepares.
//
// This file owns everything that can be judged without a
// microphone or a speaker: how an answer's Markdown becomes
// speakable segments (headings stay marked, so the shell can pause
// a beat longer after them; code blocks are announced, not read),
// which message in an extraction is "the last answer", and how the
// five reading speeds map onto the synthesizer's rate scale.
//
// Privacy shape (see DECISIONS, 2026-11-02): both engines are
// Windows' own and run on this PC — dictation audio never leaves
// the machine and is never recorded, and read-aloud text is pulled
// from the page only at the user's click through the same channel
// export uses, then spoken and forgotten. Nothing here logs, and
// the shell logs nothing of either text.

/// <summary>One speakable piece of an answer (v2.1.0): Markdown-free text, with headings flagged so playback can pause a beat longer after them.</summary>
public sealed record SpeechSegment(string Text, bool IsHeading);

/// <summary>
/// Turning an answer into speech (v2.1.0): the segmentation rules
/// for read-aloud, and the "last answer" pick from a conversation
/// extraction.
/// </summary>
public static class SpeechText
{
    /// <summary>
    /// The most segments one answer becomes. Read-aloud is for
    /// listening to an answer, not for audiobook-length sessions:
    /// 300 segments is far past any real answer, and anything
    /// beyond it is simply not queued.
    /// </summary>
    public const int MaxSegments = 300;

    /// <summary>
    /// The longest single segment, in characters. A synthesizer
    /// prompt is one breath of the reading: longer paragraphs are
    /// split at sentence ends (then at word boundaries) so pause,
    /// resume, and a speed change can land between pieces instead
    /// of waiting minutes for one prompt to finish.
    /// </summary>
    public const int MaxSegmentChars = 500;

    /// <summary>
    /// The last assistant message's text from a conversation
    /// extraction (the payload Core's
    /// <see cref="ConversationExport.TryParseExtraction"/> returns),
    /// or null when the extraction holds no assistant message —
    /// "the last answer" is the newest thing Muse said, and a
    /// trailing user message doesn't replace it.
    /// </summary>
    public static string? LastAssistantAnswer(IReadOnlyList<ExportedMessage>? messages)
    {
        if (messages is null)
            return null;
        for (var i = messages.Count - 1; i >= 0; i--)
        {
            if (string.Equals(messages[i].Role, "assistant", StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(messages[i].Text))
                return messages[i].Text;
        }
        return null;
    }

    /// <summary>
    /// Split an answer's Markdown into speakable segments:
    /// headings become heading segments; paragraphs become plain
    /// segments; each fenced code block becomes ONE marker segment
    /// carrying <paramref name="codeBlockMarker"/> (the localized
    /// spoken marker the shell passes in, e.g. "Code block.") —
    /// the code itself is not read, because source code spoken
    /// symbol by symbol helps no one, and pretending it wasn't
    /// there would misrepresent the answer. Inline Markdown is
    /// unwrapped: links keep their words and drop their addresses,
    /// images keep their alt text, emphasis and code-span markers
    /// fall away. Null, empty, or whitespace input — or an answer
    /// with nothing speakable in it — yields no segments, which
    /// the shell reports as "nothing to read".
    /// </summary>
    public static IReadOnlyList<SpeechSegment> Segment(string? markdown, string codeBlockMarker)
    {
        var segments = new List<SpeechSegment>();
        if (string.IsNullOrWhiteSpace(markdown))
            return segments;
        var marker = codeBlockMarker?.Trim() ?? "";
        var lines = markdown.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var paragraph = new List<string>();
        var inFence = false;
        var fenceCharacter = '\0';
        var fenceLength = 0;

        void FlushParagraph()
        {
            if (paragraph.Count == 0)
                return;
            var text = CleanInline(string.Join(" ", paragraph));
            paragraph.Clear();
            AddChunked(segments, text, isHeading: false);
        }

        void EmitMarker()
        {
            FlushParagraph();
            if (marker.Length > 0 && segments.Count < MaxSegments)
                segments.Add(new SpeechSegment(marker, IsHeading: false));
        }

        foreach (var rawLine in lines)
        {
            if (segments.Count >= MaxSegments) break;
            var line = rawLine.Trim();
            if (IsFenceLine(line))
            {
                // Opening or closing a fence: either way the block
                // boundary is one spoken marker — on the close, and
                // for an unterminated block, at the end of input.
                if (inFence)
                {
                    var length = line.TakeWhile(c => c == fenceCharacter).Count();
                    if (line[0] == fenceCharacter && length >= fenceLength &&
                        string.IsNullOrWhiteSpace(line[length..]))
                    { inFence = false; EmitMarker(); }
                }
                else
                {
                    FlushParagraph();
                    inFence = true;
                    fenceCharacter = line[0];
                    fenceLength = line.TakeWhile(c => c == fenceCharacter).Count();
                }
                continue;
            }
            if (inFence)
                continue;   // code contents are announced, never read
            if (line.Length == 0)
            {
                FlushParagraph();
                continue;
            }
            var heading = HeadingText(line);
            if (heading is not null)
            {
                FlushParagraph();
                AddChunked(segments, CleanInline(heading), isHeading: true);
                continue;
            }
            if (IsTableSeparatorRow(line) || IsHorizontalRule(line))
            {
                FlushParagraph();
                continue;
            }
            paragraph.Add(StripBlockPrefix(line));
        }
        FlushParagraph();
        if (inFence)
            EmitMarker();   // the answer ended mid-block; it was still a code block
        return segments;
    }

    /// <summary>A fence line opens or closes a code block: three or more backticks or tildes at the line's start (an info string like ```csharp rides along, unread).</summary>
    private static bool IsFenceLine(string trimmedLine) =>
        trimmedLine.StartsWith("```", StringComparison.Ordinal)
        || trimmedLine.StartsWith("~~~", StringComparison.Ordinal);

    /// <summary>The heading's text when the line is an ATX heading (# … ###### followed by a space), else null. A trailing run of #s (the closed ATX form) is dropped.</summary>
    private static string? HeadingText(string trimmedLine)
    {
        if (!trimmedLine.StartsWith('#'))
            return null;
        var level = 0;
        while (level < trimmedLine.Length && trimmedLine[level] == '#')
            level++;
        if (level > 6 || level >= trimmedLine.Length || trimmedLine[level] != ' ')
            return null;
        var text = trimmedLine[level..].Trim();
        if (text.EndsWith('#'))
            text = text.TrimEnd('#').TrimEnd();
        return text.Length > 0 ? text : null;
    }

    /// <summary>A table's separator row (|---|---| and kin): only pipes, dashes, colons, and spaces — punctuation with nothing to say.</summary>
    private static bool IsTableSeparatorRow(string trimmedLine) =>
        trimmedLine.Contains('-')
        && trimmedLine.All(c => c is '|' or '-' or ':' or ' ');

    /// <summary>A horizontal rule (---, ***, ___): a visual pause the paragraph break already gives the ear.</summary>
    private static bool IsHorizontalRule(string trimmedLine) =>
        trimmedLine.Length >= 3
        && (trimmedLine.All(c => c == '-') || trimmedLine.All(c => c == '*') || trimmedLine.All(c => c == '_'));

    /// <summary>Strip the block furniture from one prose line: blockquote markers, list bullets and numbers, and table pipes (cells stay, separated by the space the pipe leaves behind).</summary>
    private static string StripBlockPrefix(string line)
    {
        var text = line;
        while (text.StartsWith('>'))
            text = text[1..].TrimStart();
        text = Regex.Replace(text, @"^[-*+]\s+", "");
        text = Regex.Replace(text, @"^\d{1,9}[.)]\s+", "");
        return text.Replace('|', ' ');
    }

    /// <summary>
    /// Unwrap inline Markdown for the ear: images keep their alt
    /// text, links keep their words (addresses are unspeakable and
    /// unhelpful aloud), HTML tags fall away, emphasis and
    /// code-span markers drop, and whitespace collapses to single
    /// spaces. Single underscores survive — they live inside words
    /// (file names, identifiers) where dropping them would garble
    /// more than it cleans.
    /// </summary>
    private static string CleanInline(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return "";
        var cleaned = Regex.Replace(text, @"!\[([^\]]*)\]\([^)]*\)", "$1");
        cleaned = Regex.Replace(cleaned, @"\[([^\]]+)\]\([^)]*\)", "$1");
        cleaned = Regex.Replace(cleaned, @"\[([^\]]+)\]\[[^\]]*\]", "$1");
        cleaned = Regex.Replace(cleaned, @"<[^>]+>", " ");
        cleaned = cleaned.Replace("**", "").Replace("__", "")
            .Replace("~~", "").Replace("*", "").Replace("`", "");
        return string.Join(" ", cleaned.Split((char[]?)null,
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
    }

    /// <summary>Add a block of cleaned prose as one or more segments, splitting at sentence ends (then word boundaries, then hard) when it outgrows <see cref="MaxSegmentChars"/>, and stopping at <see cref="MaxSegments"/>. Only the first piece of a heading keeps the heading flag.</summary>
    private static void AddChunked(List<SpeechSegment> segments, string text, bool isHeading)
    {
        var rest = text;
        var first = true;
        while (rest.Length > 0 && segments.Count < MaxSegments)
        {
            if (rest.Length <= MaxSegmentChars)
            {
                segments.Add(new SpeechSegment(rest, isHeading && first));
                return;
            }
            var cut = SentenceCut(rest, MaxSegmentChars);
            if (cut <= 0)
                cut = WordCut(rest, MaxSegmentChars);
            if (cut <= 0)
                cut = MaxSegmentChars;
            segments.Add(new SpeechSegment(rest[..cut].TrimEnd(), isHeading && first));
            rest = rest[cut..].TrimStart();
            first = false;
        }
    }

    /// <summary>The end of the last whole sentence that fits in <paramref name="limit"/> characters (a ., !, or ? followed by a space or the end), or -1 when no sentence ends there.</summary>
    private static int SentenceCut(string text, int limit)
    {
        var best = -1;
        for (var i = 0; i < limit && i < text.Length; i++)
        {
            if (text[i] is '.' or '!' or '?'
                && (i + 1 >= text.Length || text[i + 1] == ' '))
                best = i + 1;
        }
        return best;
    }

    /// <summary>The last word boundary at or before <paramref name="limit"/>, or -1 when the first word alone overflows it.</summary>
    private static int WordCut(string text, int limit)
    {
        if (limit >= text.Length)
            return text.Length;
        var space = text.LastIndexOf(' ', limit);
        return space > 0 ? space : -1;
    }
}

/// <summary>
/// The reading speeds (v2.1.0): five stops — 0.75×, 1×, 1.25×,
/// 1.5×, 2× — and their translation to the synthesizer's rate
/// scale. That scale (SAPI's, −10…+10) is logarithmic, running
/// roughly ⅓× at −10 to 3× at +10, so the map is a logarithm on
/// the same span: 1× lands at 0 and every stop stays in order.
/// The shell cycles the stops and sets the mapped rate as each
/// segment starts, so a change applies from the next segment.
/// </summary>
public static class SpeechSpeed
{
    /// <summary>The five speeds, slowest first. The middle one is ordinary reading pace.</summary>
    public static IReadOnlyList<double> All { get; } = new[] { 0.75, 1.0, 1.25, 1.5, 2.0 };

    /// <summary>Where reading starts: ordinary pace.</summary>
    public const double Default = 1.0;

    /// <summary>The synthesizer rate (−10…+10) for a speed multiplier, on SAPI's logarithmic ⅓×…3× span.</summary>
    public static int ToSapiRate(double speed)
    {
        if (speed <= 0)
            return -10;
        var rate = (int)Math.Round(
            10.0 * Math.Log(speed) / Math.Log(3.0), MidpointRounding.AwayFromZero);
        return Math.Clamp(rate, -10, 10);
    }

    /// <summary>The next stop after <paramref name="current"/> (wrapping from the fastest back to the slowest); a value between stops moves up to the next one.</summary>
    public static double CycleNext(double current)
    {
        foreach (var speed in All)
        {
            if (speed > current)
                return speed;
        }
        return All[0];
    }

    /// <summary>The speed button's label: the multiplier in the reader's own number format, e.g. "1.25×" — a number, not prose, so it is the same shape in every language.</summary>
    public static string Format(double speed, CultureInfo culture) =>
        string.Create(culture, $"{speed:0.##}×");
}
