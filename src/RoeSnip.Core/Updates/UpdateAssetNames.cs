using System;
using System.Runtime.InteropServices;

namespace RoeSnip.Core.Updates;

/// <summary>The OS families the cross-platform app self-updates on.</summary>
public enum UpdatePlatform
{
    Windows,
    Linux,
    MacOS,
}

/// <summary>The release asset one install downloads. <see cref="Gz"/> is the optional gzip transit
/// sibling (Windows only); <see cref="Primary"/> is always published.</summary>
public sealed record UpdateAssetSelection(string Primary, string? Gz);

/// <summary>Release asset names per OS and arch for the cross-platform app (RoeSnip.App). These
/// strings must match release.yml exactly and never equal the WPF app's "RoeSnip.exe" - a collision
/// would let one product's updater swap in the other's binary.</summary>
public static class UpdateAssetNames
{
    /// <summary>"x64" / "arm64" for the archs release.yml ships, null otherwise.</summary>
    public static string? ArchToken(Architecture arch) => arch switch
    {
        Architecture.X64 => "x64",
        Architecture.Arm64 => "arm64",
        _ => null,
    };

    /// <summary>The asset this OS/arch self-updates from, or null when no build ships for it.</summary>
    public static UpdateAssetSelection? For(UpdatePlatform platform, Architecture arch)
    {
        string? a = ArchToken(arch);
        if (a is null)
        {
            return null;
        }

        return platform switch
        {
            UpdatePlatform.Windows => new UpdateAssetSelection($"RoeSnipApp-win-{a}.exe", $"RoeSnipApp-win-{a}.exe.gz"),
            UpdatePlatform.Linux => new UpdateAssetSelection($"RoeSnip-linux-{a}.AppImage", null),
            UpdatePlatform.MacOS => new UpdateAssetSelection($"RoeSnip-osx-{a}.app.tar.gz", null),
            _ => null,
        };
    }

    /// <summary>The running OS, or null on an OS with no shipped build.</summary>
    public static UpdatePlatform? CurrentPlatform =>
        OperatingSystem.IsWindows() ? UpdatePlatform.Windows
        : OperatingSystem.IsMacOS() ? UpdatePlatform.MacOS
        : OperatingSystem.IsLinux() ? UpdatePlatform.Linux
        : null;

    /// <summary>The asset for this process (OS + <see cref="RuntimeInformation.ProcessArchitecture"/>).
    /// An x64 build under emulation on ARM reports X64 and keeps updating the x64 asset.</summary>
    public static UpdateAssetSelection? ForCurrentProcess() =>
        CurrentPlatform is UpdatePlatform p ? For(p, RuntimeInformation.ProcessArchitecture) : null;
}
