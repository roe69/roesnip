using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using Vortice.MediaFoundation;
using Vortice.Multimedia;

namespace RoeSnip.Recording;

/// <summary>One mixed PCM chunk from <see cref="AudioCaptureEngine"/>: 48 kHz stereo 16-bit
/// samples (see Mp4Encoder's Audio* constants) and the Stopwatch/QPC timestamp of the chunk's
/// FIRST sample, which RecordingSession maps onto the video timeline.</summary>
internal readonly record struct AudioChunk(byte[] Pcm, long QpcTicks);

/// <summary>WASAPI capture engine for MP4 recording audio: microphone (default capture endpoint)
/// and/or system audio (default render endpoint in loopback mode), both converted by WASAPI
/// itself (AUTOCONVERTPCM) to one shared 48 kHz stereo 16-bit format, gap-filled with silence so
/// each source is a continuous timeline from engine start, and mixed into a single chunk stream
/// when both are on.
///
/// Vortice.MediaFoundation 3.8.3 does not expose IAudioClient/IAudioCaptureClient, so this class
/// hand-rolls minimal vtable wrappers (AudioClient/AudioCaptureClient below) over the raw COM
/// pointer IMMDevice.Activate returns. Everything WASAPI-related -- enumerator, device activation,
/// Initialize, Start, and every poll -- happens on ONE thread (the capture thread) rather than
/// split across the caller and a worker: IAudioClient/IAudioCaptureClient are not documented as
/// agile, so creating them on the UI thread and calling them from a background thread would be a
/// cross-apartment hazard for no benefit. TryStart blocks (bounded) until that thread finishes
/// device setup so it can still report success/failure synchronously.</summary>
internal sealed class AudioCaptureEngine : IDisposable
{
    // Single source of truth for the PCM format lives on Mp4Encoder; alias rather than re-derive.
    private const int SampleRate = Mp4Encoder.AudioSampleRate;
    private const int BitsPerSample = Mp4Encoder.AudioBitsPerSample;
    private const int Channels = Mp4Encoder.AudioChannels;
    private const int BlockAlign = Mp4Encoder.AudioBlockAlign;

    private const uint StreamFlagAutoConvertPcm = unchecked((uint)AudioClientStreamFlags.AutoConvertPCM);
    private const uint StreamFlagLoopback = (uint)AudioClientStreamFlags.LoopBack;
    // AUDCLNT_STREAMFLAGS_SRCDEFAULTQUALITY -- not in Vortice.Multimedia's enum, commonly OR'd
    // alongside AUTOCONVERTPCM so WASAPI's built-in resampler favors quality over speed.
    private const uint StreamFlagSrcDefaultQuality = 0x08000000;
    private const uint BufferFlagsSilent = 0x2; // AUDCLNT_BUFFERFLAGS_SILENT
    private const int ClsCtxInprocServer = 1;
    private const long BufferDuration100ns = 1_000_000; // 100 ms shared-mode endpoint buffer
    private const int PollIntervalMs = 10;
    private const int GapToleranceMs = 40; // per spec: small tolerance before padding with silence
    private const long MaxCatchUpSamplesPerPoll = SampleRate / 2; // cap a single poll's silence burst to 500 ms

    private static readonly Guid IidAudioClient = new("1CB9AD4C-DBFA-4C32-B178-C2F568A703B2");
    private static readonly Guid IidAudioCaptureClient = new("C8ADBD64-E71E-48A0-A4DE-185C395CD317");

    [DllImport("ole32.dll")]
    private static extern int CoInitializeEx(IntPtr pvReserved, uint dwCoInit);

    [DllImport("ole32.dll")]
    private static extern void CoUninitialize();

    private readonly ConcurrentQueue<AudioChunk> _queue = new();
    private readonly Thread _thread;
    private readonly ManualResetEventSlim _ready = new(false);
    private volatile bool _stopRequested;
    private volatile string? _startupError;
    private long _emittedSamples;

    private AudioCaptureEngine(bool microphone, bool systemAudio)
    {
        _thread = new Thread(() => RunCaptureLoop(microphone, systemAudio))
        {
            IsBackground = true,
            Name = "RoeSnip-Recording-Audio",
        };
        _thread.Start();
    }

