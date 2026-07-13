using System;
using System.Threading.Channels;
using RoeSnip.Core.Capture;
using RoeSnip.Core.Recording;

namespace RoeSnip.App.Recording;

/// <summary>Continuous, region-cropped capture for one monitor during a recording take - the
/// RoeSnip.App counterpart of the WPF app's own <c>RoeSnip.Recording.RegionRecorder</c>. This class
/// itself is thin and fully portable (it compiles and runs on every RoeSnip.App target OS): the
/// actual capture work is delegated to whichever <see cref="IRegionCaptureSource"/>
/// <see cref="RegionCaptureSourceRegistry"/> selects for the current OS - a Windows 3-slot D3D11
/// staging-ring session (RoeSnip.Platform.Windows.WindowsRegionCaptureSource) or, on every other OS,
/// the portable polling fallback (RoeSnip.Core.Recording.PolledRegionCaptureSource). Keeping the
/// selection behind a Core registry (the same pattern RoeSnip.Core.Capture.CaptureBackendRegistry
/// already established) is what lets this file live in RoeSnip.App/Recording with no
/// <c>#if WINDOWS</c> of its own, even though RoeSnip.App multi-targets a plain net8.0 TFM that
/// cannot reference RoeSnip.Platform.Windows at all.</summary>
internal sealed class RegionRecorder : IDisposable
{
    private readonly IRegionCaptureSource _source;

    /// <summary>Set/cleared by RecordingSession.Pause()/Resume() on the UI thread - forwards
    /// straight to the underlying source (see <see cref="IRegionCaptureSource.Paused"/>'s own doc
    /// comment for why frames dropping pre-readback during a pause is what makes the single
    /// accumulating pause clock correct).</summary>
    public bool Paused
    {
        get => _source.Paused;
        set => _source.Paused = value;
    }

    /// <summary>Raised at most once if capture fails mid-take - forwarded from the underlying
    /// source.</summary>
    public event Action<Exception>? Faulted
    {
        add => _source.Faulted += value;
        remove => _source.Faulted -= value;
    }

    public RegionRecorder(MonitorInfo monitor, RectPhysical selectionPx, int targetFps)
    {
        _source = RegionCaptureSourceRegistry.Create(monitor, selectionPx, targetFps);
    }

    public ChannelReader<RegionCaptureFrame> Frames => _source.Frames;

    /// <summary>Slides the crop origin while recording (the user dragged the region) - only the
    /// position changes, never width/height. Lock-free; safe to call from the UI thread mid-drag.</summary>
    public void SetOrigin(int left, int top) => _source.SetOrigin(left, top);

    public void Start() => _source.Start();

    public void Stop() => _source.Stop();

    public void Dispose() => _source.Dispose();
}
