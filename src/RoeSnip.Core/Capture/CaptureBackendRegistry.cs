namespace RoeSnip.Core.Capture;

/// <summary>Selects the one <see cref="ICaptureBackend"/> that applies to the OS actually running,
/// regardless of how many platform assemblies happen to be LOADED (see PLAN-XPLAT.md §1.7's
/// design-time-build note — a no-RID build references all three Platform.* projects, but exactly one
/// candidate ever reports <c>isSupported() == true</c> at runtime). Each Platform.* project registers
/// itself via a <c>[ModuleInitializer]</c>-attributed method in its own file, mirroring Phase 1's
/// AppComposition hook pattern (PLAN.md §2.4) — Core/App never references a concrete Platform.* type
/// by name.</summary>
public static class CaptureBackendRegistry
{
    private static readonly List<(Func<bool> IsSupported, Func<ICaptureBackend> Factory)> _candidates = new();

    /// <summary>Called by each Platform.* project's own [ModuleInitializer]. Order of registration
    /// across assemblies is unspecified (same caveat as PLAN.md §4's ModuleInitializer note) — this
    /// is safe here because selection filters by IsSupported(), not by registration order.</summary>
    public static void Register(Func<bool> isSupported, Func<ICaptureBackend> factory)
        => _candidates.Add((isSupported, factory));

    /// <summary>Returns the first registered candidate whose IsSupported() is true. Throws
    /// PlatformNotSupportedException if none match (e.g. Core.Tests running with zero Platform.*
    /// assemblies loaded and no fake registered — tests should register a fake backend directly
    /// instead of going through this registry at all).</summary>
    public static ICaptureBackend CreateForCurrentPlatform()
    {
        foreach (var (isSupported, factory) in _candidates)
        {
            if (isSupported()) return factory();
        }
        throw new PlatformNotSupportedException("No ICaptureBackend registered for this OS.");
    }
}