    /// <summary>UI thread. Blocks (bounded) until the capture thread has activated and started
    /// whichever WASAPI endpoints were requested, so the caller learns synchronously whether audio
    /// is actually available -- matching RecordingSession.Start's own synchronous, clean-up-on-
    /// failure contract. Returns null when both flags are false (nothing to capture) or on any
    /// device/COM failure (logged here; also see the capture thread's own mid-capture handler for
    /// failures after this point).</summary>
    public static AudioCaptureEngine? TryStart(bool microphone, bool systemAudio)
    {
        if (!microphone && !systemAudio)
        {
            return null;
        }

        var engine = new AudioCaptureEngine(microphone, systemAudio);
        bool signaled = engine._ready.Wait(5000); // device activation is normally sub-100 ms
        if (!signaled || engine._startupError is not null)
        {
            string reason = signaled ? engine._startupError! : "timed out waiting for the capture thread";
            Console.Error.WriteLine($"RoeSnip: audio capture unavailable: {reason}");
            engine.Dispose();
            return null;
        }
        return engine;
    }

    /// <summary>Encoder thread.</summary>
    public bool TryDequeue(out AudioChunk chunk) => _queue.TryDequeue(out chunk);

    /// <summary>UI thread; idempotent. Stops the queue from growing further -- the capture thread
    /// notices on its next poll and exits after one final drain.</summary>
    public void Stop() => _stopRequested = true;

    /// <summary>Idempotent, safe after Stop(). Joins the capture thread (bounded, so a wedged
    /// device can never hang the caller forever) which owns every WASAPI object's teardown.</summary>
    public void Dispose()
    {
        _stopRequested = true;
        if (_thread.IsAlive)
        {
            _thread.Join(2000);
        }
        // Only dispose the event once the thread has actually exited (Join returned because the
        // thread function returned, not because of the timeout): a still-running startup on a
        // wedged device could call _ready.Set() after this Dispose() call, and Set() on an
        // already-disposed ManualResetEventSlim throws -- unhandled on a background thread, which
        // still crashes the process. Bounded leak of one event handle in that pathological case
        // beats a crash.
        if (!_thread.IsAlive)
        {
            _ready.Dispose();
        }
    }

    private void RunCaptureLoop(bool microphone, bool systemAudio)
    {
        // MTA: this thread only ever calls in-process WASAPI objects it creates and owns itself,
        // no message pump needed. RPC_E_CHANGED_MODE (thread already in a different apartment) is
        // non-fatal -- COM is still usable, but CoUninitialize must be skipped since this call did
        // not actually take ownership of the apartment.
        int coHr = CoInitializeEx(IntPtr.Zero, 0 /*COINIT_MULTITHREADED*/);
        bool comInitialized = coHr >= 0;

        Source? mic = null;
        Source? system = null;
        long engineStartQpc;
        try
        {
            using (var enumerator = new IMMDeviceEnumerator())
            {
                if (microphone)
                {
                    mic = Source.Create(enumerator, DataFlow.Capture, loopback: false, "microphone");
                }
                if (systemAudio)
                {
                    system = Source.Create(enumerator, DataFlow.Render, loopback: true, "system audio");
                }
            }
            mic?.Start();
            system?.Start();
            engineStartQpc = Stopwatch.GetTimestamp();
        }
        catch (Exception ex)
        {
            _startupError = ex.Message;
            mic?.Dispose();
            system?.Dispose();
            if (comInitialized)
            {
                CoUninitialize();
            }
            _ready.Set();
            return;
        }

        _ready.Set();

        try
        {
            while (!_stopRequested)
            {
                long nowQpc = Stopwatch.GetTimestamp();
                mic?.Poll(nowQpc, engineStartQpc);
                system?.Poll(nowQpc, engineStartQpc);
                EmitMixedChunks(mic, system, engineStartQpc);
                Thread.Sleep(PollIntervalMs);
            }
            // Final drain so audio right up to the stop point isn't lost.
            long finalQpc = Stopwatch.GetTimestamp();
            mic?.Poll(finalQpc, engineStartQpc);
            system?.Poll(finalQpc, engineStartQpc);
            EmitMixedChunks(mic, system, engineStartQpc);
        }
        catch (Exception ex)
        {
            // Never throw across the thread boundary -- log once and let the queue simply stop
            // growing; RecordingController already treats a drained-but-silent audio engine fine.
            Console.Error.WriteLine($"RoeSnip: audio capture stopped unexpectedly: {ex.Message}");
        }
        finally
        {
            mic?.Dispose();
            system?.Dispose();
            if (comInitialized)
            {
                CoUninitialize();
            }
        }
    }

