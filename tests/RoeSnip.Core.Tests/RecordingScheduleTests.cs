using System;
using RoeSnip.Core.Recording;
using Xunit;

namespace RoeSnip.Core.Tests;

/// <summary>Pure schedule-throttle math (extracted from the WPF app's RegionRecorder.OnFrameArrived
/// callback body specifically so it is testable without a live WGC session — see
/// RecordingSchedule's own class doc comment).</summary>
public class RecordingScheduleTests
{
    [Fact]
    public void FirstFrameAtTimeZero_IsAlwaysAccepted()
    {
        var schedule = new RecordingSchedule(targetFps: 50, tickFrequency: 10_000_000);
        Assert.True(schedule.ShouldAccept(0));
    }

    [Fact]
    public void SecondFrame_BeforeInterval_IsThrottled()
    {
        // 50fps at a 10,000,000-tick/sec clock => 200,000 ticks/frame.
        var schedule = new RecordingSchedule(targetFps: 50, tickFrequency: 10_000_000);
        Assert.True(schedule.ShouldAccept(0));
        Assert.False(schedule.ShouldAccept(100_000)); // half an interval later — too soon
    }

    [Fact]
    public void SecondFrame_AtOrAfterInterval_IsAccepted()
    {
        var schedule = new RecordingSchedule(targetFps: 50, tickFrequency: 10_000_000);
        Assert.True(schedule.ShouldAccept(0));
        Assert.True(schedule.ShouldAccept(200_000)); // exactly one interval later
    }

    [Fact]
    public void HigherSourceCadence_StillHitsTargetRateOnAverage()
    {
        // A 60Hz source (16,666.67-tick callbacks on a 1,000,000-tick/sec clock) driven at a 50fps
        // target must not alias down to 30fps the way a naive since-last-forward gate would — see
        // RecordingSchedule's own class doc comment.
        const long tickFrequency = 1_000_000;
        var schedule = new RecordingSchedule(targetFps: 50, tickFrequency: tickFrequency);
        long sourceIntervalTicks = tickFrequency / 60;

        int accepted = 0;
        long simulatedSeconds = 5;
        long totalTicks = tickFrequency * simulatedSeconds;
        for (long t = 0; t < totalTicks; t += sourceIntervalTicks)
        {
            if (schedule.ShouldAccept(t))
            {
                accepted++;
            }
        }

        double effectiveFps = accepted / (double)simulatedSeconds;
        // Must land close to 50fps (not 30, not 60) — a generous tolerance since 60Hz doesn't divide
        // 50fps evenly.
        Assert.InRange(effectiveFps, 45.0, 55.0);
    }

    [Fact]
    public void QuietStretch_DoesNotBankAnUnboundedBurst()
    {
        // After a long gap with no calls at all, the very next call must be accepted (not throttled
        // by ancient due-time math) — but the clamp in ShouldAccept must still bound how many
        // further calls in a tight burst immediately afterward can also pass, rather than letting
        // due-time math "owe" the whole quiet stretch's worth of frames all at once.
        var schedule = new RecordingSchedule(targetFps: 50, tickFrequency: 10_000_000);
        Assert.True(schedule.ShouldAccept(0));

        long farFuture = 10_000_000_000L; // 1000 seconds later
        Assert.True(schedule.ShouldAccept(farFuture));

        int accepted = 0;
        const int burstCalls = 1000;
        for (int i = 1; i <= burstCalls; i++)
        {
            if (schedule.ShouldAccept(farFuture + i)) // 1 tick apart — far tighter than the schedule's interval
            {
                accepted++;
            }
        }

        // A handful of frames right at the boundary is expected (see this class's own doc comment),
        // but nowhere near all 1000 tightly-spaced calls — the whole point of the clamp.
        Assert.True(accepted < burstCalls / 10, $"expected a bounded burst, got {accepted} of {burstCalls}");
    }

    [Fact]
    public void InvalidTargetFps_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new RecordingSchedule(targetFps: 0, tickFrequency: 10_000_000));
        Assert.Throws<ArgumentOutOfRangeException>(() => new RecordingSchedule(targetFps: -1, tickFrequency: 10_000_000));
    }
}
