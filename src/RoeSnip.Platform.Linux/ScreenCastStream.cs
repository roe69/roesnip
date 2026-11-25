using RoeSnip.Core.Capture;
using RoeSnip.Core.Diagnostics;

namespace RoeSnip.Platform.Linux;

/// <summary>One portal stream consumed by a GStreamer pipeline
/// (pipewiresrc -> videoconvert -> BGRx -> appsink, max-buffers=1 drop=true). A pump thread keeps
/// only the newest sample, so a capture copies the screen as it is now without waiting for a
/// frame. Mutter sends frames on damage only, so the newest sample is current even when old.</summary>
internal sealed unsafe class ScreenCastStream : IScreenCastStream
{
    private readonly object _gate = new();
    private readonly IntPtr _pipeline;
    private readonly IntPtr _sink;
    private readonly IntPtr _bus;
    private readonly Thread _pump;
    private readonly ManualResetEventSlim _firstSample = new(false);
    private IntPtr _latest; // GstSample*, owned, guarded by _gate
    private readonly FrameSignal _frames = new();
    private volatile bool _stop;
    private volatile bool _idle;
    private volatile string? _deadReason;
    private int _disposed;

    public ScreenCastStreamGeometry Geometry { get; }
    public string? DeadReason => _deadReason;
    public bool IsDead => _deadReason is not null;

    /// <summary>Bumped for every new sample; lets the recorder skip unchanged frames.</summary>
    public long Sequence => _frames.Sequence;

    public ScreenCastStream(int pipeWireFd, ScreenCastStreamGeometry geometry)
    {
        Geometry = geometry;
        // always-copy: the kept latest sample must not pin one of mutter's shared PipeWire buffers.
        // Without it the producer runs out of free buffers, every new frame is dropped (pw-top ERR
        // in the 100k's) and snapshots show the screen as it was minutes ago.
        string description =
            $"pipewiresrc fd={pipeWireFd} path={geometry.NodeId} always-copy=true ! videoconvert ! " +
            "video/x-raw,format=BGRx ! appsink name=sink max-buffers=1 drop=true sync=false";
        _pipeline = Gst.ParseLaunch(description);
        _sink = Gst.gst_bin_get_by_name(_pipeline, "sink");
        _bus = Gst.gst_element_get_bus(_pipeline);
        if (_sink == IntPtr.Zero || _bus == IntPtr.Zero)
        {
            Release();
            throw new InvalidOperationException("screencast pipeline is missing its appsink or bus");
        }
        if (Gst.gst_element_set_state(_pipeline, Gst.StatePlaying) == Gst.StateChangeFailure)
        {
            string why = Gst.PopFailure(_bus) ?? "state change to PLAYING failed";
            Release();
            throw new InvalidOperationException($"screencast pipeline for node {geometry.NodeId}: {why}");
        }
        _pump = new Thread(Pump) { IsBackground = true, Name = $"RoeSnip-ScreenCast-{geometry.NodeId}" };
        _pump.Start();
    }

    /// <summary>Waits until the first sample arrived (or the stream died). False on timeout.</summary>
    public bool WaitForFirstSample(TimeSpan timeout) => _firstSample.Wait(timeout) && !IsDead;

    /// <summary>READY disconnects pipewiresrc's pw_stream, so the compositor stops producing for the node
    /// (PAUSED/PLAYING toggles hung the state change). PLAYING reconnects it.</summary>
    public void SetActive(bool active)
    {
        if (_stop || _disposed != 0) return;
        if (!active) _idle = true;
        int rc = Gst.gst_element_set_state(_pipeline, active ? Gst.StatePlaying : Gst.StateReady);
        if (active) _idle = false;
        if (rc == Gst.StateChangeFailure && active) MarkDead(Gst.PopFailure(_bus) ?? "state change to PLAYING failed");
    }

    public bool WaitForFrameAfter(long sequence, TimeSpan timeout)
        => _frames.WaitAbove(sequence, timeout, () => IsDead || _stop) && !IsDead;

    private void Pump()
    {
        while (!_stop)
        {
            if (_idle) // a stopped appsink returns at once: do not spin
            {
                Thread.Sleep(20);
                continue;
            }
            IntPtr sample;
            try
            {
                sample = Gst.gst_app_sink_try_pull_sample(_sink, 100_000_000); // 100 ms
            }
            catch (Exception ex)
            {
                MarkDead("appsink pull threw: " + ex.Message);
                return;
            }

            if (sample != IntPtr.Zero)
            {
                IntPtr old;
                lock (_gate)
                {
                    old = _latest;
                    _latest = sample;
                }
                _frames.Bump();
                if (old != IntPtr.Zero) Gst.gst_mini_object_unref(old);
                _firstSample.Set();
                continue;
            }

            string? failure = Gst.PopFailure(_bus);
            if (failure is null && Gst.gst_app_sink_is_eos(_sink) != 0)
            {
                failure = "end of stream";
            }
            if (failure is not null)
            {
                MarkDead(failure);
                return;
            }
        }
    }

