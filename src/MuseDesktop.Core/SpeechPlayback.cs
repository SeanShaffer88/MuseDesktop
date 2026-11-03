namespace MuseDesktop.Core;

// ─────────────────────── read-aloud playback control (M5) ───────────────────────
//
// The read-aloud state machine, lifted out of the shell's engine
// wrapper so the decisions can be tested without a synthesizer:
// which segment is current, whether playback is playing or
// paused, and what the rate is. The shell's engine owns the
// synthesizer and its threads; it drives this controller (Load,
// Start, Advance as each prompt completes, Pause/Resume/Stop) and
// reads the segment and rate back out. The read-aloud control
// bar binds to the same surface — State, CurrentIndex, Rate, and
// the events — instead of growing a second copy of the rules.
//
// Rate (M5): 0.5×–2× in quarter steps, clamped and snapped — a
// value the UI or a caller hands in can never land between stops.
// The shell maps the rate onto the synthesizer's scale through
// SpeechSpeed.ToSapiRate as each segment starts, exactly as
// before, so a change applies from the next segment.
//
// Events are raised synchronously on the caller's thread; the
// shell marshals to its dispatcher the way it already does for
// engine completions.

/// <summary>Where read-aloud playback stands.</summary>
public enum SpeechPlaybackState
{
    /// <summary>Nothing is loaded for playback, or playback has stopped or finished.</summary>
    Idle,

    /// <summary>Segments are being spoken, in order.</summary>
    Playing,

    /// <summary>Playback is held mid-queue; the current segment is the one that was speaking (or next to speak).</summary>
    Paused,
}

/// <summary>
/// The read-aloud playback controller (M5): a segment queue over
/// <see cref="SpeechText"/>'s segments with play/pause/stop and a
/// clamped quarter-step rate. Segment advancement is explicit —
/// the shell calls <see cref="Advance"/> when a prompt completes —
/// so pause and a rate change can land between segments instead of
/// waiting for one long prompt to finish.
/// </summary>
public sealed class SpeechPlaybackController
{
    /// <summary>The slowest rate: half speed.</summary>
    public const double MinRate = 0.5;

    /// <summary>The fastest rate: double speed.</summary>
    public const double MaxRate = 2.0;

    /// <summary>The rate grid: quarter steps between <see cref="MinRate"/> and <see cref="MaxRate"/>.</summary>
    public const double RateStep = 0.25;

    /// <summary>The playback state changed (play, pause, resume, stop, finish, load).</summary>
    public event Action? StateChanged;

    /// <summary>A segment became the current one (its index), at Start and at each Advance.</summary>
    public event Action<int>? SegmentStarted;

    /// <summary>The queue ran out: Start found nothing to play, or Advance stepped past the last segment. Never raised by Stop.</summary>
    public event Action? PlaybackFinished;

    /// <summary>The loaded segments, in speaking order; empty when nothing is loaded.</summary>
    public IReadOnlyList<SpeechSegment> Segments { get; private set; } = Array.Empty<SpeechSegment>();

    /// <summary>The current state.</summary>
    public SpeechPlaybackState State { get; private set; } = SpeechPlaybackState.Idle;

    /// <summary>The index of the segment currently being spoken (or held, while paused); −1 when nothing is current.</summary>
    public int CurrentIndex { get; private set; } = -1;

    /// <summary>The current segment, or null when none is current.</summary>
    public SpeechSegment? CurrentSegment =>
        CurrentIndex >= 0 && CurrentIndex < Segments.Count ? Segments[CurrentIndex] : null;

    /// <summary>The segment before the current one (the break after it is what the shell times the pause by); null at the head of the queue.</summary>
    public SpeechSegment? PreviousSegment =>
        CurrentIndex > 0 && CurrentIndex <= Segments.Count ? Segments[CurrentIndex - 1] : null;

    /// <summary>The playback rate: always on the quarter-step grid inside [<see cref="MinRate"/>, <see cref="MaxRate"/>].</summary>
    public double Rate { get; private set; } = SpeechSpeed.Default;

    /// <summary>True while a queue is loaded and playback is playing or paused.</summary>
    public bool IsActive => State != SpeechPlaybackState.Idle;

