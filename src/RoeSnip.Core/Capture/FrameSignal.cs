namespace RoeSnip.Core.Capture;

/// <summary>A frame counter a reader can block on: <see cref="WaitAbove"/> returns once a frame newer
/// than a recorded sequence has arrived.</summary>
public sealed class FrameSignal
{
    private readonly object _gate = new();
    private long _sequence;

    public long Sequence => Interlocked.Read(ref _sequence);

    public void Bump()
    {
        lock (_gate)
        {
            _sequence++;
            Monitor.PulseAll(_gate);
        }
    }

    public bool WaitAbove(long baseline, TimeSpan timeout, Func<bool>? abort = null)
    {
        long deadline = Environment.TickCount64 + (long)timeout.TotalMilliseconds;
        lock (_gate)
        {
            while (_sequence <= baseline)
            {
                if (abort?.Invoke() == true) return false;
                long left = deadline - Environment.TickCount64;
                if (left <= 0) return false;
                Monitor.Wait(_gate, (int)Math.Min(left, 50));
            }
            return true;
        }
    }
}
