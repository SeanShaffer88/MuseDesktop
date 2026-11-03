using MuseDesktop.Core;
using Xunit;

namespace MuseDesktop.Core.Tests;

// ───────────────────── Read-aloud playback control (M5) ─────────────────────
//
// The shell owns the synthesizer; these tests pin the controller
// that now owns the queue decisions around it: Start speaks the
// head segment, Advance walks the queue and finishes honestly at
// its end, Pause holds (and blocks Advance), Stop resets without
// claiming a finish, and the rate is clamped to 0.5×–2× on the
// quarter-step grid whatever a caller hands in.

public class SpeechPlaybackTests
{
    private static SpeechSegment[] Segments(int count) =>
        Enumerable.Range(1, count)
            .Select(i => new SpeechSegment($"Segment {i}", IsHeading: i == 1))
            .ToArray();

    private static SpeechPlaybackController Loaded(int count = 3)
    {
        var controller = new SpeechPlaybackController();
        controller.Load(Segments(count));
        return controller;
    }

    [Fact]
    public void Start_SpeaksTheHeadSegment_AndPlays()
    {
        var controller = Loaded();
        var started = new List<int>();
        controller.SegmentStarted += index => started.Add(index);
        var segment = controller.Start();
        Assert.Equal("Segment 1", segment?.Text);
        Assert.Equal(SpeechPlaybackState.Playing, controller.State);
        Assert.Equal(0, controller.CurrentIndex);
        Assert.Equal(segment, controller.CurrentSegment);
        Assert.Equal(new[] { 0 }, started);
    }

    [Fact]
    public void Start_OnAnEmptyQueue_FinishesAtOnce_AndStaysIdle()
    {
        var controller = Loaded(0);
        var finished = false;
        controller.PlaybackFinished += () => finished = true;
        Assert.Null(controller.Start());
        Assert.Equal(SpeechPlaybackState.Idle, controller.State);
        Assert.True(finished);
    }

    [Fact]
    public void Advance_WalksTheQueue_ThenFinishesHonestly()
    {
        var controller = Loaded(2);
        var finished = false;
        controller.PlaybackFinished += () => finished = true;
        controller.Start();
        Assert.Equal("Segment 2", controller.Advance()?.Text);
        Assert.Equal(1, controller.CurrentIndex);
        Assert.Null(controller.Advance());
        Assert.Equal(SpeechPlaybackState.Idle, controller.State);
        Assert.Equal(-1, controller.CurrentIndex);
        Assert.Null(controller.CurrentSegment);
        Assert.True(finished);
    }

    [Fact]
    public void Advance_WhilePausedOrIdle_DoesNotMove()
    {
        var controller = Loaded();
        Assert.Null(controller.Advance());   // idle: nothing to advance
        controller.Start();
        controller.Pause();
        Assert.Null(controller.Advance());   // paused: the held segment stands
        Assert.Equal(0, controller.CurrentIndex);
        Assert.Equal(SpeechPlaybackState.Paused, controller.State);
    }

    [Fact]
    public void PauseAndResume_OnlyFromTheirOwnStates()
    {
        var controller = Loaded();
        Assert.False(controller.Pause());    // idle
        Assert.False(controller.Resume());   // idle
        controller.Start();
        Assert.False(controller.Resume());   // already playing
        Assert.True(controller.Pause());
        Assert.False(controller.Pause());    // already paused
        Assert.True(controller.Resume());
        Assert.Equal(SpeechPlaybackState.Playing, controller.State);
        Assert.Equal(0, controller.CurrentIndex);   // resume holds the segment
    }

    [Fact]
    public void Stop_ResetsWithoutClaimingAFinish_AndKeepsTheQueue()
    {
        var controller = Loaded();
        var finished = false;
        controller.PlaybackFinished += () => finished = true;
        controller.Start();
        controller.Advance();
        controller.Stop();
        Assert.Equal(SpeechPlaybackState.Idle, controller.State);
        Assert.Equal(-1, controller.CurrentIndex);
        Assert.False(finished);
        // The queue survives: Start replays from the head.
        Assert.Equal("Segment 1", controller.Start()?.Text);
    }

    [Fact]
    public void PreviousSegment_FollowsTheCurrentOne()
    {
        var controller = Loaded();
        Assert.Null(controller.PreviousSegment);
        controller.Start();
        Assert.Null(controller.PreviousSegment);   // nothing before the head
        controller.Advance();
        Assert.Equal("Segment 1", controller.PreviousSegment?.Text);
    }

    [Fact]
    public void Load_ReplacesTheQueue_ResetsPlayback_KeepsTheRate()
    {
        var controller = Loaded();
        controller.SetRate(1.5);
        controller.Start();
        controller.Load(Segments(1));
        Assert.Equal(SpeechPlaybackState.Idle, controller.State);
        Assert.Equal(-1, controller.CurrentIndex);
        Assert.Single(controller.Segments);
        Assert.Equal(1.5, controller.Rate);
    }

    [Theory]
    [InlineData(0.5, 0.5)]
    [InlineData(2.0, 2.0)]
    [InlineData(0.1, 0.5)]      // below the floor clamps up
    [InlineData(3.0, 2.0)]      // above the ceiling clamps down
    [InlineData(1.0, 1.0)]
    [InlineData(1.13, 1.25)]    // snaps to the nearest quarter step
    [InlineData(1.12, 1.0)]
    [InlineData(1.75, 1.75)]
    [InlineData(double.NaN, 1.0)]
    public void ClampRate_ClampsAndSnapsToTheGrid(double given, double expected)
    {
        Assert.Equal(expected, SpeechPlaybackController.ClampRate(given));
    }

    [Fact]
    public void SetRate_ReportsTheRateInForce()
    {
        var controller = Loaded(1);
        Assert.Equal(0.75, controller.SetRate(0.8));
        Assert.Equal(0.75, controller.Rate);
        Assert.Equal(2.0, controller.SetRate(99));
    }

    [Fact]
    public void StateChanged_RaisedOnTransitions()
    {
        var controller = Loaded(1);
        var states = new List<SpeechPlaybackState>();
        controller.StateChanged += () => states.Add(controller.State);
        controller.Start();
        controller.Pause();
        controller.Resume();
        controller.Stop();
        Assert.Equal(
            new[]
            {
                SpeechPlaybackState.Playing,
                SpeechPlaybackState.Paused,
                SpeechPlaybackState.Playing,
                SpeechPlaybackState.Idle,
            },
            states);
    }
}
