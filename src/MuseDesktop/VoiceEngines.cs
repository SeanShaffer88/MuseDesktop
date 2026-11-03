using System.Globalization;
using System.Speech.Recognition;
using System.Speech.Synthesis;
using System.Windows.Threading;
using MuseDesktop.Core;

namespace MuseDesktop;

// ─────────────────────── voice in, voice out (v2.1.0) ───────────────────────
//
// The shell halves of the app's own voice paths. Both wrap
// Windows' own speech engines (System.Speech): recognition for
// dictation, synthesis for read-aloud. The engines run on this
// PC — dictation audio never leaves the machine and is never
// recorded; recognized text and answer text cross to the window
// only through the events below, and neither is ever logged
// (these classes hold no reference to the log at all, on purpose).
//
// Core owns the decisions that can be tested without hardware
// (SpeechText's segmentation, SpeechSpeed's rate map); these
// classes own the engines and their lifecycles, and MainWindow
// owns the presentation (status lines, the menu items, the
// status-bar reading controls).

/// <summary>How a dictation start ended; each outcome maps to exactly one status line in the window.</summary>
public enum DictationStartOutcome
{
    /// <summary>Listening, with a recognizer for the app's own language.</summary>
    Started,

    /// <summary>Listening, but with another installed recognizer — none exists for the app's language.</summary>
    StartedWithFallbackRecognizer,

    /// <summary>No speech recognizer is installed on this PC at all.</summary>
    NoRecognizerInstalled,

    /// <summary>A recognizer exists but no working microphone was found to feed it.</summary>
    NoMicrophone,

    /// <summary>The engine itself refused to start.</summary>
    Failed,
}

/// <summary>A dictation start's result: the outcome, plus — when listening started on a fallback — the recognizer actually in use, named by its culture for the status line.</summary>
public sealed record DictationStartResult(DictationStartOutcome Outcome, string? RecognizerCultureName);

/// <summary>
/// One dictation session (v2.1.0): an in-process Windows
/// recognizer with a dictation grammar, feeding its FINAL results
/// to the window through <see cref="Recognized"/> — interim
/// hypotheses never surface. Events arrive on engine threads; the
/// subscriber (the window) marshals to its dispatcher. Starting is
/// honest about the machine it finds: no recognizer, no
/// recognizer for the app's language (a fallback starts instead,
/// and says so), or no microphone are distinct outcomes, never
/// exceptions dressed as success.
/// </summary>
public sealed class DictationEngine : IDisposable
{
    private SpeechRecognitionEngine? _engine;
    private bool _stopping;
    private int _silentRestarts;

    /// <summary>A final recognition result (the recognized text, trimmed). Raised on an engine thread.</summary>
    public event Action<string>? Recognized;

    /// <summary>Recognition ended without the user asking (and a quiet restart didn't take). Raised on an engine thread.</summary>
    public event Action? Ended;

    public bool IsListening => _engine is not null;

    /// <summary>
    /// Start listening for <paramref name="desiredCulture"/> (the
    /// app's active language): an installed recognizer for that
    /// culture wins, then one for the same language, then — when
    /// neither exists — the OS default recognizer, reported as a
    /// fallback so the status can say which language will actually
    /// hear the user. Never throws.
    /// </summary>
    public DictationStartResult Start(CultureInfo desiredCulture)
    {
        Stop();
        IReadOnlyList<RecognizerInfo> installed;
        try { installed = SpeechRecognitionEngine.InstalledRecognizers(); }
        catch (Exception) { return new DictationStartResult(DictationStartOutcome.Failed, null); }
        if (installed.Count == 0)
            return new DictationStartResult(DictationStartOutcome.NoRecognizerInstalled, null);

        var match = installed.FirstOrDefault(info => info.Culture.Equals(desiredCulture))
            ?? installed.FirstOrDefault(info =>
                info.Culture.TwoLetterISOLanguageName == desiredCulture.TwoLetterISOLanguageName);
        SpeechRecognitionEngine engine;
        try
        {
            engine = match is not null
                ? new SpeechRecognitionEngine(match)
                : new SpeechRecognitionEngine();
        }
        catch (Exception) { return new DictationStartResult(DictationStartOutcome.Failed, null); }

        try
        {
            engine.LoadGrammar(new DictationGrammar());
            engine.SetInputToDefaultAudioDevice();
        }
        catch (InvalidOperationException)
        {
            // The recognizer exists but has no audio input to hear:
            // the microphone is missing, disabled, or taken.
            engine.Dispose();
            return new DictationStartResult(DictationStartOutcome.NoMicrophone, null);
        }
        catch (Exception)
        {
            engine.Dispose();
            return new DictationStartResult(DictationStartOutcome.Failed, null);
        }

        _stopping = false;
        _engine = engine;
        engine.SpeechRecognized += OnSpeechRecognized;
        engine.RecognizeCompleted += OnRecognizeCompleted;
        try { engine.RecognizeAsync(RecognizeMode.Multiple); }
        catch (Exception)
        {
            Stop();
            return new DictationStartResult(DictationStartOutcome.Failed, null);
        }
        return match is not null
            ? new DictationStartResult(DictationStartOutcome.Started, null)
            : new DictationStartResult(DictationStartOutcome.StartedWithFallbackRecognizer,
                engine.RecognizerInfo.Culture.DisplayName);
    }

