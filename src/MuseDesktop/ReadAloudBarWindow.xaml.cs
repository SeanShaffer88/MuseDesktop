using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using MuseDesktop.Core;

namespace MuseDesktop;

/// <summary>
/// The read-aloud control bar (M5; design Concept A — the floating
/// pill, SEAN's pick 2026-11-03): a borderless pill floating at
/// the bottom-center of the main window while a reading is playing
/// or paused, gone when playback is idle or stopped. It replaces
/// the status-bar controls (v2.1.0), which carried the same
/// actions — one live control surface, not two — and because it
/// floats, the controls now exist in Compact Focus too, where
/// the status bar is hidden.
///
/// It binds to the surface the M5 core half prepared: Core's
/// <see cref="SpeechPlaybackController"/>, reached through the
/// <see cref="ReadAloudEngine"/> that drives it. The toggle's
/// face, the rate label, and the "part N of M" status follow the
/// controller's StateChanged / SegmentStarted events. The buttons
/// do NOT drive the controller directly for pause and stop — the
/// controller alone cannot pause or release the synthesizer — so
/// those two go through the delegates the main window hands in
/// (its own toggle and stop paths, status lines included). The
/// speed steps DO call the controller's SetRate themselves: the
/// rate is pure controller state, read by the engine as each
/// segment starts, so a step lands from the next segment, exactly
/// as the old speed button promised.
///
/// The pill is an owned window — never Topmost, no taskbar entry —
/// so it rides above the main window and only the main window,
/// and it closes when the main window closes. While visible it
/// tracks the owner's moves, resizes, minimize, and tray hide.
/// Showing it never activates it: the page keeps the focus, the
/// way the status-bar controls left it.
/// </summary>
public partial class ReadAloudBarWindow : Window
{
    /// <summary>The gap between the pill and the main window's bottom edge, in DIPs.</summary>
    private const double BottomMarginDip = 28;

    private readonly ReadAloudEngine _engine;
    private readonly Action _togglePause;
    private readonly Action _stop;
    private Window? _owner;
    private bool _closed;

    /// <summary>
    /// Build the bar over one read-aloud engine. <paramref name="togglePause"/>
    /// and <paramref name="stop"/> are the main window's own
    /// read-aloud paths — the bar adds no second way to pause or
    /// stop a reading.
    /// </summary>
    public ReadAloudBarWindow(ReadAloudEngine engine, Action togglePause, Action stop)
    {
        _engine = engine;
        _togglePause = togglePause;
        _stop = stop;
        InitializeComponent();
        var playback = engine.Playback;
        playback.StateChanged += OnPlaybackStateChanged;
        playback.SegmentStarted += OnSegmentStarted;
        Closed += OnClosed;
        Refresh();
    }

    /// <summary>Show the pill over the main window, docked; the main window calls this when a reading starts.</summary>
    public void Present()
    {
        if (_closed)
            return;
        AttachOwner(Owner);
        Refresh();
        if (!IsVisible)
            Show();
        Dock();
    }

    /// <summary>Hide the pill — its reading stopped or finished. The window itself is kept for the next reading; the main window closing closes it for good.</summary>
    public void Retire()
    {
        if (_closed)
            return;
        if (IsVisible)
            Hide();
    }

    // ──────────────────────── owner tracking ────────────────────────

    private void AttachOwner(Window? owner)
    {
        if (ReferenceEquals(_owner, owner))
            return;
        DetachOwner();
        _owner = owner;
        if (owner is null)
            return;
        owner.LocationChanged += OnOwnerMovedOrResized;
        owner.SizeChanged += OnOwnerMovedOrResized;
        owner.StateChanged += OnOwnerStateChanged;
        owner.IsVisibleChanged += OnOwnerVisibilityChanged;
    }

    private void DetachOwner()
    {
        var owner = _owner;
        _owner = null;
        if (owner is null)
            return;
        owner.LocationChanged -= OnOwnerMovedOrResized;
        owner.SizeChanged -= OnOwnerMovedOrResized;
        owner.StateChanged -= OnOwnerStateChanged;
        owner.IsVisibleChanged -= OnOwnerVisibilityChanged;
    }

    private void OnOwnerMovedOrResized(object? sender, EventArgs e)
    {
        if (IsVisible)
            Dock();
    }

    private void OnOwnerStateChanged(object? sender, EventArgs e) => SyncWithOwner();

