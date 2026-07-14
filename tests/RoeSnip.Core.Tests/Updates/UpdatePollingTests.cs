using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using RoeSnip.Core.Updates;
using Xunit;

namespace RoeSnip.Core.Tests.Updates;

/// <summary>UpdatePolling.RunPeriodicAsync's own while(true) never returns under normal operation
/// (matching the three call sites it replaces), so every test here drives it with a scripted
/// <c>delay</c> callback that advances a fake clock synchronously and eventually throws
/// <see cref="OperationCanceledException"/> to end the loop deterministically - the same role a real
/// cancellation would play in production, but driven entirely by call count rather than wall-clock
/// time so these tests run instantly. Because the scripted delay never actually awaits anything,
/// every iteration up to the throwing call runs synchronously within the single await of the
/// returned task, which is what lets assertions below count exact tick/wake occurrences.</summary>
public class UpdatePollingTests
{
    private sealed class FakeClock
    {
        public DateTime Now = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        public DateTime UtcNow() => Now;
        public void Advance(TimeSpan by) => Now += by;
    }

    [Fact]
    public async Task IterationZero_FiresImmediately_BeforeAnyDelay()
    {
        int tickCount = 0;
        var clock = new FakeClock();

        Task loop = UpdatePolling.RunPeriodicAsync(
            getFrequencySetting: () => "StartupOnly",
            tick: () => { tickCount++; return Task.CompletedTask; },
            delay: _ => throw new OperationCanceledException("test stop"),
            utcNow: clock.UtcNow);

        await Assert.ThrowsAsync<OperationCanceledException>(() => loop);
        Assert.Equal(1, tickCount); // fired before the very first (throwing) delay call was ever reached
    }

    [Fact]
    public async Task SettingsPollCadence_WakesEveryMinute_RegardlessOfConfiguredInterval()
    {
        var delayCalls = new List<TimeSpan>();
        var clock = new FakeClock();
        int wakeCount = 0;
        Func<TimeSpan, Task> delay = ts =>
        {
            delayCalls.Add(ts);
            if (++wakeCount >= 3)
            {
                throw new OperationCanceledException("test stop");
            }
            return Task.CompletedTask;
        };

        Task loop = UpdatePolling.RunPeriodicAsync(() => "Daily", () => Task.CompletedTask, delay, clock.UtcNow);
        await Assert.ThrowsAsync<OperationCanceledException>(() => loop);

        Assert.All(delayCalls, ts => Assert.Equal(UpdatePolling.SettingsPollInterval, ts));
        Assert.Equal(TimeSpan.FromMinutes(1), UpdatePolling.SettingsPollInterval);
    }

    [Fact]
    public async Task StartupOnly_NeverFiresAgain_EvenAcrossHugeElapsedTime()
    {
        int tickCount = 0;
        var clock = new FakeClock();
        int wakeCount = 0;
        Func<TimeSpan, Task> delay = _ =>
        {
            clock.Advance(TimeSpan.FromDays(365)); // absurdly overdue by any real interval
            if (++wakeCount >= 5)
            {
                throw new OperationCanceledException("test stop");
            }
            return Task.CompletedTask;
        };

        Task loop = UpdatePolling.RunPeriodicAsync(
            () => "StartupOnly",
            () => { tickCount++; return Task.CompletedTask; },
            delay,
            clock.UtcNow);
        await Assert.ThrowsAsync<OperationCanceledException>(() => loop);

        Assert.Equal(1, tickCount); // only ever the iteration-zero check
    }

    [Fact]
    public async Task ElapsedBelowInterval_DoesNotFireAgain()
    {
        int tickCount = 0;
        var clock = new FakeClock();
        int wakeCount = 0;
        Func<TimeSpan, Task> delay = _ =>
        {
            clock.Advance(TimeSpan.FromMinutes(10)); // 3 wakes = 30 min, well under Hourly's 1h(+<=15m jitter)
            if (++wakeCount >= 3)
            {
                throw new OperationCanceledException("test stop");
            }
            return Task.CompletedTask;
        };

        Task loop = UpdatePolling.RunPeriodicAsync(
            () => "Hourly",
            () => { tickCount++; return Task.CompletedTask; },
            delay,
            clock.UtcNow);
        await Assert.ThrowsAsync<OperationCanceledException>(() => loop);

        Assert.Equal(1, tickCount); // still just the iteration-zero check
    }

