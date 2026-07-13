namespace RoeSnip.Core.Recording;

/// <summary>What this OS's recording backend can actually do — mirrors the capability-flag pattern
/// <c>ICaptureBackend.SupportsHdrExport</c> already established (RoeSnip.Core.Capture). Item 21's
/// recording chrome uses these three flags to DISABLE (never hide) an MP4/microphone/loopback toggle
/// it can't honor, with a caption explaining why, the same "considered, not absent" treatment
/// SupportsHdrExport already gets. GIF recording itself needs no flag here —
/// <see cref="GifVideoEncoder"/> has zero platform dependency, so it is unconditionally available on
/// every OS.</summary>
public sealed record RecordingCapabilities(bool SupportsMp4, bool SupportsMicrophone, bool SupportsLoopback)
{
    /// <summary>The Linux/macOS answer until those Platform.* projects grow a real MP4/WASAPI-
    /// equivalent backend (PipeWire/CoreAudio, or an ffmpeg encoder) — none of which this item
    /// scaffolds speculatively (per this repo's minimal-fixes convention). Recording on those OSes
    /// degrades to GIF-only, silent takes; this is that degrade's own descriptor, not an error.</summary>
    public static readonly RecordingCapabilities None = new(SupportsMp4: false, SupportsMicrophone: false, SupportsLoopback: false);
}

/// <summary>Selects the one <see cref="RecordingCapabilities"/> descriptor for the OS actually
/// running — same registration/selection shape as <c>RoeSnip.Core.Capture.CaptureBackendRegistry</c>
/// (see that class's own doc comment for why a design-time no-RID build can have every Platform.*
/// assembly loaded yet only one candidate ever report IsSupported() == true). Unlike
/// CaptureBackendRegistry, <see cref="ForCurrentPlatform"/> never throws when nothing matches:
/// <see cref="RecordingCapabilities.None"/> IS the correct answer for an OS with no registrant yet
/// (the documented GIF-only degrade), not an error condition.</summary>
public static class RecordingCapabilitiesRegistry
{
    private static readonly List<(Func<bool> IsSupported, Func<RecordingCapabilities> Factory)> _candidates = new();

    /// <summary>Called by each Platform.* project's own [ModuleInitializer]. Order of registration
    /// across assemblies is unspecified (same caveat as CaptureBackendRegistry.Register's own doc) —
    /// safe here because selection filters by IsSupported(), not by registration order.</summary>
    public static void Register(Func<bool> isSupported, Func<RecordingCapabilities> factory)
        => _candidates.Add((isSupported, factory));

    public static RecordingCapabilities ForCurrentPlatform()
    {
        foreach (var (isSupported, factory) in _candidates)
        {
            if (isSupported()) return factory();
        }
        return RecordingCapabilities.None;
    }
}
