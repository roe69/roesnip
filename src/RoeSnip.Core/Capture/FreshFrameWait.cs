namespace RoeSnip.Core.Capture;

/// <summary>Waits for the first frame a woken stream sends. A stream that stays silent past a step is
/// kicked (deactivated and activated again) and waited on again; the caller never reads a frame
/// older than the wake, so a false result means "capture failed", not "use the old buffer".</summary>
public static class FreshFrameWait
{
    public static readonly TimeSpan[] DefaultSteps =
    {
        TimeSpan.FromMilliseconds(250), TimeSpan.FromMilliseconds(250), TimeSpan.FromMilliseconds(300),
        TimeSpan.FromMilliseconds(800),
    };

    /// <summary>True once <paramref name="waitAbove"/> saw a frame newer than <paramref name="baseline"/>.
    /// <paramref name="kicks"/> counts the re-activations done.</summary>
    public static bool Await(long baseline, IReadOnlyList<TimeSpan> steps,
        Func<long, TimeSpan, bool> waitAbove, Action kick, out int kicks)
    {
        kicks = 0;
        for (int i = 0; i < steps.Count; i++)
        {
            if (waitAbove(baseline, steps[i])) return true;
            if (i + 1 < steps.Count)
            {
                kicks++;
                kick();
            }
        }
        return false;
    }
}