    /// <summary>Both sources on: saturating-add mix, emitting only up to whichever timeline is
    /// shorter so both stay sample-aligned. One source on: straight passthrough of its continuous
    /// timeline. Coalesces a whole poll's worth of packets into one chunk (10-40 ms typical).</summary>
    private void EmitMixedChunks(Source? mic, Source? system, long engineStartQpc)
    {
        if (mic is not null && system is not null)
        {
            int n = Math.Min(mic.AvailableBytes, system.AvailableBytes);
            n -= n % BlockAlign;
            if (n <= 0)
            {
                return;
            }
            byte[] mixed = new byte[n];
            MixInto(mixed, mic.Peek(n), system.Peek(n));
            long chunkStartSample = _emittedSamples;
            mic.Consume(n);
            system.Consume(n);
            _emittedSamples += n / BlockAlign;
            Enqueue(mixed, chunkStartSample, engineStartQpc);
        }
        else
        {
            Source? single = mic ?? system;
            if (single is null)
            {
                return;
            }
            int n = single.AvailableBytes;
            n -= n % BlockAlign;
            if (n <= 0)
            {
                return;
            }
            byte[] data = single.Peek(n).ToArray();
            long chunkStartSample = _emittedSamples;
            single.Consume(n);
            _emittedSamples += n / BlockAlign;
            Enqueue(data, chunkStartSample, engineStartQpc);
        }
    }

    private void Enqueue(byte[] pcm, long chunkStartSample, long engineStartQpc)
    {
        long qpc = engineStartQpc + (long)(chunkStartSample / (double)SampleRate * Stopwatch.Frequency);
        _queue.Enqueue(new AudioChunk(pcm, qpc));
    }

    private static void MixInto(byte[] dest, ReadOnlySpan<byte> a, ReadOnlySpan<byte> b)
    {
        Span<short> destSamples = MemoryMarshal.Cast<byte, short>(dest.AsSpan());
        ReadOnlySpan<short> aSamples = MemoryMarshal.Cast<byte, short>(a);
        ReadOnlySpan<short> bSamples = MemoryMarshal.Cast<byte, short>(b);
        for (int i = 0; i < destSamples.Length; i++)
        {
            int sum = aSamples[i] + bSamples[i];
            destSamples[i] = (short)Math.Clamp(sum, short.MinValue, short.MaxValue);
        }
    }

    /// <summary>One WASAPI endpoint (microphone capture or loopback render), converted to the
    /// shared target format and gap-filled into a continuous sample timeline. All members are
    /// capture-thread-only -- no locking, since only AudioCaptureEngine's single background thread
    /// ever touches a Source.</summary>
    private sealed class Source : IDisposable
    {
        private readonly AudioClient _client;
        private readonly AudioCaptureClient _captureClient;
        private readonly string _label;
        // Growable byte buffer for this source's continuous timeline: [0, _consumed) already
        // emitted into chunks, [_consumed, Count) pending. Compacted back to the front whenever
        // something is consumed -- in steady state the mixer drains nearly everything every poll,
        // so this stays tiny.
        private readonly List<byte> _pending = new();
        private int _consumed;
        private bool _started;

        private Source(AudioClient client, AudioCaptureClient captureClient, string label)
        {
            _client = client;
            _captureClient = captureClient;
            _label = label;
        }

        public long DeliveredSamples { get; private set; }

        public int AvailableBytes => _pending.Count - _consumed;