    private void MarkDead(string reason)
    {
        if (_stop) return;
        _deadReason = reason;
        FileLog.Write($"RoeSnip: screencast stream node {Geometry.NodeId} stopped: {reason}");
        _firstSample.Set();
    }

    /// <summary>Copies a <paramref name="outWidth"/> x <paramref name="outHeight"/> block of the newest
    /// frame into <paramref name="dest"/> (tight BGRA, alpha forced to 255). <paramref name="srcLeft"/>,
    /// <paramref name="srcTop"/> and the out size are in monitor pixels (<paramref name="monitorWidth"/>
    /// x <paramref name="monitorHeight"/>); when the stream's buffer has another size the copy
    /// nearest-samples. False when there is no frame yet or the buffer cannot be read.</summary>
    public bool TryCopy(int srcLeft, int srcTop, int outWidth, int outHeight,
        int monitorWidth, int monitorHeight, byte[] dest, out long sequence, out string? sizeNote)
    {
        sequence = 0;
        sizeNote = null;
        lock (_gate)
        {
            if (_latest == IntPtr.Zero) return false;
            sequence = _frames.Sequence;

            IntPtr caps = Gst.gst_sample_get_caps(_latest);
            IntPtr buffer = Gst.gst_sample_get_buffer(_latest);
            if (caps == IntPtr.Zero || buffer == IntPtr.Zero) return false;
            IntPtr st = Gst.gst_caps_get_structure(caps, 0);
            if (st == IntPtr.Zero
                || Gst.gst_structure_get_int(st, "width", out int fw) == 0
                || Gst.gst_structure_get_int(st, "height", out int fh) == 0
                || fw <= 0 || fh <= 0)
            {
                return false;
            }

            Gst.MapInfo map;
            if (Gst.gst_buffer_map(buffer, &map, Gst.MapRead) == 0) return false;
            try
            {
                int stride = (int)((long)map.Size / fh);
                if (stride < fw * 4) return false;
                byte* src = (byte*)map.Data;
                int outStride = outWidth * 4;

                fixed (byte* dst0 = dest)
                {
                    if (fw == monitorWidth && fh == monitorHeight)
                    {
                        int left = Math.Clamp(srcLeft, 0, Math.Max(0, fw - outWidth));
                        int top = Math.Clamp(srcTop, 0, Math.Max(0, fh - outHeight));
                        int w = Math.Min(outWidth, fw - left);
                        int h = Math.Min(outHeight, fh - top);
                        for (int y = 0; y < h; y++)
                        {
                            Buffer.MemoryCopy(src + (long)(top + y) * stride + left * 4,
                                dst0 + (long)y * outStride, outStride, w * 4);
                        }
                    }
                    else
                    {
                        sizeNote = $"stream buffer {fw}x{fh} != monitor {monitorWidth}x{monitorHeight}, nearest-sampling";
                        double sx = (double)fw / monitorWidth, sy = (double)fh / monitorHeight;
                        for (int y = 0; y < outHeight; y++)
                        {
                            int fy = Math.Min(fh - 1, (int)((srcTop + y) * sy));
                            uint* srow = (uint*)(src + (long)fy * stride);
                            uint* drow = (uint*)(dst0 + (long)y * outStride);
                            for (int x = 0; x < outWidth; x++)
                            {
                                drow[x] = srow[Math.Min(fw - 1, (int)((srcLeft + x) * sx))];
                            }
                        }
                    }

                    uint* px = (uint*)dst0;
                    long count = (long)outWidth * outHeight;
                    for (long i = 0; i < count; i++) px[i] |= 0xFF000000u; // BGRx: X byte is undefined
                }
                return true;
            }
            finally
            {
                Gst.gst_buffer_unmap(buffer, &map);
            }
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _stop = true;
        _pump.Join(TimeSpan.FromSeconds(2));
        Release();
    }

    private void Release()
    {
        Gst.Shutdown(_pipeline);
        if (_sink != IntPtr.Zero) Gst.gst_object_unref(_sink);
        if (_bus != IntPtr.Zero) Gst.gst_object_unref(_bus);
        lock (_gate)
        {
            if (_latest != IntPtr.Zero)
            {
                Gst.gst_mini_object_unref(_latest);
                _latest = IntPtr.Zero;
            }
        }
    }
}
