using System;

namespace RoeSnip.Core.Recording;

/// <summary>Schedule-based capture-cadence throttle - ported verbatim (as pure math, extracted from
/// the callback-shaped original) from the WPF app's RegionRecorder.OnFrameArrived. See that method's
/// own doc comment for the full "why schedule-advance, not since-last-forward" reasoning: advancing a
/// due-time by the target interval on every ACCEPTED frame, rather than gating on "has
/// minIntervalTicks passed since the last forwarded frame", hits the target rate on average
/// regardless of the source's own delivery cadence - a 50fps target against a 60Hz WGC source does
/// not alias down to 30fps the way a naive since-last-forward gate would (it would skip every other
/// 60Hz callback).
///
/// Pure, allocation-free struct so every <see cref="IRegionCaptureSource"/> implementation - the
/// Windows staging-ring recorder AND the portable polling fallback
/// (<see cref="PolledRegionCaptureSource"/>) - shares EXACTLY the same throttle behavior, unit-tested
/// directly (RecordingScheduleTests) rather than only observable through a live capture. This is the
/// "fps IS the recorder throttle" piece item 20 is required to land: the recording chrome's FPS
/// slider (RoeSnipSettings.GifFps/Mp4Fps) drives this constructor's <c>targetFps</c> parameter
/// directly, with no other rate-shaping anywhere upstream of it.</summary>
public struct RecordingSchedule
{
    private readonly long _minIntervalTicks;
    private long _nextDueTicks;

    /// <param name="targetFps">Frames accepted per second of wall-clock time, on the SAME tick clock
    /// as <paramref name="tickFrequency"/> (Stopwatch.Frequency in production, an arbitrary constant
    /// in tests).</param>
    /// <param name="tickFrequency">Ticks per second of whichever clock <see cref="ShouldAccept"/>'s
    /// <c>nowTicks</c> is measured on - production callers pass <c>Stopwatch.Frequency</c>.</param>
    public RecordingSchedule(int targetFps, long tickFrequency)
    {
        if (targetFps <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(targetFps), targetFps, "targetFps must be positive.");
        }
        if (tickFrequency <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(tickFrequency), tickFrequency, "tickFrequency must be positive.");
        }
        _minIntervalTicks = tickFrequency / targetFps;
        _nextDueTicks = 0; // the very first frame at any nowTicks >= 0 is always accepted
    }

    /// <summary>True if a frame arriving at <paramref name="nowTicks"/> should be ACCEPTED - and, on
    /// true, advances the schedule so the NEXT accepted frame is paced correctly. False (throttled)
    /// has no side effect, so a caller can call this as often as frames actually arrive without
    /// needing its own separate "did we already check this tick" bookkeeping.</summary>
    public bool ShouldAccept(long nowTicks)
    {
        long due = _nextDueTicks;
        if (nowTicks < due)
        {
            return false;
        }
        // Clamped so a quiet stretch (nothing changed for a while, no callbacks/polls at all) can't
        // bank an unbounded burst of "already due" credit - the next accepted frame is paced at most
        // one interval ahead of "now", never further.
        _nextDueTicks = Math.Max(due + _minIntervalTicks, nowTicks - _minIntervalTicks);
        return true;
    }
}