        public static Source Create(IMMDeviceEnumerator enumerator, DataFlow flow, bool loopback, string label)
        {
            using var device = enumerator.GetDefaultAudioEndpoint(flow, Role.Multimedia);

            var activateResult = device.Activate(IidAudioClient, ClsCtxInprocServer, null, out IntPtr audioClientPtr);
            activateResult.CheckError();

            var client = new AudioClient(audioClientPtr);
            try
            {
                var targetFormat = new WaveFormat(SampleRate, BitsPerSample, Channels);
                IntPtr formatPtr;
                unsafe
                {
                    formatPtr = (IntPtr)WaveFormat.MarshalToPtr(targetFormat);
                }
                try
                {
                    uint streamFlags = StreamFlagAutoConvertPcm | StreamFlagSrcDefaultQuality;
                    if (loopback)
                    {
                        streamFlags |= StreamFlagLoopback;
                    }
                    int initHr = client.Initialize(
                        (int)AudioClientShareMode.Shared, streamFlags, BufferDuration100ns, 0, formatPtr, IntPtr.Zero);
                    if (initHr != 0)
                    {
                        throw new InvalidOperationException($"{label}: IAudioClient.Initialize failed (0x{initHr:X8})");
                    }
                }
                finally
                {
                    // Must pair with WaveFormat.MarshalToPtr's actual allocator: Vortice allocates
                    // via NativeMemory.Alloc (malloc heap); FreeHGlobal would release through the
                    // Win32 LocalFree API on a DIFFERENT heap, which is undefined behavior and can
                    // corrupt the process heap.
                    Vortice.UnsafeUtilities.Free(formatPtr);
                }

                int svcHr = client.GetService(IidAudioCaptureClient, out IntPtr captureClientPtr);
                if (svcHr != 0)
                {
                    throw new InvalidOperationException($"{label}: GetService(IAudioCaptureClient) failed (0x{svcHr:X8})");
                }

                return new Source(client, new AudioCaptureClient(captureClientPtr), label);
            }
            catch
            {
                client.Dispose();
                throw;
            }
        }

        public void Start()
        {
            int hr = _client.Start();
            if (hr != 0)
            {
                throw new InvalidOperationException($"{_label}: IAudioClient.Start failed (0x{hr:X8})");
            }
            _started = true;
        }

        /// <summary>Drains every packet WASAPI actually has, then pads with silence up to the
        /// expected sample count for elapsed wall-clock time (beyond a small tolerance) so this
        /// source's timeline never falls behind -- loopback in particular yields nothing while no
        /// audio plays.</summary>
        public void Poll(long nowQpc, long engineStartQpc)
        {
            while (true)
            {
                int npsHr = _captureClient.GetNextPacketSize(out uint packetFrames);
                if (npsHr != 0)
                {
                    throw new InvalidOperationException($"{_label}: GetNextPacketSize failed (0x{npsHr:X8})");
                }
                if (packetFrames == 0)
                {
                    break;
                }

                int gbHr = _captureClient.GetBuffer(out IntPtr dataPtr, out uint numFrames, out uint flags);
                if (gbHr != 0)
                {
                    throw new InvalidOperationException($"{_label}: GetBuffer failed (0x{gbHr:X8})");
                }

                int byteCount = checked((int)numFrames * BlockAlign);
                if (byteCount > 0)
                {
                    if ((flags & BufferFlagsSilent) != 0 || dataPtr == IntPtr.Zero)
                    {
                        AppendSilence(byteCount);
                    }
                    else
                    {
                        unsafe
                        {
                            Append(new ReadOnlySpan<byte>((void*)dataPtr, byteCount));
                        }
                    }
                    DeliveredSamples += numFrames;
                }

                int relHr = _captureClient.ReleaseBuffer(numFrames);
                if (relHr != 0)
                {
                    throw new InvalidOperationException($"{_label}: ReleaseBuffer failed (0x{relHr:X8})");
                }
            }

            double elapsedSeconds = (nowQpc - engineStartQpc) / (double)Stopwatch.Frequency;
            long expectedSamples = (long)(elapsedSeconds * SampleRate);
            long toleranceSamples = GapToleranceMs * (long)SampleRate / 1000;
            if (expectedSamples - DeliveredSamples > toleranceSamples)
            {
                long fillSamples = Math.Min(expectedSamples - DeliveredSamples, MaxCatchUpSamplesPerPoll);
                AppendSilence(checked((int)(fillSamples * BlockAlign)));
                DeliveredSamples += fillSamples;
            }
        }

        public ReadOnlySpan<byte> Peek(int count) => CollectionsMarshal.AsSpan(_pending).Slice(_consumed, count);

