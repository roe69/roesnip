using System;
using System.Threading.Tasks;

namespace RoeSnip.Core.Updates;

/// <summary>The periodic-check scheduling skeleton shared by every long-lived tray resident's
/// update loop (WPF's <c>TrayApp.RunUpdateLoopAsync</c>, Avalonia's Windows
/// <c>TrayApp.RunUpdateLoopAsync</c>, and Avalonia's Linux/macOS
/// <c>TrayApp.RunPassiveNoticeLoopAsync</c>) — three call sites that differ only in what "check for
/// an update" means (auto-apply vs. a passive notice) and how that work gets marshalled back onto a
/// UI thread, never in the scheduling itself. <paramref name="tick"/> owns both of those concerns;
/// this method owns only WHEN to call it.
///
/// Semantics (unchanged from the three call sites this replaces): iteration zero runs
/// <paramref name="tick"/> immediately and IS the old startup-only check; after that this wakes
/// every minute purely to re-read <paramref name="getFrequencySetting"/>, and only actually calls
/// <paramref name="tick"/> again once the configured interval (plus jitter) has elapsed since the
/// last call. The 1-minute wake - not a timer re-armed to the configured interval - is what lets a
/// live settings change take effect without any cancellation/re-arm plumbing: the setting is read
/// fresh on every wake, so a Settings-window save is picked up on the very next minute. Wall-clock
/// elapsed (<see cref="DateTime.UtcNow"/>, never <c>.Now</c> - a DST jump must not double-fire or
/// stall this) rather than counting wakes means a laptop resumed after sleep fires its overdue
/// check on the next wake instead of drifting forever behind. <paramref name="tick"/> is awaited
/// INLINE, not fire-and-forgotten per wake: if a tick is parked for hours (an auto-apply's
/// idle-wait gate), this loop is parked with it, so ticks can never pile up behind whatever lock the
/// tick body itself serializes on. Jitter (up to interval/4, capped at 5 minutes,
/// <see cref="UpdateCheckFrequencies.Jitter"/>) is recomputed on every actual fire and keeps a
/// fleet of machines that all boot at the same moment from stampeding GitHub's API in lockstep.
/// <see cref="UpdateCheckFrequency.StartupOnly"/> (<see cref="UpdateCheckFrequencies.Interval"/>
/// returning null) means "never call tick again on our own" - the loop keeps waking every minute to
/// watch for the setting changing to something else, it just never fires.
///
/// Never returns under normal operation (the while loop has no exit condition, matching the three
/// call sites, which all ran for the lifetime of their process). <paramref name="delay"/> and
/// <paramref name="utcNow"/> exist purely so tests can drive the loop deterministically without a
/// real per-wake wait; production callers omit both and get real <see cref="Task.Delay(TimeSpan)"/>
/// / <see cref="DateTime.UtcNow"/>, exactly like <see cref="GitHubLatestReleaseClient"/>'s own
/// injectable clock.</summary>
public static class UpdatePolling
{
    /// <summary>How often the loop wakes to re-read the frequency setting - independent of, and
    /// always shorter than, the configured check interval itself.</summary>
    public static readonly TimeSpan SettingsPollInterval = TimeSpan.FromMinutes(1);

    public static async Task RunPeriodicAsync(
        Func<string?> getFrequencySetting,
        Func<Task> tick,
        Func<TimeSpan, Task>? delay = null,
        Func<DateTime>? utcNow = null)
    {
        Func<TimeSpan, Task> delayAsync = delay ?? (static d => Task.Delay(d));
        Func<DateTime> now = utcNow ?? (static () => DateTime.UtcNow);

        await tick().ConfigureAwait(false); // iteration zero = the startup check
        DateTime lastCheckUtc = now();
        TimeSpan currentInterval = UpdateCheckFrequencies.Interval(UpdateCheckFrequencies.Parse(getFrequencySetting()))
            ?? TimeSpan.FromHours(1);
        TimeSpan jitter = UpdateCheckFrequencies.Jitter(currentInterval, Random.Shared.NextDouble());
        while (true)
        {
            await delayAsync(SettingsPollInterval).ConfigureAwait(false); // settings-poll cadence, not network cadence
            var freq = UpdateCheckFrequencies.Parse(getFrequencySetting()); // FRESH read: live reconfigure
            TimeSpan? interval = UpdateCheckFrequencies.Interval(freq);
            if (interval is null)
            {
                continue; // StartupOnly: keep watching the setting, never check again on our own
            }
            if (now() - lastCheckUtc < interval + jitter)
            {
                continue;
            }
            lastCheckUtc = now();
            jitter = UpdateCheckFrequencies.Jitter(interval.Value, Random.Shared.NextDouble());
            await tick().ConfigureAwait(false); // awaited INLINE: no pile-up possible
        }
    }
}
