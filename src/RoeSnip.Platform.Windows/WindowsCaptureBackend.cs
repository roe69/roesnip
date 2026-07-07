using System;
using RoeSnip.Core.Capture;

namespace RoeSnip.Platform.Windows;

/// <summary>The Windows <see cref="ICaptureBackend"/>: DXGI/DisplayConfig monitor enumeration
/// (<see cref="MonitorEnumerator"/>) plus the Desktop-Duplication-then-WGC capture chain, composed
/// into Core's reusable <see cref="FallbackCaptureBackend"/> exactly per PLAN-XPLAT.md §3.2 — the
/// fallback ordering, parallel capture, and the persisted "capturer broken here" memo
/// (<see cref="CaptureCache"/>, keyed "{DeviceName}::{capturerIndex}") are all Core behavior,
/// unchanged from the WPF app's CaptureService.</summary>
public sealed class WindowsCaptureBackend : ICaptureBackend
{
    private readonly FallbackCaptureBackend _inner;

    public WindowsCaptureBackend() : this(CaptureCache.Default) { }

    /// <summary>Cache-taking overload so tests can point at an isolated temp cache file instead of
    /// the real per-user capture-cache.json (same pattern as CaptureCache's own path-taking ctor).</summary>
    public WindowsCaptureBackend(CaptureCache cache)
    {
        _inner = new FallbackCaptureBackend(
            "Windows (Desktop Duplication/WGC)",
            supportsHdrExport: true,
            MonitorEnumerator.Enumerate,
            new IScreenCapturer[] { new DesktopDuplicationCapturer(), new WgcCapturer() },
            cache);
    }

    public string Name => _inner.Name;

    public bool SupportsHdrExport => _inner.SupportsHdrExport;

    public IReadOnlyList<MonitorInfo> EnumerateMonitors() => _inner.EnumerateMonitors();

    public IReadOnlyList<CapturedFrame> CaptureAll(
        IReadOnlyList<MonitorInfo>? monitors = null, int? onlyMonitorIndex = null)
        => _inner.CaptureAll(monitors, onlyMonitorIndex);
}

file static class ModuleInit
{
    // CA2255 warns against [ModuleInitializer] in a library; this exact usage is the seam
    // PLAN-XPLAT.md §2.3/§3.2 mandates (each Platform.* assembly self-registers its backend so
    // Core/App never name a concrete Platform.* type). The App shell force-loads the platform
    // assemblies at startup, which is what makes this initializer's timing deterministic.
#pragma warning disable CA2255
    [System.Runtime.CompilerServices.ModuleInitializer]
#pragma warning restore CA2255
    internal static void Init() => CaptureBackendRegistry.Register(
        () => OperatingSystem.IsWindows(), () => new WindowsCaptureBackend());
}