        public void Consume(int count)
        {
            _consumed += count;
            int remaining = _pending.Count - _consumed;
            if (remaining > 0)
            {
                CollectionsMarshal.AsSpan(_pending).Slice(_consumed).CopyTo(CollectionsMarshal.AsSpan(_pending));
            }
            CollectionsMarshal.SetCount(_pending, remaining);
            _consumed = 0;
        }

        private void Append(ReadOnlySpan<byte> data)
        {
            int oldCount = _pending.Count;
            CollectionsMarshal.SetCount(_pending, oldCount + data.Length);
            data.CopyTo(CollectionsMarshal.AsSpan(_pending).Slice(oldCount));
        }

        private void AppendSilence(int byteCount)
        {
            int oldCount = _pending.Count;
            CollectionsMarshal.SetCount(_pending, oldCount + byteCount);
            CollectionsMarshal.AsSpan(_pending).Slice(oldCount, byteCount).Clear();
        }

        public void Dispose()
        {
            try
            {
                if (_started)
                {
                    _client.Stop();
                }
            }
            catch
            {
                /* best-effort; the process is tearing this source down regardless */
            }
            _captureClient.Dispose();
            _client.Dispose();
        }
    }

    /// <summary>Minimal IAudioClient vtable wrapper -- Vortice.MediaFoundation 3.8.3 does not
    /// expose this interface. IUnknown occupies slots 0-2; slot numbers below are IAudioClient's
    /// own layout (Initialize=3 ... GetService=14), verified against the real DLL and a live
    /// device (see the recording-audio smoke test).</summary>
    private sealed unsafe class AudioClient : SharpGen.Runtime.ComObject
    {
        public AudioClient(IntPtr ptr) : base(ptr)
        {
        }

        public int Initialize(int shareMode, uint streamFlags, long bufferDuration100ns, long periodicity, IntPtr format, IntPtr audioSessionGuid)
            => ((delegate* unmanaged[Stdcall]<IntPtr, int, uint, long, long, IntPtr, IntPtr, int>)this[3])(
                NativePointer, shareMode, streamFlags, bufferDuration100ns, periodicity, format, audioSessionGuid);

        public int Start() => ((delegate* unmanaged[Stdcall]<IntPtr, int>)this[10])(NativePointer);

        public int Stop() => ((delegate* unmanaged[Stdcall]<IntPtr, int>)this[11])(NativePointer);

        public int GetService(Guid riid, out IntPtr ppv)
        {
            IntPtr local = IntPtr.Zero;
            int hr = ((delegate* unmanaged[Stdcall]<IntPtr, Guid*, IntPtr*, int>)this[14])(NativePointer, &riid, &local);
            ppv = local;
            return hr;
        }
    }

    /// <summary>Minimal IAudioCaptureClient vtable wrapper, same reasoning as AudioClient above.
    /// GetBuffer's device-position/QPC-position out params are omitted (pass null pointers) --
    /// this class computes its own timeline from Stopwatch/QPC rather than the device's clock.</summary>
    private sealed unsafe class AudioCaptureClient : SharpGen.Runtime.ComObject
    {
        public AudioCaptureClient(IntPtr ptr) : base(ptr)
        {
        }

        public int GetBuffer(out IntPtr data, out uint numFramesAvailable, out uint flags)
        {
            IntPtr localData = IntPtr.Zero;
            uint localFrames = 0;
            uint localFlags = 0;
            int hr = ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr*, uint*, uint*, IntPtr, IntPtr, int>)this[3])(
                NativePointer, &localData, &localFrames, &localFlags, IntPtr.Zero, IntPtr.Zero);
            data = localData;
            numFramesAvailable = localFrames;
            flags = localFlags;
            return hr;
        }

        public int ReleaseBuffer(uint numFramesWritten)
            => ((delegate* unmanaged[Stdcall]<IntPtr, uint, int>)this[4])(NativePointer, numFramesWritten);

        public int GetNextPacketSize(out uint numFramesInNextPacket)
        {
            uint local = 0;
            int hr = ((delegate* unmanaged[Stdcall]<IntPtr, uint*, int>)this[5])(NativePointer, &local);
            numFramesInNextPacket = local;
            return hr;
        }
    }
}
