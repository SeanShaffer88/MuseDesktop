namespace MuseDesktop.Core;

// ─────────────────────── follow-up chips (v2.1.0) ───────────────────────
//
// After a long answer, one quiet bar of follow-ups. The six chips
// are fixed data — the same six in every language — and this file
// owns their ids, their order, and the "long answer" rule. The
// words themselves live in the string catalog (each chip names its
// label key and its prompt key); the shell resolves them through
// Strings in the active language, hands the labels to the page
// script, and — when a click comes back over the bridge carrying
// only a chip id — resolves that id here and inserts the prompt
// text into the composer, unsent.
//
// Privacy shape (see DECISIONS, 2026-11-02): the answer is measured
// ON the page (a word count, kept there); the only thing that ever
// crosses the bridge is the clicked chip's id — a word from this
// fixed set, never any part of the answer.

/// <summary>One follow-up chip (v2.1.0): its stable id (the only thing the bridge ever carries), and the catalog keys for its button label and for the prompt text a click drops into the composer.</summary>
public sealed record FollowUpChip(string Id, string LabelKey, string PromptKey);

/// <summary>
/// The follow-up chips (v2.1.0): six one-click continuations offered
/// once under a finished long answer — Summarize, Give an example,
/// Turn into steps, Make a table, Pros and cons, Explain simpler.
/// </summary>
public static class FollowUpChips
{
    /// <summary>An assistant answer at or above this many words counts as long — the bar is offered only for those.</summary>
    public const int LongAnswerWordThreshold = 150;

    /// <summary>The six chips, in the order the bar shows them.</summary>
    public static IReadOnlyList<FollowUpChip> All { get; } = new[]
    {
        new FollowUpChip("summarize", "chip.summarize", "chip.summarize_prompt"),
        new FollowUpChip("example", "chip.example", "chip.example_prompt"),
        new FollowUpChip("steps", "chip.steps", "chip.steps_prompt"),
        new FollowUpChip("table", "chip.table", "chip.table_prompt"),
        new FollowUpChip("pros-cons", "chip.pros_cons", "chip.pros_cons_prompt"),
        new FollowUpChip("simpler", "chip.simpler", "chip.simpler_prompt"),
    };

    /// <summary>The chip with this id, or null — ids match exactly (they are a fixed vocabulary, not user text).</summary>
    public static FollowUpChip? Find(string? id) =>
        id is null ? null : All.FirstOrDefault(chip => chip.Id == id);

    /// <summary>True when <paramref name="id"/> is one of the six chip ids — the bridge's whole validation for a chip click.</summary>
    public static bool IsKnownId(string? id) => Find(id) is not null;

    /// <summary>
    /// Offer the bar for an answer of <paramref name="answerWordCount"/>
    /// words? Only when the answer is long (at or over
    /// <see cref="LongAnswerWordThreshold"/>) and this answer hasn't
    /// had its offer already — one bar per answer, whether it was
    /// used or simply left behind.
    /// </summary>
    public static bool ShouldOffer(int answerWordCount, bool alreadyOfferedForThisAnswer) =>
        !alreadyOfferedForThisAnswer && answerWordCount >= LongAnswerWordThreshold;
}