    private void OnSpeechRecognized(object? sender, SpeechRecognizedEventArgs e)
    {
        _silentRestarts = 0;
        var text = e.Result?.Text?.Trim();
        if (!string.IsNullOrEmpty(text))
            Recognized?.Invoke(text);
    }

    private void OnRecognizeCompleted(object? sender, RecognizeCompletedEventArgs e)
    {
        if (_stopping || _engine is null)
            return;
        // A long thoughtful pause before (or between) speaking
        // can trip the recognizer's silence timeouts and end the
        // run on its own: the completion then says so
        // (InitialSilenceTimeout / BabbleTimeout) and carries no
        // error. Dictation is the user's to end, so such a run
        // simply starts again (bounded, and reset by every word
        // actually heard — a recognizer completing instantly
        // forever is a fault, not silence). Anything else — the
        // input stream ending, an error — ends it honestly.
        if (e.Error is null && !e.Cancelled
            && (e.InitialSilenceTimeout || e.BabbleTimeout)
            && _silentRestarts < 1000)
        {
            _silentRestarts++;
            try
            {
                _engine.RecognizeAsync(RecognizeMode.Multiple);
                return;
            }
            catch (Exception) { /* fall through to the honest end */ }
        }
        var engine = _engine;
        _engine = null;
        try { engine.Dispose(); } catch (Exception) { }
        Ended?.Invoke();
    }

    /// <summary>Stop listening and release the engine. Safe to call when not listening; never throws.</summary>
    public void Stop()
    {
        var engine = _engine;
        _engine = null;
        if (engine is null)
            return;
        _stopping = true;
        try
        {
            engine.SpeechRecognized -= OnSpeechRecognized;
            engine.RecognizeCompleted -= OnRecognizeCompleted;
            engine.RecognizeAsyncCancel();
        }
        catch (Exception) { }
        try { engine.Dispose(); } catch (Exception) { }
    }

    public void Dispose() => Stop();
}

/// <summary>
/// One read-aloud session (v2.1.0): a Windows synthesizer speaking
/// Core's segments one prompt at a time. One at a time is what
/// makes the controls honest — Pause lands immediately, and a
/// speed change applies from the NEXT segment because the rate is
/// set as each segment starts. The pause between segments rides
/// inside the next segment's prompt as a break: ~350 ms after a
/// heading segment, ~150 ms between paragraphs. All public events
/// are raised on the dispatcher handed to
/// <see cref="Start(IReadOnlyList{SpeechSegment}, CultureInfo, Dispatcher)"/>.
/// </summary>
public sealed class ReadAloudEngine : IDisposable
{
    private static readonly TimeSpan AfterHeadingBreak = TimeSpan.FromMilliseconds(350);
    private static readonly TimeSpan BetweenParagraphsBreak = TimeSpan.FromMilliseconds(150);

    private SpeechSynthesizer? _synth;
    private Dispatcher? _dispatcher;
    private IReadOnlyList<SpeechSegment> _segments = Array.Empty<SpeechSegment>();
    private int _index;
    private int _session;
    private Prompt? _currentPrompt;
    private SpeechSegment? _previous;

    /// <summary>The last segment finished: the reading completed on its own.</summary>
    public event Action? Finished;

    /// <summary>The synthesizer reported an error mid-reading; the session is over.</summary>
    public event Action? Failed;

    public bool IsActive => _synth is not null;
    public bool IsPaused { get; private set; }

    /// <summary>The current speed stop (Core's table); a change applies from the next segment.</summary>
    public double Speed { get; private set; } = SpeechSpeed.Default;

