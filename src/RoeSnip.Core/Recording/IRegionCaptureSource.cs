using System;
using System.Threading.Channels;
using RoeSnip.Core.Capture;

namespace RoeSnip.Core.Recording;

/// <summary>One capture-cropped frame delivered from an <see cref="IRegionCaptureSource"/>, alongside
/// the tick timestamp its copy was ISSUED at - not necessarily when it was actually read back off the
/// GPU (see <see cref="RoeSnip.Platform.Windows.WindowsRegionCaptureSource"/>'s own doc comment, in
/// RoeSnip.Platform.Windows, for why that distinction matters for the ring's own stall-avoidance).
/// Mirrors the WPF app's own internal <c>QueuedFrame</c> (Recording/RegionRecorder.cs) - public here
/// because the producer (a Platform.* assembly) and the consumer (RoeSnip.App's own RegionRecorder)
/// live in different assemblies.</summary>
public readonly record struct RegionCaptureFrame(CapturedFrame Frame, long TimestampTicks);

/// <summary>The per-OS entry point for CONTINUOUS, region-cropped capture during a recording take -
/// mirrors <see cref="RoeSnip.Core.Capture.ICaptureBackend"/>'s per-OS-entry-point shape, but for a
/// PERSISTENT capture session (built once per take, torn down once at Stop) instead of a one-shot
/// CaptureAll. RoeSnip.App's own RegionRecorder (item 20) is a thin wrapper around whichever
/// implementation <see cref="RegionCaptureSourceRegistry"/> selects for the current OS:
///   - Windows: RoeSnip.Platform.Windows.WindowsRegionCaptureSource - a genuine WGC persistent
///     session reading back through a 3-slot D3D11 staging ring (see that class's own doc comment
///     for why a ring, not a single reused staging texture, is required at recording cadence).
///   - Every other OS (no Platform.* registrant): <see cref="PolledRegionCaptureSource"/> (this
///     assembly) - polls ICaptureBackend.CaptureAll on the SAME schedule-throttle cadence
///     (<see cref="RecordingSchedule"/>) and crops the result in managed code. Correct, but pays a
///     full one-shot capture-backend round trip per accepted frame instead of a persistent low-
///     latency GPU session - a documented, accepted perf limitation (docs/PARITY.md item 20), not a
///     functional gap.
/// Both kinds of implementation share the exact throttle math so a take's effective framerate does
/// not depend on which backend produced it.</summary>
public interface IRegionCaptureSource : IDisposable
{
    /// <summary>Bounded (DropOldest) channel of ready-to-encode raw frames - never tone-mapped here;
    /// see RecordingController's own encoder-thread ownership of that step.</summary>
    ChannelReader<RegionCaptureFrame> Frames { get; }

    /// <summary>Set/cleared by RecordingSession.Pause()/Resume() on the UI thread. While true, no
    /// frame enters <see cref="Frames"/> - frames drop pre-readback, which is what makes
    /// RecordingController's single accumulating pause-clock (<c>_pausedTicks</c>) correct: nothing
    /// captured during a pause ever needs its timestamp corrected, only the GAP the pause left
    /// behind in later frames' timestamps does.</summary>
    bool Paused { get; set; }

    /// <summary>Raised at most once if capture fails mid-take (TDR, monitor unplug, a portal
    /// disconnect) - the producing thread catches its own exception and hands the failure here
    /// instead of crashing the process.</summary>
    event Action<Exception>? Faulted;

    /// <summary>Starts the persistent capture. Never disposed until <see cref="Stop"/> - that
    /// permanence is exactly what recording needs, unlike ICaptureBackend's ephemeral per-shot
    /// captures.</summary>
    void Start();

    /// <summary>Slides the crop origin while recording (the user dragged the region) - width/height
    /// stay fixed for the whole take, only position moves. Clamped to the monitor internally.</summary>
    void SetOrigin(int left, int top);

    /// <summary>Stops producing frames and completes <see cref="Frames"/>. Idempotent.</summary>
    void Stop();
}

/// <summary>Selects the <see cref="IRegionCaptureSource"/> implementation for the OS actually
/// running - same registration/selection shape as <c>RoeSnip.Core.Capture.CaptureBackendRegistry</c>.
/// Unlike that registry, <see cref="Create"/> never throws: <see cref="PolledRegionCaptureSource"/>
/// is always a valid answer, so an OS with no platform-specific registrant simply gets the portable
/// fallback rather than an error.</summary>
public static class RegionCaptureSourceRegistry
{
    private static readonly List<(Func<bool> IsSupported, Func<MonitorInfo, RectPhysical, int, IRegionCaptureSource> Factory)> _candidates = new();

    /// <summary>Called by each Platform.* project's own [ModuleInitializer]. Order of registration
    /// across assemblies is unspecified (same caveat as CaptureBackendRegistry.Register's own doc) -
    /// safe here because selection filters by IsSupported(), not by registration order.</summary>
    public static void Register(Func<bool> isSupported, Func<MonitorInfo, RectPhysical, int, IRegionCaptureSource> factory)
        => _candidates.Add((isSupported, factory));

    /// <param name="selectionMonitorRelativePx">The recorded crop rect, relative to
    /// <paramref name="monitor"/>.BoundsPx's own origin - the SAME convention
    /// RoeSnip.Core.Capture.ICaptureBackend's own frames use.</param>
    public static IRegionCaptureSource Create(MonitorInfo monitor, RectPhysical selectionMonitorRelativePx, int targetFps)
    {
        foreach (var (isSupported, factory) in _candidates)
        {
            if (isSupported())
            {
                return factory(monitor, selectionMonitorRelativePx, targetFps);
            }
        }
        return new PolledRegionCaptureSource(monitor, selectionMonitorRelativePx, targetFps);
    }
}