    private void OnOwnerVisibilityChanged(object sender, DependencyPropertyChangedEventArgs e) =>
        SyncWithOwner();

    /// <summary>
    /// The pill shows only while its owner is on screen to carry
    /// it: a minimized or tray-hidden main window takes the pill
    /// with it, and restoring the window mid-reading brings the
    /// pill back — a reading that ended meanwhile keeps it gone,
    /// because the controller is idle by then.
    /// </summary>
    private void SyncWithOwner()
    {
        var owner = _owner;
        if (owner is null || _closed)
            return;
        var wanted = _engine.Playback.IsActive
            && owner.IsVisible
            && owner.WindowState != WindowState.Minimized;
        if (wanted && !IsVisible)
        {
            Show();
            Dock();
        }
        else if (!wanted && IsVisible)
            Hide();
        else if (wanted)
            Dock();
    }

    /// <summary>Bottom-center of the owner, a small margin above its bottom edge. The owner's coordinates are DIPs, so the pill lands right at any scaling.</summary>
    private void Dock()
    {
        var owner = _owner;
        if (owner is null || !IsVisible)
            return;
        Left = owner.Left + (owner.ActualWidth - ActualWidth) / 2;
        Top = owner.Top + owner.ActualHeight - ActualHeight - BottomMarginDip;
    }

    // ──────────────────────── controller binding ────────────────────────

    private void OnPlaybackStateChanged()
    {
        // Controller events are raised on the caller's thread —
        // the shell's own, in practice (the engine marshals its
        // completions to the UI dispatcher before advancing).
        // Marshal anyway, the dictation path's discipline, so a
        // future caller can never cross threads into the pill.
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(OnPlaybackStateChanged);
            return;
        }
        Refresh();
        // Idle means the reading stopped or finished, whatever
        // path got here — the pill's showing is over. (The main
        // window also retires it through its own hide path.)
        if (!_engine.Playback.IsActive)
            Retire();
    }

    private void OnSegmentStarted(int index)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(() => OnSegmentStarted(index));
            return;
        }
        Refresh();
    }

    /// <summary>Set the faces from the controller: the toggle (Pause while playing, Resume while paused), the rate label, the part-N-of-M status, and the speed steps' ends (dimmed at the clamps, where a step would change nothing).</summary>
    private void Refresh()
    {
        var playback = _engine.Playback;
        var paused = playback.State == SpeechPlaybackState.Paused;
        var toggleLabel = Strings.Get(paused ? "ui.reading_resume" : "ui.reading_pause");
        PauseResumeButton.Content = toggleLabel;
        AutomationProperties.SetName(PauseResumeButton, toggleLabel);
        RateText.Text = FormatRate(playback.Rate);
        var part = playback.CurrentIndex + 1;
        PartText.Text = playback.Segments.Count > 0 && part >= 1
            ? Strings.Format("ui.reading_part", part, playback.Segments.Count)
            : "";
        SlowerButton.IsEnabled = playback.Rate > SpeechPlaybackController.MinRate;
        FasterButton.IsEnabled = playback.Rate < SpeechPlaybackController.MaxRate;
    }

    /// <summary>The rate label: the multiplier with its decimal — "1.0×", "0.75×" — in the app's own number format. Every rate sits on the controller's quarter-step grid, so at most two decimals ever say it exactly.</summary>
    private static string FormatRate(double rate) =>
        string.Create(CultureInfo.GetCultureInfo(Strings.Current.Language), $"{rate:0.0#}×");

    // ──────────────────────── the buttons ────────────────────────

    private void OnPauseResume(object sender, RoutedEventArgs e) => _togglePause();

    private void OnStop(object sender, RoutedEventArgs e) => _stop();

    private void OnSlower(object sender, RoutedEventArgs e) =>
        StepRate(-SpeechPlaybackController.RateStep);

    private void OnFaster(object sender, RoutedEventArgs e) =>
        StepRate(SpeechPlaybackController.RateStep);

    /// <summary>Step the controller's rate one quarter step; SetRate clamps and snaps, so the ends of the range are honest no-ops. The label follows at once; the pace itself applies from the next segment.</summary>
    private void StepRate(double delta)
    {
        _engine.Playback.SetRate(_engine.Playback.Rate + delta);
        Refresh();
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _closed = true;
        var playback = _engine.Playback;
        playback.StateChanged -= OnPlaybackStateChanged;
        playback.SegmentStarted -= OnSegmentStarted;
        DetachOwner();
    }
}