    /// <summary>
    /// Load a queue, replacing whatever was there, and reset to
    /// Idle with nothing current. The rate is kept — it is a
    /// listening preference, not a property of one answer.
    /// </summary>
    public void Load(IReadOnlyList<SpeechSegment>? segments)
    {
        Segments = segments is null ? Array.Empty<SpeechSegment>() : segments.ToArray();
        CurrentIndex = -1;
        State = SpeechPlaybackState.Idle;
        StateChanged?.Invoke();
    }

    /// <summary>
    /// Start playing from the head of the queue: returns the first
    /// segment and moves to Playing. On an empty queue there is
    /// nothing to play: the state stays Idle, PlaybackFinished is
    /// raised (the reading is over before it began — the shell's
    /// long-standing empty-answer outcome), and null is returned.
    /// Calling Start while already playing or paused restarts from
    /// the head.
    /// </summary>
    public SpeechSegment? Start()
    {
        if (Segments.Count == 0)
        {
            CurrentIndex = -1;
            State = SpeechPlaybackState.Idle;
            PlaybackFinished?.Invoke();
            return null;
        }
        CurrentIndex = 0;
        State = SpeechPlaybackState.Playing;
        StateChanged?.Invoke();
        SegmentStarted?.Invoke(CurrentIndex);
        return CurrentSegment;
    }

    /// <summary>
    /// The current segment finished: move to the next and return
    /// it. Stepping past the last segment ends playback — the
    /// state returns to Idle, PlaybackFinished is raised, and null
    /// is returned. Only meaningful while Playing: while paused or
    /// idle nothing advances and null is returned, so a late engine
    /// completion can never skip a segment the user is holding.
    /// </summary>
    public SpeechSegment? Advance()
    {
        if (State != SpeechPlaybackState.Playing)
            return null;
        if (CurrentIndex + 1 >= Segments.Count)
        {
            CurrentIndex = -1;
            State = SpeechPlaybackState.Idle;
            StateChanged?.Invoke();
            PlaybackFinished?.Invoke();
            return null;
        }
        CurrentIndex++;
        SegmentStarted?.Invoke(CurrentIndex);
        return CurrentSegment;
    }

    /// <summary>Hold playback. True when this call paused a playing queue; false otherwise.</summary>
    public bool Pause()
    {
        if (State != SpeechPlaybackState.Playing)
            return false;
        State = SpeechPlaybackState.Paused;
        StateChanged?.Invoke();
        return true;
    }

    /// <summary>Resume a held queue from its current segment. True when this call resumed; false otherwise.</summary>
    public bool Resume()
    {
        if (State != SpeechPlaybackState.Paused)
            return false;
        State = SpeechPlaybackState.Playing;
        StateChanged?.Invoke();
        return true;
    }

    /// <summary>
    /// Stop playback outright: back to Idle with nothing current.
    /// The queue stays loaded (a later Start replays it from the
    /// head); PlaybackFinished is NOT raised — stopping is the
    /// user's act, not the queue running out.
    /// </summary>
    public void Stop()
    {
        if (State == SpeechPlaybackState.Idle && CurrentIndex < 0)
            return;
        CurrentIndex = -1;
        State = SpeechPlaybackState.Idle;
        StateChanged?.Invoke();
    }

    /// <summary>
    /// Set the rate, clamped to [0.5×, 2×] and snapped to the
    /// quarter-step grid; a NaN rate means ordinary pace. Returns
    /// the rate now in force. It applies from the next segment —
    /// the shell reads it as each segment starts.
    /// </summary>
    public double SetRate(double rate)
    {
        Rate = ClampRate(rate);
        return Rate;
    }

    /// <summary>Clamp and snap any rate onto the grid: [0.5, 2.0] in 0.25 steps; NaN and infinities land on ordinary pace / the nearer end.</summary>
    public static double ClampRate(double rate)
    {
        if (double.IsNaN(rate))
            return SpeechSpeed.Default;
        var clamped = Math.Clamp(rate, MinRate, MaxRate);
        var steps = Math.Round(clamped / RateStep, MidpointRounding.AwayFromZero);
        return Math.Clamp(steps * RateStep, MinRate, MaxRate);
    }
}