    /// <summary>
    /// Begin reading <paramref name="segments"/> (Core's
    /// <see cref="SpeechText.Segment"/> output) with the installed
    /// voice nearest <paramref name="voiceCulture"/> — the OS
    /// default voice for the app's language when one is installed,
    /// else the synthesizer's own default. Any previous session is
    /// stopped first.
    /// </summary>
    public void Start(IReadOnlyList<SpeechSegment> segments, CultureInfo voiceCulture, Dispatcher dispatcher)
    {
        Stop();
        _dispatcher = dispatcher;
        _segments = segments;
        _index = 0;
        _previous = null;
        IsPaused = false;
        Speed = SpeechSpeed.Default;
        var session = ++_session;
        if (segments.Count == 0)
        {
            dispatcher.BeginInvoke(() => { if (session == _session) Finished?.Invoke(); });
            return;
        }
        var synth = new SpeechSynthesizer();
        _synth = synth;
        try
        {
            var voices = synth.GetInstalledVoices()
                .Where(voice => voice.Enabled)
                .ToList();
            var match = voices.FirstOrDefault(voice => voice.VoiceInfo.Culture.Equals(voiceCulture))
                ?? voices.FirstOrDefault(voice =>
                    voice.VoiceInfo.Culture.TwoLetterISOLanguageName
                        == voiceCulture.TwoLetterISOLanguageName);
            if (match is not null)
                synth.SelectVoice(match.VoiceInfo.Name);
        }
        catch (Exception)
        {
            // No selectable voice for the language — the
            // synthesizer's default voice reads instead.
        }
        synth.SpeakCompleted += OnSpeakCompleted;
        SpeakNext(session);
    }

    /// <summary>Pause or resume. Returns the paused state now in force (false when nothing is reading).</summary>
    public bool TogglePause()
    {
        var synth = _synth;
        if (synth is null)
            return false;
        try
        {
            if (IsPaused)
            {
                synth.Resume();
                IsPaused = false;
            }
            else if (synth.State == SynthesizerState.Speaking)
            {
                synth.Pause();
                IsPaused = true;
            }
        }
        catch (Exception) { }
        return IsPaused;
    }

    /// <summary>Move to the next speed stop (wrapping at the top) and return it; it takes effect from the next segment.</summary>
    public double CycleSpeed() => Speed = SpeechSpeed.CycleNext(Speed);

    /// <summary>Stop reading and release the synthesizer, silently — no Finished, no Failed. Safe when idle; never throws.</summary>
    public void Stop()
    {
        _session++;
        _currentPrompt = null;
        IsPaused = false;
        var synth = _synth;
        _synth = null;
        if (synth is null)
            return;
        try
        {
            synth.SpeakCompleted -= OnSpeakCompleted;
            synth.SpeakAsyncCancelAll();
        }
        catch (Exception) { }
        try { synth.Dispose(); } catch (Exception) { }
    }

    private void SpeakNext(int session)
    {
        var synth = _synth;
        if (synth is null || session != _session)
            return;
        if (_index >= _segments.Count)
        {
            Stop();
            Finished?.Invoke();
            return;
        }
        var segment = _segments[_index++];
        var builder = new PromptBuilder();
        if (_previous is not null)
            builder.AppendBreak(_previous.IsHeading ? AfterHeadingBreak : BetweenParagraphsBreak);
        builder.AppendText(segment.Text);
        _previous = segment;
        try
        {
            synth.Rate = SpeechSpeed.ToSapiRate(Speed);
            _currentPrompt = synth.SpeakAsync(builder);
        }
        catch (Exception)
        {
            Stop();
            Failed?.Invoke();
        }
    }

    private void OnSpeakCompleted(object? sender, SpeakCompletedEventArgs e)
    {
        // Fires on a synthesizer thread, and also for cancelled
        // prompts — only the current prompt of the current session
        // advances the reading, and only on the UI dispatcher.
        if (!ReferenceEquals(e.Prompt, _currentPrompt))
            return;
        _currentPrompt = null;
        var session = _session;
        var dispatcher = _dispatcher;
        if (dispatcher is null)
            return;
        dispatcher.BeginInvoke(() =>
        {
            if (session != _session)
                return;
            if (e.Error is not null)
            {
                Stop();
                Failed?.Invoke();
                return;
            }
            if (!e.Cancelled)
                SpeakNext(session);
        });
    }

    public void Dispose() => Stop();
}
