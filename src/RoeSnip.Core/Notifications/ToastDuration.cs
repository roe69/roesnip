namespace RoeSnip.Core.Notifications;

/// <summary>The one place that bounds how long any toast / balloon stays up.</summary>
public static class ToastDuration
{
    public const int MaxMs = 10000;

    /// <summary>Clamps a requested lifetime to 1 ms .. <see cref="MaxMs"/>.</summary>
    public static int Cap(int durationMs) => Math.Clamp(durationMs, 1, MaxMs);

    public static TimeSpan Cap(TimeSpan duration) => TimeSpan.FromMilliseconds(Cap((int)Math.Min(duration.TotalMilliseconds, int.MaxValue)));
}