    [Fact]
    public async Task ElapsedPastInterval_FiresAgain_UsingWallClockNotWakeCount()
    {
        int tickCount = 0;
        var clock = new FakeClock();
        int wakeCount = 0;
        Func<TimeSpan, Task> delay = _ =>
        {
            clock.Advance(TimeSpan.FromDays(2)); // >> Hourly interval + max 5-minute jitter
            if (++wakeCount >= 3)
            {
                throw new OperationCanceledException("test stop");
            }
            return Task.CompletedTask;
        };

        Task loop = UpdatePolling.RunPeriodicAsync(
            () => "Hourly",
            () => { tickCount++; return Task.CompletedTask; },
            delay,
            clock.UtcNow);
        await Assert.ThrowsAsync<OperationCanceledException>(() => loop);

        // iteration zero, then a real check on each of the two wakes that got to run before the
        // third wake's throw ended the loop.
        Assert.Equal(3, tickCount);
    }

    [Fact]
    public async Task FrequencySetting_IsReReadEveryWake_SoALiveChangeTakesEffectNextWake()
    {
        int tickCount = 0;
        var clock = new FakeClock();
        string frequency = "StartupOnly";
        int wakeCount = 0;
        Func<TimeSpan, Task> delay = _ =>
        {
            wakeCount++;
            clock.Advance(TimeSpan.FromDays(2)); // always far past any real interval once switched on
            if (wakeCount == 2)
            {
                frequency = "Hourly"; // simulates a Settings-window save landing mid-loop
            }
            if (wakeCount >= 4)
            {
                throw new OperationCanceledException("test stop");
            }
            return Task.CompletedTask;
        };

        Task loop = UpdatePolling.RunPeriodicAsync(
            () => frequency,
            () => { tickCount++; return Task.CompletedTask; },
            delay,
            clock.UtcNow);
        await Assert.ThrowsAsync<OperationCanceledException>(() => loop);

        // iteration zero (StartupOnly, still fires once) + wake2's fire (freq just flipped to
        // Hourly, elapsed already 4 days) + wake3's fire (another 2 days elapsed since wake2's
        // check) = 3. Wake1 must NOT have fired: frequency was still StartupOnly for that read.
        Assert.Equal(3, tickCount);
    }

    [Fact]
    public async Task Tick_IsAwaitedInline_SoASlowTickBlocksTheLoopUntilItCompletes()
    {
        var order = new List<string>();
        var clock = new FakeClock();
        var tickGate = new TaskCompletionSource();

        Func<Task> tick = async () =>
        {
            order.Add("tick-start");
            await tickGate.Task.ConfigureAwait(false);
            order.Add("tick-end");
        };
        Func<TimeSpan, Task> delay = _ =>
        {
            order.Add("delay");
            throw new OperationCanceledException("test stop"); // never reached until the gate opens
        };

        Task loop = UpdatePolling.RunPeriodicAsync(() => "StartupOnly", tick, delay, clock.UtcNow);

        // Iteration zero's tick is parked on tickGate - if the loop fire-and-forgot it, the delay
        // call (and the "delay" entry / thrown cancellation) would already be visible here. Instead
        // nothing beyond "tick-start" has happened and the returned task is still running.
        Assert.Equal(new[] { "tick-start" }, order);
        Assert.False(loop.IsCompleted);

        tickGate.SetResult();
        await Assert.ThrowsAsync<OperationCanceledException>(() => loop);

        Assert.Equal(new[] { "tick-start", "tick-end", "delay" }, order);
    }
}
