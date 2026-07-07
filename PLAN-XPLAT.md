# RoeSnip Phase B — Cross-Platform Port Implementation Plan (PLAN-XPLAT.md)

*Authoritative execution plan for parallel implementer agents building the Avalonia cross-platform
port. Read DESIGN-XPLAT.md first — this document does not repeat its rationale, only the exact
contracts, file ownership, and instructions needed to build it. Where this plan and DESIGN-XPLAT.md
conflict, DESIGN-XPLAT.md wins for **behavior/architecture**; this plan wins for **file layout /
exact signatures / package versions**. Genuine gaps are called out in "Plan-time flags" at the
bottom — implementers must not silently invent behavior beyond what's written here or in
DESIGN-XPLAT.md/DESIGN.md.*

*This plan reuses PLAN.md's style (ground rules, verbatim contracts, disjoint work packages,
integration sequence) for the same reasons: several agents build this concurrently against a
written spec, not against each other's actual code, so precision here is what prevents integration
breakage.*

---

## 0. Ground rules for implementer agents

1. **The existing `src/RoeSnip/` WPF app and `tests/RoeSnip.Tests/` are FROZEN.** Phase B agents may
   read them (they are the behavior reference and the extraction source) but must never edit them.
   The WPF app keeps its own copies of Color/Capture/Imaging code this cycle — Core is an
   *extraction*, not a refactor of the WPF app (DESIGN-XPLAT.md "Strategy").
2. **File ownership is absolute**, same rule as PLAN.md §0.1: each work package (WP-X1..X5) may only
   create/edit files listed under it in §3. If you believe you need to touch a file owned by
   another package, stop and use the seam defined for you (§2 contracts, the `CaptureBackendRegistry`
   in §2.3, or `AppComposition` in §2.8).
3. **Code against the contracts in §2 verbatim.** Do not rename types/members, change signatures, or
   "improve" them unilaterally. If a contract is genuinely insufficient, note it in your final
   report — the integrator reconciles it once, exactly as PLAN.md §0.2 says.
4. **All coordinates that cross a module boundary are physical pixels**, never DIPs, never
   Avalonia's logical/scaled units — same discipline as the WPF app (DESIGN.md, PLAN.md §0.4).
   `System.Half` is still the FP16 storage type (PLAN.md §0.5) — this is unchanged in Core.
5. **Every new project targets the exact TFM/package versions pinned in §1.** These were verified
   against nuget.org on 2026-07-07 (see §5 "Package versions" and §6 flag 1) — do not "helpfully"
   bump a version without re-verifying the whole dependency graph still resolves to one SkiaSharp.
6. **A platform project compiling does not mean it runs correctly.** Only Windows gets full
   interactive verification on this machine (§4). Linux gets a WSLg smoke test if available. macOS
   is compile-only — ship it explicitly labeled "built, not hardware-validated" (DESIGN-XPLAT.md
   "Verification reality"); do not claim more than that in any status report.
7. Money quote to keep visually in mind while implementing: buffers stay **raw**; only the metadata
   (`SdrWhiteInBufferUnits`, `Monitor.SdrWhiteNits`) carries the convention that turns raw values
   into nits or into a tone-mapped SDR image. Getting this wrong silently reintroduces the
   washed-out-screenshot bug RoeSnip exists to fix — see §2.3's worked derivation before touching
   `ToneMapper` or `CapturedFrame.ReadPixelNits`.

---

## 1. New solution layout

### 1.1 Directory layout (added; `src/RoeSnip/` and `tests/RoeSnip.Tests/` untouched)

```
roesnip/
  RoeSnip.sln                          # existing file, gains new project entries (§1.6)
  DESIGN.md  DESIGN-XPLAT.md  PLAN.md  PLAN-XPLAT.md  README.md
  src/
    RoeSnip/                           # UNTOUCHED — frozen WPF app (existing)
    RoeSnip.Core/                      # WP-X1
      RoeSnip.Core.csproj
      Capture/
        RectPhysical.cs  MonitorInfo.cs
        FrameFormat.cs  CapturedFrame.cs
        IScreenCapturer.cs  CaptureException.cs
        ICaptureBackend.cs  CaptureBackendRegistry.cs
        FallbackCaptureBackend.cs        # new: generalized DD->WGC-style fallback+cache, reusable
        CaptureService.cs
        CaptureCache.cs                  # ported from src/RoeSnip/Capture/CaptureCache.cs (portable as-is)
        FrameSanity.cs                   # ported verbatim
      Color/
        ColorMath.cs  ToneMapper.cs  Dither.cs   # ported, ToneMapper's scale step generalized (§2.3)
      Imaging/
        SdrImage.cs                      # ported, WPF BitmapSource method removed
        PngWriter.cs                     # re-implemented via SkiaSharp (was WPF PngBitmapEncoder)
      Settings/
        RoeSnipSettings.cs  SettingsStore.cs  ConfigPaths.cs
    RoeSnip.Platform.Windows/           # WP-X2 (backend half)
      RoeSnip.Platform.Windows.csproj
      Interop/NativeMethods.cs           # ported from src/RoeSnip/Interop/NativeMethods.cs
      WindowsCaptureBackend.cs           # new: ICaptureBackend impl, wraps FallbackCaptureBackend
      DesktopDuplicationCapturer.cs  WgcCapturer.cs   # ported to IScreenCapturer (Core contract)
      MonitorEnumerator.cs               # ported logic, portable MonitorInfo output
      JxrWriter.cs                        # ported verbatim (Windows-only, unchanged encoder choice)
    RoeSnip.Platform.MacOS/             # WP-X5
      RoeSnip.Platform.MacOS.csproj
      MacCaptureBackend.cs
      ScksnapHelperClient.cs             # shells out to the helper binary, parses its output
    RoeSnip.Platform.Linux/             # WP-X4
      RoeSnip.Platform.Linux.csproj
      LinuxCaptureBackend.cs
      PortalScreenshotCapturer.cs        # xdg-desktop-portal via Tmds.DBus
      X11Capturer.cs                     # XGetImage via libX11 P/Invoke fallback
    RoeSnip.App/                        # WP-X2 (shell half) + WP-X3 (overlay half) — see §3 seam
      RoeSnip.App.csproj
      app.manifest                       # Windows only; harmless elsewhere (§1.5)
      Program.cs                         # WP-X2: AppComposition, OverlayResult, RoeSnipSettings alias,
                                          #         CliOptions, Main — direct analog of Phase 1 Program.cs
      App.axaml  App.axaml.cs            # WP-X2: Avalonia Application, styles
      AppShell/
        TrayApp.cs                       # WP-X2
        HotkeyManager.cs                 # WP-X2 (SharpHook wrapper)
        SingleInstance.cs                # WP-X2 (mutex + named pipe, cross-platform)
        SettingsWindow.axaml(.cs)        # WP-X2
        StartupManager.cs                # WP-X2
      Overlay/
        OverlayController.cs             # WP-X3
        OverlayWindow.axaml(.cs)         # WP-X3
        SelectionAdorner.cs  Magnifier.cs  AnnotationLayer.cs  ToolbarControl.axaml(.cs)  # WP-X3
        ClipboardService.cs              # WP-X3 (per-OS branches, single file — see §3.3)
        SdrImageAvaloniaExtensions.cs     # WP-X3 (SdrImage <-> WriteableBitmap, the Core/UI seam)
  tests/
    RoeSnip.Tests/                      # UNTOUCHED — existing WPF golden tests
    RoeSnip.Core.Tests/                 # WP-X1
      RoeSnip.Core.Tests.csproj
      ColorMathTests.cs  ToneMapperTests.cs  SettingsTests.cs
      CaptureCacheTests.cs  FrameSanityTests.cs  FallbackCaptureBackendTests.cs
    RoeSnip.Platform.Windows.Tests/     # WP-X2
      RoeSnip.Platform.Windows.Tests.csproj
      JxrRoundTripTests.cs
  helpers/
    scksnap/                             # WP-X5: Swift source + GitHub Actions workflow
```

### 1.2 Package versions (verified against nuget.org, 2026-07-07 — see §6 flag 1 for the caveats)

| Package | Version | Notes |
|---|---|---|
| `Avalonia` | `12.0.5` | Stable; net8.0/net9.0/net10.0 all supported, we pin net8.0 (matches WPF app + Core). |
| `Avalonia.Desktop` | `12.0.5` | Pulls Win32/X11/Native/Skia/HarfBuzz backends. |
| `Avalonia.Themes.Fluent` | `12.0.5` | |
| `Avalonia.Fonts.Inter` | `12.0.5` | Avalonia template default; keeps text rendering consistent across OSes without relying on a system font that may be absent on a minimal Linux install. |
| `Avalonia.Diagnostics` | `12.0.5` | Debug-config only (DevTools overlay). |
| `SkiaSharp` (Core, Linux backend) | `3.119.4` | **Must match** `Avalonia.Skia 12.0.5`'s own floor exactly (verified: `Avalonia.Skia 12.0.5` depends on `SkiaSharp >= 3.119.4`). Do not pin higher — SkiaSharp's own numbering jumped to a `4.x` line for unrelated packages/timelines; pinning ahead of Avalonia's floor risks a second, incompatible native-asset set being restored. |
| `SkiaSharp.NativeAssets.Win32` (Core.Tests only) | `3.119.4` | `RoeSnip.Core` itself stays native-asset-agnostic (App supplies the right native assets transitively via Avalonia.Skia for the real app); the test project needs its own native asset reference since it never references Avalonia. |
| `SharpHook` | `7.1.1` | Cross-platform global hook (X11/Windows/macOS only — see §5). |
| `Tmds.DBus` | `0.94.1` | Higher-level, reflection/codegen-based D-Bus client — chosen over `Tmds.DBus.Protocol` (the newer low-level/AOT-trim-friendly library) because RoeSnip needs exactly one D-Bus method call (`org.freedesktop.portal.Screenshot.Screenshot`) plus signal-waiting for the response, for which `Tmds.DBus`'s proxy-interface ergonomics are simpler and NativeAOT/trimming is not a goal for this app. |
| `Vortice.Direct3D11` / `Vortice.DXGI` / `Vortice.Direct2D1` | `3.8.3` | Unchanged from the WPF app's pins (PLAN.md §1.2) — Platform.Windows ports the exact same capture/JXR code against the exact same package versions. |

### 1.3 `src/RoeSnip.Core/RoeSnip.Core.csproj`

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
    <RootNamespace>RoeSnip.Core</RootNamespace>
    <AssemblyName>RoeSnip.Core</AssemblyName>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <AllowUnsafeBlocks>true</AllowUnsafeBlocks>
  </PropertyGroup>

  <ItemGroup>
    <!-- Pinned 2026-07 to Avalonia.Skia 12.0.5's own SkiaSharp floor exactly — see §1.2. -->
    <PackageReference Include="SkiaSharp" Version="3.119.4" />
  </ItemGroup>

</Project>
```

Zero UI dependency, zero OS-specific dependency — this project must build identically on every host
OS (it is the thing every other Phase B project references).

### 1.4 `src/RoeSnip.Platform.Windows/RoeSnip.Platform.Windows.csproj`

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <TargetFramework>net8.0-windows10.0.22621.0</TargetFramework>
    <RootNamespace>RoeSnip.Platform.Windows</RootNamespace>
    <AssemblyName>RoeSnip.Platform.Windows</AssemblyName>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <AllowUnsafeBlocks>true</AllowUnsafeBlocks>
  </PropertyGroup>

  <ItemGroup>
    <!-- Same pins as src/RoeSnip/RoeSnip.csproj (PLAN.md §1.2) — this is a mechanical port of the
         same DD/WGC/JXR code against Core contracts, not a rewrite. -->
    <PackageReference Include="Vortice.Direct3D11" Version="3.8.3" />
    <PackageReference Include="Vortice.DXGI" Version="3.8.3" />
    <PackageReference Include="Vortice.Direct2D1" Version="3.8.3" />
  </ItemGroup>

  <ItemGroup>
    <ProjectReference Include="..\RoeSnip.Core\RoeSnip.Core.csproj" />
  </ItemGroup>

</Project>
```

No `UseWPF`/`UseWindowsForms` here — this project is a pure managed capture backend (Vortice +
CsWinRT-projected `Windows.Graphics.Capture` types from the TFM), not a UI project. That distinction
matters for design-time builds — see §5's "cross-host compile" fact.

### 1.5 `src/RoeSnip.Platform.MacOS/RoeSnip.Platform.MacOS.csproj`

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
    <RootNamespace>RoeSnip.Platform.MacOS</RootNamespace>
    <AssemblyName>RoeSnip.Platform.MacOS</AssemblyName>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
  </PropertyGroup>

  <ItemGroup>
    <ProjectReference Include="..\RoeSnip.Core\RoeSnip.Core.csproj" />
  </ItemGroup>

</Project>
```

Plain `net8.0` — this project only shells out to the `scksnap` helper binary and parses its output;
it needs no macOS SDK/workload and compiles on any host OS, including this Windows machine (per
DESIGN-XPLAT.md: "net8.0-macos workload can't build on this Windows machine" is exactly why the real
capture logic is a Swift helper, not a .NET one).

### 1.6 `src/RoeSnip.Platform.Linux/RoeSnip.Platform.Linux.csproj`

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
    <RootNamespace>RoeSnip.Platform.Linux</RootNamespace>
    <AssemblyName>RoeSnip.Platform.Linux</AssemblyName>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <AllowUnsafeBlocks>true</AllowUnsafeBlocks>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="Tmds.DBus" Version="0.94.1" />
    <!-- Decodes the portal's returned PNG (the portal path is SDR-only per DESIGN-XPLAT) and
         re-slices per monitor; matches Core's own pin exactly (§1.2). -->
    <PackageReference Include="SkiaSharp" Version="3.119.4" />
  </ItemGroup>

  <ItemGroup>
    <ProjectReference Include="..\RoeSnip.Core\RoeSnip.Core.csproj" />
  </ItemGroup>

</Project>
```

`libX11.so.6` P/Invoke declarations for the `XGetImage` fallback are plain `DllImport` attributes —
they compile on any host OS (the native library only needs to actually *exist* at runtime, on a real
Linux/X11 machine).

### 1.7 `src/RoeSnip.App/RoeSnip.App.csproj`

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net8.0</TargetFramework>
    <RootNamespace>RoeSnip.App</RootNamespace>
    <AssemblyName>RoeSnip</AssemblyName>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <AllowUnsafeBlocks>true</AllowUnsafeBlocks>
    <RuntimeIdentifiers>win-x64;osx-x64;osx-arm64;linux-x64</RuntimeIdentifiers>
    <AvaloniaUseCompiledBindingsByDefault>true</AvaloniaUseCompiledBindingsByDefault>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="Avalonia" Version="12.0.5" />
    <PackageReference Include="Avalonia.Desktop" Version="12.0.5" />
    <PackageReference Include="Avalonia.Themes.Fluent" Version="12.0.5" />
    <PackageReference Include="Avalonia.Fonts.Inter" Version="12.0.5" />
    <PackageReference Include="Avalonia.Diagnostics" Version="12.0.5" Condition="'$(Configuration)' == 'Debug'" />
    <PackageReference Include="SharpHook" Version="7.1.1" />
  </ItemGroup>

  <ItemGroup>
    <ProjectReference Include="..\RoeSnip.Core\RoeSnip.Core.csproj" />
  </ItemGroup>

  <!-- Conditional per-OS capture backend — see the worked explanation immediately below. -->
  <ItemGroup Condition="'$(RuntimeIdentifier)' == '' Or $(RuntimeIdentifier.StartsWith('win'))">
    <ProjectReference Include="..\RoeSnip.Platform.Windows\RoeSnip.Platform.Windows.csproj" />
  </ItemGroup>
  <ItemGroup Condition="'$(RuntimeIdentifier)' == '' Or $(RuntimeIdentifier.StartsWith('osx'))">
    <ProjectReference Include="..\RoeSnip.Platform.MacOS\RoeSnip.Platform.MacOS.csproj" />
  </ItemGroup>
  <ItemGroup Condition="'$(RuntimeIdentifier)' == '' Or $(RuntimeIdentifier.StartsWith('linux'))">
    <ProjectReference Include="..\RoeSnip.Platform.Linux\RoeSnip.Platform.Linux.csproj" />
  </ItemGroup>

</Project>
```

**Exact msbuild condition semantics and design-time build behavior** (this is the part
DESIGN-XPLAT.md explicitly flagged as needing to be spelled out):

- `$(RuntimeIdentifier)` is empty unless the build/run/publish command passes `-r <rid>` (or
  `--os`/`--arch` that resolve to one) or `RuntimeIdentifier` is set another way. A plain
  `dotnet build RoeSnip.sln`, an IDE build, or `dotnet run --project src/RoeSnip.App` with no `-r`
  all leave it empty.
- **When `$(RuntimeIdentifier)` is empty, all three conditions are true**, so a design-time /
  no-RID build references and compiles **all three** platform projects into `RoeSnip.App`. This is
  intentional, not a bug: it means a single `dotnet build RoeSnip.sln` on this Windows dev machine
  is a genuine compile gate for Platform.Windows, Platform.MacOS, and Platform.Linux simultaneously
  (all three are designed to compile on any host OS — see §5). Ground rule 6 still applies: only
  Platform.Windows gets *runtime* verification here.
- **When publishing with a specific `-r <rid>`** (the real distribution artifacts — §4), exactly
  one platform project is referenced, so `RoeSnip.Platform.MacOS`'s and `RoeSnip.Platform.Linux`'s
  assemblies (and Tmds.DBus, etc.) never end up in a `win-x64` publish output, and vice versa.
- At **runtime**, when more than one platform assembly happens to be loaded (the no-RID case above,
  e.g. `dotnet run` on this Windows box with no `-r`), exactly one of them must actually "win" and
  become the active `ICaptureBackend`. This is NOT resolved by "whichever assembly's static
  initializer happened to run last" — see §2.3's `CaptureBackendRegistry`, which resolves it by each
  candidate reporting its own `IsSupported()` at the moment of selection (only Windows reports true
  on this machine, regardless of how many platform assemblies are physically loaded).
- `OutputType` is plain `Exe`, not `WinExe`, for all RIDs (kept simple/uniform rather than
  conditioned) — meaning a double-clicked Windows publish shows a console window. This is a real,
  minor UX regression versus the frozen WPF app (`WinExe`) — flagged in §6 flag 7 rather than
  silently "fixed" with an unverified conditional-`OutputType` trick.

### 1.8 `RoeSnip.sln` additions

Do not hand-author GUIDs — use the CLI (matching PLAN.md §1.1's own approach) so Visual Studio's
project-type GUIDs and nesting are generated correctly:

```
dotnet new classlib -o src/RoeSnip.Core -n RoeSnip.Core --framework net8.0
dotnet new classlib -o src/RoeSnip.Platform.Windows -n RoeSnip.Platform.Windows --framework net8.0-windows10.0.22621.0
dotnet new classlib -o src/RoeSnip.Platform.MacOS -n RoeSnip.Platform.MacOS --framework net8.0
dotnet new classlib -o src/RoeSnip.Platform.Linux -n RoeSnip.Platform.Linux --framework net8.0
dotnet new xunit -o tests/RoeSnip.Core.Tests -n RoeSnip.Core.Tests --framework net8.0
dotnet new xunit -o tests/RoeSnip.Platform.Windows.Tests -n RoeSnip.Platform.Windows.Tests --framework net8.0-windows10.0.22621.0

# Avalonia app project: install the template pack once (needs network access), then scaffold it —
# if template install is unavailable in your environment, hand-author RoeSnip.App.csproj to exactly
# §1.7's content plus a minimal Program.cs/App.axaml (§2.8/§3.2) instead; the CLI scaffold is a
# convenience, not a requirement, same as PLAN.md §1.1's own note about "reasonable skeleton but
# wrong TFM/packages."
dotnet new install Avalonia.Templates
dotnet new avalonia.app -o src/RoeSnip.App -n RoeSnip.App --framework net8.0

dotnet sln RoeSnip.sln add ^
  src/RoeSnip.Core/RoeSnip.Core.csproj ^
  src/RoeSnip.Platform.Windows/RoeSnip.Platform.Windows.csproj ^
  src/RoeSnip.Platform.MacOS/RoeSnip.Platform.MacOS.csproj ^
  src/RoeSnip.Platform.Linux/RoeSnip.Platform.Linux.csproj ^
  src/RoeSnip.App/RoeSnip.App.csproj ^
  tests/RoeSnip.Core.Tests/RoeSnip.Core.Tests.csproj ^
  tests/RoeSnip.Platform.Windows.Tests/RoeSnip.Platform.Windows.Tests.csproj

dotnet add tests/RoeSnip.Core.Tests reference src/RoeSnip.Core/RoeSnip.Core.csproj
dotnet add tests/RoeSnip.Platform.Windows.Tests reference src/RoeSnip.Platform.Windows/RoeSnip.Platform.Windows.csproj
```

(`^` is the Windows `cmd.exe` line-continuation character; if your shell is PowerShell or Git Bash,
either put each path on the `dotnet sln add` command as one line or use that shell's own
continuation.) Then hand-edit every generated `.csproj` to exactly the content in §1.3–§1.7 — the
templates generate reasonable skeletons but wrong TFMs/packages, exactly as PLAN.md §1.1 already
warned about for Phase 1.

---

## 2. Core contracts (copy verbatim)

These are the seams between work packages, same convention as PLAN.md §2: ownership of the *file*
stays with the package noted in §3, but every package may *read/reference* any type below.

### 2.1 `Capture/RectPhysical.cs`, `Capture/MonitorInfo.cs` — owned by WP-X1

```csharp
using System;

namespace RoeSnip.Core.Capture;

/// <summary>Physical-pixel rectangle. Identical to the WPF app's type (PLAN.md §2.1) — copy verbatim,
/// only the namespace changed.</summary>
public readonly record struct RectPhysical(int Left, int Top, int Right, int Bottom)
{
    public int Width => Right - Left;
    public int Height => Bottom - Top;
    public static RectPhysical FromSize(int left, int top, int width, int height)
        => new(left, top, left + width, top + height);
    public RectPhysical Normalized() => new(
        Math.Min(Left, Right), Math.Min(Top, Bottom),
        Math.Max(Left, Right), Math.Max(Top, Bottom));
}

/// <summary>One physical display, enumerated once per capture trigger. Portable: no HMONITOR (the
/// Windows-only PLAN.md §2.1 field). <see cref="BackendKey"/> is opaque outside the ICaptureBackend
/// that produced it — never parse or compare it across backends.
///   Windows: HMONITOR formatted as "0x{hex}".
///   macOS: the display's CGDirectDisplayID, formatted as its decimal value.
///   Linux (X11 fallback): the RandR output name (e.g. "DP-1").
///   Linux (portal): a synthetic zero-based index — the portal returns one whole-desktop PNG with
///     no per-monitor handles, so there is nothing more meaningful to key on (see §5 Linux facts).
/// <see cref="Scale"/> is the OS-reported HiDPI scale factor (1.0, 1.25, 1.5, 2.0, ...) —
/// <see cref="DpiX"/>/<see cref="DpiY"/> / 96.0 on Windows, the compositor/portal-reported scale
/// elsewhere. WP-X3 uses this to sanity-check its own match between a CapturedFrame and the
/// Avalonia Screen it's drawing an overlay for (see §3.3's correlation note — this is a real
/// integration risk, not a formality).</summary>
public sealed record MonitorInfo(
    int Index,
    string DeviceName,
    string BackendKey,
    RectPhysical BoundsPx,
    int DpiX,
    int DpiY,
    double Scale,
    bool AdvancedColorActive,
    double SdrWhiteNits,
    double MaxLuminanceNits,
    bool IsPrimary
);
```

### 2.2 `Capture/FrameFormat.cs`, `Capture/CapturedFrame.cs` — owned by WP-X1

```csharp
namespace RoeSnip.Core.Capture;

public enum FrameFormat
{
    Fp16ScRgb,   // linear HDR buffer (Windows scRGB or macOS EDR — see SdrWhiteInBufferUnits below).
                 // 8 bytes/pixel (4 x System.Half), channel order R,G,B,A.
    Bgra8Srgb,   // already sRGB-encoded passthrough. 4 bytes/pixel, channel order B,G,R,A.
}
```

```csharp
using System;
using System.Numerics;
using RoeSnip.Core.Color;

namespace RoeSnip.Core.Capture;

/// <summary>Owns one monitor's raw captured pixels for the lifetime of a capture session. Identical
/// shape to the WPF app's type (PLAN.md §2.2) with one addition: <see cref="SdrWhiteInBufferUnits"/>.
///
/// ## Key semantics — read this before touching ToneMapper or ReadPixelNits
///
/// The WPF app hardcoded "1.0 buffer unit = 80 nits" everywhere because Windows scRGB defines it
/// that way. macOS SCK's EDR buffers use a DIFFERENT convention: 1.0 buffer unit IS SDR/reference
/// white by construction (EDR headroom is expressed as a multiplier above 1.0, not in nits-per-unit
/// terms). <see cref="SdrWhiteInBufferUnits"/> is what raw buffer value (per channel) corresponds to
/// SDR white for THIS frame, so both conventions can share one ToneMapper:
///   - Windows Fp16ScRgb: <c>Monitor.SdrWhiteNits / 80.0</c> (scRGB's fixed 80-nits-per-unit rule).
///   - macOS EDR Fp16 buffers: <c>1.0</c> (SCK's own convention).
///   - Bgra8Srgb frames (any backend): irrelevant/unused — see the ReadPixelNits note below. Set it
///     to <c>1.0</c> by convention; nothing reads it for this format.
///
/// ToneMapper's pass-1 scale step generalizes from the WPF app's hardcoded
/// <c>scale = 80.0 / Monitor.SdrWhiteNits</c> to <c>scale = 1.0 / SdrWhiteInBufferUnits</c> — algebraically
/// identical on Windows when <c>SdrWhiteInBufferUnits == Monitor.SdrWhiteNits / 80.0</c> (substitute:
/// <c>1.0 / (SdrWhiteNits/80.0) == 80.0/SdrWhiteNits</c>). ToneMapper.MapToSdr still only accepts
/// Fp16ScRgb frames (throws otherwise, unchanged) — this generalization only ever executes on that
/// format, so it never needs to special-case Bgra8Srgb.
///
/// <see cref="ReadPixelNits"/> is trickier because — unlike ToneMapper — it is called for EVERY
/// frame format (the magnifier/color-inspector reads nits from whatever the user is hovering,
/// regardless of whether that monitor is HDR). The WPF app's Bgra8Srgb branch of
/// <see cref="ReadPixelScRgb"/> ALREADY bakes <c>Monitor.SdrWhiteNits / 80.0</c> into its own decode
/// (so a pure-white byte reads back as exactly <c>Monitor.SdrWhiteNits</c> nits via the constant
/// <c>* 80.0</c> in the old ReadPixelNits) — that baked-in scale is NOT the same value as
/// <see cref="SdrWhiteInBufferUnits"/> for this format (which is the unused "1.0" sentinel above).
/// Naively generalizing ReadPixelNits to always do <c>bufferMax / SdrWhiteInBufferUnits * Monitor.SdrWhiteNits</c>
/// for BOTH formats is WRONG for Bgra8Srgb — it would double-apply the SdrWhiteNits scaling and
/// produce <c>SdrWhiteNits² / 80</c> instead of <c>SdrWhiteNits</c> for a white pixel. The fix (baked
/// into the method below): keep the Bgra8Srgb branch doing the exact old constant-<c>*80.0</c> math,
/// unconditionally, and ONLY use the generalized <c>SdrWhiteInBufferUnits</c> formula for Fp16ScRgb.
/// This is exactly why DESIGN-XPLAT.md calls Bgra8Srgb's value "n/a" — it is unused BECAUSE the
/// nits math for that format never needed generalizing in the first place, not because nits
/// don't matter for SDR frames.</summary>
public sealed class CapturedFrame : IDisposable
{
    public FrameFormat Format { get; }
    public int Width { get; }
    public int Height { get; }
    public int Stride { get; }
    public MonitorInfo Monitor { get; }
    public int BytesPerPixel => Format == FrameFormat.Fp16ScRgb ? 8 : 4;
    public double SdrWhiteInBufferUnits { get; }

    private byte[]? _pixels;

    public CapturedFrame(
        FrameFormat format, int width, int height, int stride, byte[] pixels,
        MonitorInfo monitor, double sdrWhiteInBufferUnits)
    {
        Format = format;
        Width = width;
        Height = height;
        Stride = stride;
        Monitor = monitor;
        SdrWhiteInBufferUnits = sdrWhiteInBufferUnits;
        _pixels = pixels;
    }

    public ReadOnlySpan<byte> Row(int y)
    {
        var pixels = _pixels ?? throw new ObjectDisposedException(nameof(CapturedFrame));
        return pixels.AsSpan(y * Stride, Width * BytesPerPixel);
    }

    /// <summary>Reads pixel (x,y) in this frame's own buffer units (NOT nits) — for Fp16ScRgb this is
    /// the raw linear value (Windows scRGB or macOS EDR, per SdrWhiteInBufferUnits); for Bgra8Srgb
    /// this decodes the sRGB EOTF and rescales by Monitor.SdrWhiteNits/80.0, EXACTLY as the WPF app
    /// did (PLAN.md §2.2) — unchanged, not touched by the SdrWhiteInBufferUnits generalization.</summary>
    public Vector4 ReadPixelScRgb(int x, int y)
    {
        var row = Row(y);
        if (Format == FrameFormat.Fp16ScRgb)
        {
            int o = x * 8;
            float r = (float)BitConverter.ToHalf(row.Slice(o, 2));
            float g = (float)BitConverter.ToHalf(row.Slice(o + 2, 2));
            float b = (float)BitConverter.ToHalf(row.Slice(o + 4, 2));
            float a = (float)BitConverter.ToHalf(row.Slice(o + 6, 2));
            return new Vector4(r, g, b, a);
        }
        else
        {
            int o = x * 4;
            byte b8 = row[o], g8 = row[o + 1], r8 = row[o + 2], a8 = row[o + 3];
            float scale = (float)(Monitor.SdrWhiteNits / 80.0);
            float r = ColorMath.SrgbByteToLinear(r8) * scale;
            float g = ColorMath.SrgbByteToLinear(g8) * scale;
            float b = ColorMath.SrgbByteToLinear(b8) * scale;
            return new Vector4(r, g, b, a8 / 255f);
        }
    }

    /// <summary>Photometric nits for the magnifier/color-inspector readout — see the class doc
    /// comment above for why the two formats are NOT both routed through the same formula.</summary>
    public double ReadPixelNits(int x, int y)
    {
        var v = ReadPixelScRgb(x, y);
        double bufferMax = Math.Max(v.X, Math.Max(v.Y, v.Z));
        if (Format == FrameFormat.Bgra8Srgb)
        {
            return bufferMax * 80.0; // unchanged from the WPF app — do not route through SdrWhiteInBufferUnits.
        }
        return bufferMax / SdrWhiteInBufferUnits * Monitor.SdrWhiteNits;
    }

    public void Dispose() => _pixels = null;
}
```

### 2.3 `Capture/IScreenCapturer.cs`, `ICaptureBackend.cs`, `CaptureBackendRegistry.cs`, `FallbackCaptureBackend.cs`, `CaptureService.cs` — owned by WP-X1

```csharp
namespace RoeSnip.Core.Capture;

public sealed class CaptureException : Exception
{
    public CaptureException(string message, Exception? inner = null) : base(message, inner) { }
}

/// <summary>Captures a single frame of one monitor. Unchanged from the WPF app's contract
/// (PLAN.md §2.3) — Windows' DD/WGC capturers, Linux's portal/X11 capturers all implement this.</summary>
public interface IScreenCapturer
{
    CapturedFrame Capture(MonitorInfo monitor);
}
```

```csharp
namespace RoeSnip.Core.Capture;

/// <summary>The per-OS entry point: monitor enumeration + capture-all for one platform. This is the
/// NEW abstraction DESIGN-XPLAT.md calls for ("ICaptureBackend (new: monitor enumeration +
/// capture-all)") — it replaces the WPF app's static <c>MonitorEnumerator</c> class (monitor
/// enumeration is now backend-specific: DXGI+DisplayConfig on Windows, CGDirectDisplayID/scksnap on
/// macOS, RandR/portal on Linux) and generalizes <c>CaptureService</c> from "always DD-then-WGC" to
/// "whatever this OS's backend does."</summary>
public interface ICaptureBackend
{
    /// <summary>Human-readable name for --diag / error messages, e.g. "Windows (Desktop
    /// Duplication/WGC)", "Linux (xdg-desktop-portal)".</summary>
    string Name { get; }

    /// <summary>True if this backend can produce an untouched HDR original suitable for the "Save
    /// HDR" / --jxr-equivalent export path. v1: Windows only (DESIGN-XPLAT.md "Save HDR is
    /// Windows-only v1 (backend capability flag hides the button elsewhere)") — this is that flag.</summary>
    bool SupportsHdrExport { get; }

    /// <summary>Enumerates all active monitors for this session. Never throws for a single bad
    /// monitor entry — logs to stderr and omits it. Empty list only if enumeration itself fails
    /// entirely.</summary>
    IReadOnlyList<MonitorInfo> EnumerateMonitors();

    /// <summary>Captures every monitor in <paramref name="monitors"/> (or all enumerated monitors if
    /// null). Per monitor: try this backend's own fallback policy; on total failure, log to stderr
    /// and OMIT that monitor (never throw). If <paramref name="onlyMonitorIndex"/> is set, only that
    /// monitor is attempted. Returns frames in the same order as the input monitor list.</summary>
    IReadOnlyList<CapturedFrame> CaptureAll(
        IReadOnlyList<MonitorInfo>? monitors = null, int? onlyMonitorIndex = null);
}

/// <summary>Selects the one <see cref="ICaptureBackend"/> that applies to the OS actually running,
/// regardless of how many platform assemblies happen to be LOADED (see §1.7's design-time-build
/// note — a no-RID build references all three Platform.* projects, but exactly one candidate ever
/// reports <c>isSupported() == true</c> at runtime). Each Platform.* project registers itself via a
/// <c>[ModuleInitializer]</c>-attributed method in its own file, mirroring Phase 1's AppComposition
/// hook pattern (PLAN.md §2.4) — Core/App never references a concrete Platform.* type by name.</summary>
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
```

```csharp
namespace RoeSnip.Core.Capture;

/// <summary>Generalizes the WPF app's CaptureService fallback/cache/parallel-capture orchestration
/// (PLAN.md §2.3 + the Phase-A CaptureCache addition) into reusable infrastructure for ANY platform
/// whose capture strategy is itself an ordered list of capturers with a "once broken, skip forever"
/// memo — Windows (DD then WGC) and Linux (portal then X11) both fit this shape; macOS does not
/// (exactly one capturer) and implements ICaptureBackend directly instead (§3.5).
///
/// Behavior, copied from the WPF app's CaptureService.CaptureAll/CaptureOneOrNull (unchanged):
/// monitors are captured in PARALLEL (each capturer call is independent); per monitor, try
/// capturers in the given priority order, skipping any this monitor's <see cref="CaptureCache"/>
/// entry says is already known-broken; on the first success, return it; on total failure across all
/// capturers, log to stderr and omit the monitor. The FIRST failure of a given capturer for a given
/// monitor persists a cache entry so future captures (including after a relaunch) skip straight past
/// it.</summary>
public sealed class FallbackCaptureBackend : ICaptureBackend
{
    private readonly Func<IReadOnlyList<MonitorInfo>> _enumerate;
    private readonly IReadOnlyList<IScreenCapturer> _capturersInPriorityOrder;
    private readonly CaptureCache _cache;

    public string Name { get; }
    public bool SupportsHdrExport { get; }

    public FallbackCaptureBackend(
        string name, bool supportsHdrExport,
        Func<IReadOnlyList<MonitorInfo>> enumerate,
        IReadOnlyList<IScreenCapturer> capturersInPriorityOrder,
        CaptureCache cache)
    {
        Name = name;
        SupportsHdrExport = supportsHdrExport;
        _enumerate = enumerate;
        _capturersInPriorityOrder = capturersInPriorityOrder;
        _cache = cache;
    }

    public IReadOnlyList<MonitorInfo> EnumerateMonitors() => _enumerate();

    public IReadOnlyList<CapturedFrame> CaptureAll(
        IReadOnlyList<MonitorInfo>? monitors = null, int? onlyMonitorIndex = null)
    {
        var targets = monitors ?? EnumerateMonitors();
        var selected = new List<MonitorInfo>();
        foreach (var monitor in targets)
        {
            if (onlyMonitorIndex is int idx && monitor.Index != idx) continue;
            selected.Add(monitor);
        }

        var slots = new CapturedFrame?[selected.Count];
        Parallel.For(0, selected.Count, i => slots[i] = CaptureOneOrNull(selected[i]));

        var results = new List<CapturedFrame>(selected.Count);
        foreach (var frame in slots) if (frame is not null) results.Add(frame);
        return results;
    }

    private CapturedFrame? CaptureOneOrNull(MonitorInfo monitor)
    {
        for (int i = 0; i < _capturersInPriorityOrder.Count; i++)
        {
            string capturerKey = $"{monitor.DeviceName}::{i}"; // per-capturer-slot memo, not just per-monitor
            if (_cache.IsDesktopDuplicationBroken(capturerKey)) continue; // name kept for on-disk compat; see note below

            try
            {
                return _capturersInPriorityOrder[i].Capture(monitor);
            }
            catch (CaptureException ex)
            {
                bool isLastCapturer = i == _capturersInPriorityOrder.Count - 1;
                _cache.MarkDesktopDuplicationBroken(capturerKey);
                Console.Error.WriteLine(
                    $"RoeSnip: capturer #{i} failed for monitor {monitor.Index} ({monitor.DeviceName}): " +
                    $"{ex.Message}.{(isLastCapturer ? " Omitting this monitor." : " Falling back to the next capturer.")}");
            }
        }
        return null;
    }
}
```

```csharp
namespace RoeSnip.Core.Capture;

/// <summary>Thin, backend-agnostic facade so CLI/AppComposition code looks almost identical to the
/// WPF app's (PLAN.md §2.3) even though the fallback logic now lives behind whichever
/// ICaptureBackend the registry selects.</summary>
public sealed class CaptureService
{
    private readonly ICaptureBackend _backend;

    public CaptureService() : this(CaptureBackendRegistry.CreateForCurrentPlatform()) { }
    public CaptureService(ICaptureBackend backend) { _backend = backend; }

    public bool SupportsHdrExport => _backend.SupportsHdrExport;
    public string BackendName => _backend.Name;

    public IReadOnlyList<MonitorInfo> EnumerateMonitors() => _backend.EnumerateMonitors();

    public IReadOnlyList<CapturedFrame> CaptureAll(
        IReadOnlyList<MonitorInfo>? monitors = null, int? onlyMonitorIndex = null)
        => _backend.CaptureAll(monitors, onlyMonitorIndex);
}
```

**Note on `FallbackCaptureBackend`'s cache key:** the ported `CaptureCache` (§2.4) keeps its existing
method names (`IsDesktopDuplicationBroken`/`MarkDesktopDuplicationBroken`) verbatim from the WPF app
for minimum porting risk — this plan generalizes its *usage* (keying by `"{DeviceName}::{capturerIndex}"`
instead of bare `DeviceName`) without renaming the class's public methods, since the on-disk JSON
shape and existing `CaptureCacheTests.cs` assertions key off those exact method names. If an
implementer finds the "DesktopDuplication"-flavored method names too confusing when reused for
Linux's portal/X11 fallback, that's a legitimate readability complaint — flag it in your final report
rather than renaming unilaterally (ground rule 3).

### 2.4 `Capture/CaptureCache.cs`, `Capture/FrameSanity.cs` — owned by WP-X1 (ported near-verbatim)

Port `src/RoeSnip/Capture/CaptureCache.cs` and `src/RoeSnip/Capture/FrameSanity.cs` into
`RoeSnip.Core.Capture` with exactly one change: `CaptureCache.CacheFilePath` must use the portable
`ConfigPaths.ConfigDirectory` (§2.6) instead of the hardcoded
`Environment.SpecialFolder.ApplicationData` call, i.e.:

```csharp
public static string CacheFilePath => Path.Combine(ConfigPaths.ConfigDirectory, "capture-cache.json");
```

Everything else — the lazy-load, the `HashSet<string>` memo, the fail-open-on-corrupt-file behavior,
the `CacheDto`/`SchemaVersion` shape — ports byte-for-byte identical to
`src/RoeSnip/Capture/CaptureCache.cs`. `FrameSanity.IsAllZero` ports with zero changes (it's already
pure `ReadOnlySpan<byte>` logic).

### 2.5 `Color/ColorMath.cs`, `Color/Dither.cs`, `Color/ToneMapper.cs` — owned by WP-X1

`ColorMath.cs` and `Dither.cs` port with **zero changes** (already pure, no WPF/Windows dependency) —
copy `src/RoeSnip/Color/ColorMath.cs` and `src/RoeSnip/Color/Dither.cs` verbatim, only the namespace
changes to `RoeSnip.Core.Color`.

`ToneMapper.cs` ports with exactly one change to the algorithm — everywhere the original computes
`float scale = (float)(80.0 / frame.Monitor.SdrWhiteNits);` (both in Pass 1's per-pixel loop and
conceptually as "the" scale constant), replace with:

```csharp
float scale = (float)(1.0 / frame.SdrWhiteInBufferUnits);
```

`ToneMapOptions` (the `record struct` with `Knee`/`PeakOverride`/`Epsilon`) is unchanged — it doesn't
reference `SdrWhiteNits` directly, only `ToneMapper.MapToSdr`'s body does. The `peak` derivation
(`Math.Clamp(Math.Min(m0, frame.Monitor.MaxLuminanceNits / frame.Monitor.SdrWhiteNits), 2.0,
double.MaxValue)`) is **unchanged** — that ratio is real nits-to-nits, not buffer-units, so it needs
no generalization (see §2.3's/§2.2's worked derivation for why only the *scale* step needed it).
Everything else — the Hermite shoulder math, the dither-only-on-shoulder-pixels rule, the
`Parallel.For` structure, the defensive knee/peak sanitization — ports byte-for-byte identical to
`src/RoeSnip/Color/ToneMapper.cs`.

### 2.6 `Settings/RoeSnipSettings.cs`, `Settings/ConfigPaths.cs`, `Settings/SettingsStore.cs` — owned by WP-X1

```csharp
namespace RoeSnip.Core.Settings;

/// <summary>Same shape as the WPF app's RoeSnipSettings (PLAN.md §2.4 / Phase-A additions) — moved
/// into Core per DESIGN-XPLAT.md's own solution layout ("Settings/ RoeSnipSettings + SettingsStore").
/// HotkeyModifiers/HotkeyVirtualKey remain Windows VK/MOD_* values in shape — see §5 hotkey facts for
/// how WP-X2's hotkey code interprets (or ignores) them per OS.</summary>
public sealed record RoeSnipSettings
{
    public int SchemaVersion { get; init; } = 1;
    public uint HotkeyModifiers { get; init; } = 0;
    public uint HotkeyVirtualKey { get; init; } = 0x2C; // VK_SNAPSHOT; meaningful on Windows only
    public string SaveDirectory { get; init; } = DefaultSaveDirectory();
    public bool AutoSaveHdrCopy { get; init; } = false;
    public double? ToneMapKneeOverride { get; init; } = null;
    public double? ToneMapPeakOverride { get; init; } = null;
    public bool RunAtStartup { get; init; } = false;
    public bool CopyOnSelect { get; init; } = false;
    public bool PrintScreenPromptAnswered { get; init; } = false; // Windows-only meaning; harmless elsewhere

    public static RoeSnipSettings Default { get; } = new();

    private static string DefaultSaveDirectory() =>
        System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "RoeSnip");
}
```

```csharp
namespace RoeSnip.Core.Settings;

/// <summary>Per-OS config directory (DESIGN-XPLAT.md "portable config dirs"). Shared by
/// SettingsStore (settings.json) and CaptureCache (capture-cache.json, §2.4) so both files live
/// side by side per OS, matching the WPF app's %APPDATA%\RoeSnip convention on Windows.</summary>
public static class ConfigPaths
{
    public const string AppName = "RoeSnip";       // Windows/macOS directory name
    public const string AppNameLower = "roesnip";  // Linux directory name (XDG convention)

    public static string ConfigDirectory
    {
        get
        {
            if (OperatingSystem.IsWindows())
            {
                return System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), AppName);
            }
            if (OperatingSystem.IsMacOS())
            {
                string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                return System.IO.Path.Combine(home, "Library", "Application Support", AppName);
            }
            // Linux and any other POSIX host.
            string? xdg = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
            string baseDir = !string.IsNullOrEmpty(xdg)
                ? xdg
                : System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");
            return System.IO.Path.Combine(baseDir, AppNameLower);
        }
    }
}
```

`SettingsStore.cs` ports from `src/RoeSnip/App/Settings.cs` with exactly one change: replace the
hardcoded `%APPDATA%\RoeSnip` path with `ConfigPaths.ConfigDirectory`. The fail-closed load
semantics, atomic-write-via-temp-file-then-`File.Replace`/`File.Move` save, and the two path-taking
test overloads (`Load(string path)`/`Save(settings, path)`) all port unchanged from
`src/RoeSnip/App/Settings.cs`.

### 2.7 `Imaging/SdrImage.cs`, `Imaging/PngWriter.cs` — owned by WP-X1

`SdrImage.cs` ports from `src/RoeSnip/Imaging/SdrImage.cs` with the WPF-only method
(`ToBitmapSource()`) **removed** — Core has zero UI dependency (DESIGN-XPLAT.md). Everything else
(`Width`/`Height`/`Stride`/`Pixels`, the constructor validation, `FromCapturedFrame`'s format branch,
`FromBgra8Passthrough`, `Crop`) ports unchanged. Avalonia's `WriteableBitmap` conversion is a WP-X3
concern living in `RoeSnip.App` (§3.3), not Core, exactly mirroring how the removed WPF method never
belonged in a "zero UI deps" module either — it just happened to live there in Phase 1 because there
was only one UI framework to support.

```csharp
namespace RoeSnip.Core.Imaging;

/// <summary>SkiaSharp-based replacement for the WPF app's PngBitmapEncoder-based PngWriter
/// (PLAN.md §2.6/§3.1) — same two-method surface, same BGRA8-straight-alpha-tightly-packed
/// contract as SdrImage guarantees, so no caller needs to change.</summary>
public static class PngWriter
{
    public static void WriteFile(string path, SdrImage image)
    {
        using var stream = new System.IO.FileStream(path, System.IO.FileMode.Create, System.IO.FileAccess.Write);
        using var data = EncodeToData(image);
        data.SaveTo(stream);
    }

    public static byte[] Encode(SdrImage image)
    {
        using var data = EncodeToData(image);
        return data.ToArray();
    }

    private static SkiaSharp.SKData EncodeToData(SdrImage image)
    {
        var info = new SkiaSharp.SKImageInfo(image.Width, image.Height, SkiaSharp.SKColorType.Bgra8888, SkiaSharp.SKAlphaType.Unpremul);
        using var bitmap = new SkiaSharp.SKBitmap(info);
        // SdrImage.Stride == Width * 4 always (tightly packed, per its own contract) and matches
        // SKImageInfo's default RowBytes for Bgra8888 exactly — a straight Marshal.Copy into
        // Skia-owned pixel memory is safe (no manual pinning of the managed array required).
        System.Runtime.InteropServices.Marshal.Copy(image.Pixels, 0, bitmap.GetPixels(), image.Pixels.Length);
        using var skImage = SkiaSharp.SKImage.FromBitmap(bitmap);
        return skImage.Encode(SkiaSharp.SKEncodedImageFormat.Png, 100);
    }
}
```

`SKColorType.Bgra8888` + `SKAlphaType.Unpremul` matches `SdrImage`'s documented byte layout exactly
(B,G,R,A straight alpha) — this is not a behavior change, only an encoder-library swap.

### 2.8 `Program.cs` — composition root, owned by WP-X2

Direct analog of Phase 1's `AppComposition` (PLAN.md §2.4), generalized only where the OS-plurality
requires it (`RunOverlay`/`WriteHdrExport` hooks are unchanged in spirit; there is no more
`RunTrayApp`/`ITrayNotifier` split needed than before). Copy this shape; WP-X2 fills in the CLI
verbs, WP-X3 registers `RunOverlay` from `Overlay/OverlayController.cs` via `[ModuleInitializer]`
exactly like PLAN.md §2.7's `OverlayController.RunAsync`.

```csharp
using RoeSnip.Core.Capture;
using RoeSnip.Core.Imaging;
using RoeSnip.Core.Settings;

namespace RoeSnip.App;

/// <summary>Data-only result of one overlay session — identical fields to the WPF app's
/// OverlayResult (PLAN.md §2.4).</summary>
public sealed record OverlayResult(
    MonitorInfo Monitor,
    RectPhysical SelectionPx,
    SdrImage RenderedImage,
    CapturedFrame SourceFrame,
    bool CopyPerformed,
    string? SavedPngPath,
    bool SaveHdrRequested
);

public interface ITrayNotifier
{
    void ShowSavedBalloon(string filePath);
    void ShowError(string message);
}

/// <summary>CliMode gains two new verbs beyond the WPF app's CliOptions (PLAN.md §2.4):
/// TriggerCapture ("RoeSnip capture" — signal a running instance to run the interactive overlay
/// flow, or become the resident instance and do so if none is running) and TriggerSettings
/// ("RoeSnip settings" — open the settings window in the running/new instance). These are the CLI
/// verbs DESIGN-XPLAT.md requires as the primary Linux activation path (a DE keyboard shortcut bound
/// to "RoeSnip capture") and as part of "the app is fully operable via CLI verbs." Diag/Capture
/// (headless, dash-prefixed) are UNCHANGED from the WPF app.</summary>
public enum CliMode { None, Diag, Capture, TriggerCapture, TriggerSettings }

public sealed record CliOptions(CliMode Mode, int? Monitor, string? Out, bool Jxr)
{
    // Grammar: --diag | --capture [--monitor N] [--out path] [--jxr] | capture | settings
    // "capture"/"settings" are bare positional verbs (no leading --) — distinct from the headless
    // "--capture" flag. Unknown/malformed args => Mode=None, Program.Main prints usage, exit 1.
    public static CliOptions Parse(string[] args) { /* WP-X2 implements; see §3.2 responsibilities */ }
}

public static class AppComposition
{
    public static Func<RoeSnipSettings>? LoadSettings { get; set; }              // set by RoeSnip.Core.Settings.SettingsStore.Load directly — no hook needed, Core has no ModuleInitializer dependency on App
    public static Func<IReadOnlyList<(CapturedFrame Frame, SdrImage Preview)>, RoeSnipSettings, Task<OverlayResult?>>? RunOverlay { get; set; } // set by Overlay/OverlayController.cs (WP-X3)
    public static Action<string, CapturedFrame, RectPhysical>? WriteHdrExport { get; set; }  // set by RoeSnip.Platform.Windows's JxrWriter (WP-X2) — null on non-Windows builds/RIDs, exactly like the WPF app's WriteJxr being null before WP-C landed
    public static Func<string[], int>? RunTrayApp { get; set; }                  // set by AppShell/TrayApp.cs (WP-X2)

    public static int RunDiagCli() { /* same shape as PLAN.md's RunDiagCli, using CaptureService().EnumerateMonitors() */ }
    public static int RunCaptureCli(CliOptions cli) { /* same shape as PLAN.md's RunCaptureCli */ }
    public static int RunTray(string[] args) { /* same shape as PLAN.md's RunTray */ }
    public static async Task RunCaptureFlowAsync(RoeSnipSettings settings, ITrayNotifier? notifier) { /* same shape, using CaptureService().SupportsHdrExport to gate the Save-HDR branch instead of always assuming Windows */ }

    // New: implements the "capture"/"settings" bare verbs by signalling a running instance over
    // AppShell/SingleInstance.cs (WP-X2), or becoming the resident instance itself if none exists.
    public static int RunTriggerCapture() { /* WP-X2 implements; see §3.2 */ }
    public static int RunTriggerSettings() { /* WP-X2 implements; see §3.2 */ }
}

public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        var cli = CliOptions.Parse(args);
        return cli.Mode switch
        {
            CliMode.Diag => AppComposition.RunDiagCli(),
            CliMode.Capture => AppComposition.RunCaptureCli(cli),
            CliMode.TriggerCapture => AppComposition.RunTriggerCapture(),
            CliMode.TriggerSettings => AppComposition.RunTriggerSettings(),
            _ => AppComposition.RunTray(args),
        };
    }
}
```

One deliberate departure from Phase 1's shape: `LoadSettings` does not strictly need to be a
nullable hook set via `[ModuleInitializer]` anymore, because `SettingsStore` now lives in
`RoeSnip.Core` (§2.6), which `Program.cs` already references directly — WP-X2 may call
`SettingsStore.Load()` directly instead of going through a hook if that's simpler; the hook is kept
in the contract above only for drop-in structural parity with Phase 1's pattern and to preserve the
"testable before every package lands" property PLAN.md §2.4 relied on. Note this choice in your
final report if you take the direct-call route instead — it's a legitimate simplification, not a
deviation from behavior.

---

## 3. Work packages

### 3.1 WP-X1 — Core extraction

**Files owned:** `src/RoeSnip.Core/RoeSnip.Core.csproj` and everything under
`src/RoeSnip.Core/**`; `tests/RoeSnip.Core.Tests/RoeSnip.Core.Tests.csproj` and everything under
`tests/RoeSnip.Core.Tests/**`.

**Responsibilities:** implement §2.1–§2.7 exactly as specified, extracting from (never editing)
`src/RoeSnip/**`. Port and adapt the existing golden tests:

- `ColorMathTests.cs`, `ToneMapperTests.cs` — port from `tests/RoeSnip.Tests/`. `ToneMapperTests.cs`
  needs one mechanical update: every synthetic `CapturedFrame` the tests construct must now also
  supply `sdrWhiteInBufferUnits: sdrWhiteNits / 80.0` (the Windows convention) at construction time —
  this keeps every existing golden numeric assertion (byte 255 for scRGB 3.0 @ 240 nits SDR white,
  byte 188 for scRGB 1.5, etc.) valid **unchanged**, per the algebraic identity in §2.2/§2.5. Add
  (new, not in the WPF app's test file) at least one case with `sdrWhiteInBufferUnits = 1.0` and a
  buffer value of e.g. `2.5` to exercise the macOS-EDR-style convention path and confirm the
  generalized formula, not just the Windows-equivalent one, actually shoulders correctly.
- `SettingsTests.cs` — ports near-verbatim against `RoeSnip.Core.Settings.SettingsStore`'s
  path-taking overloads; same fail-closed/atomic-write assertions.
- `CaptureCacheTests.cs`, `FrameSanityTests.cs` — port verbatim (constructors/behavior unchanged).
- `FallbackCaptureBackendTests.cs` (new — replaces `CaptureServiceTests.cs`'s role): port the WPF
  app's `CaptureServiceTests.cs` fake-primary/fake-fallback/ordering/parallelism assertions, but
  construct a `FallbackCaptureBackend` with two fake `IScreenCapturer`s instead of the old
  `CaptureService(IScreenCapturer, IScreenCapturer, CaptureCache)` 3-arg constructor (which no
  longer exists — `CaptureService` is now the 1-arg `ICaptureBackend`-wrapping facade in §2.3).
  Preserve every existing assertion's intent: primary success short-circuits fallback; primary
  failure falls back and persists a cache memo; both failing omits the monitor without throwing;
  output preserves input monitor order regardless of completion order; a monitor previously marked
  broken in the cache skips straight to the next capturer on a fresh `FallbackCaptureBackend`
  instance pointed at the same cache file (simulating a relaunch).

**Acceptance:** `dotnet test tests/RoeSnip.Core.Tests` all green, with zero references to
`System.Windows.*` or any Windows-only API anywhere under `src/RoeSnip.Core/`.

### 3.2 WP-X2 — Windows backend + App shell

**Files owned:**
`src/RoeSnip.Platform.Windows/RoeSnip.Platform.Windows.csproj` and everything under
`src/RoeSnip.Platform.Windows/**`;
`tests/RoeSnip.Platform.Windows.Tests/RoeSnip.Platform.Windows.Tests.csproj` and everything under it;
in `src/RoeSnip.App/`: `RoeSnip.App.csproj`, `app.manifest`, `Program.cs`, `App.axaml`,
`App.axaml.cs`, and everything under `AppShell/`.

**Consumes (read-only):** everything in `RoeSnip.Core` (§2); from WP-X3, only the `RunOverlay`
hook's signature (§2.8) and `OverlayController`'s registration of it — never any concrete
`Overlay/*` type.

**Responsibilities — Platform.Windows (mechanical port, behavior identical to `src/RoeSnip/`):**

- Port `Interop/NativeMethods.cs` verbatim (PLAN.md §5's full P/Invoke appendix — unchanged; every
  struct/DllImport/const in that appendix still applies as-is).
- Port `MonitorEnumerator`'s algorithm (PLAN.md §3.1's numbered steps: `EnumDisplayMonitors` +
  `GetDpiForMonitor`, DXGI `IDXGIOutput6.GetDesc1()`, `QueryDisplayConfig`/`DisplayConfigGetDeviceInfo`
  SDR white level) into an instance method on the new `WindowsCaptureBackend : ICaptureBackend`,
  changing only the output shape: build the portable `MonitorInfo` (§2.1) with
  `BackendKey = "0x" + hMonitor.ToString("X")` and `Scale = dpiX / 96.0`.
  `AdvancedColorActive`/`SdrWhiteNits`/`MaxLuminanceNits` default values and failure-handling are
  unchanged (240 nits / 1000 nits defaults, log-and-continue on every per-path failure).
- Port `DesktopDuplicationCapturer.cs` and `WgcCapturer.cs` unchanged in algorithm (PLAN.md §3.1's
  DD/WGC steps — `DuplicateOutput1` with both formats, the `AcquireNextFrame` retry loop, the
  staging-texture-then-`Map` copy respecting `RowPitch`, `DXGI_ERROR_ACCESS_LOST` handling for DD;
  `CreateForMonitor` interop + `CreateFreeThreaded` + `IsCursorCaptureEnabled=false` for WGC), except
  each capturer's `Capture(MonitorInfo)` now constructs `CapturedFrame` with the new 7th constructor
  argument: `sdrWhiteInBufferUnits: monitor.SdrWhiteNits / 80.0`, for BOTH `Fp16ScRgb` and
  `Bgra8Srgb` results (§2.2's note: the value is functionally unused for `Bgra8Srgb` but must still
  be a valid, non-NaN number, and using the same formula for both formats is simplest/least
  surprising). Also apply the DD black-frame quirk check (`FrameSanity.IsAllZero`, §2.4) exactly as
  the WPF app does — throw `CaptureException` on an all-zero DD buffer so `FallbackCaptureBackend`
  falls to WGC.
- `WindowsCaptureBackend` composes these into a `FallbackCaptureBackend` (§2.3):
  `new FallbackCaptureBackend("Windows (Desktop Duplication/WGC)", supportsHdrExport: true, EnumerateMonitors, new IScreenCapturer[] { desktopDuplicationCapturer, wgcCapturer }, CaptureCache.Default)`.
  Register it at the bottom of the file:
  ```csharp
  file static class ModuleInit
  {
      [System.Runtime.CompilerServices.ModuleInitializer]
      internal static void Init() => CaptureBackendRegistry.Register(
          () => OperatingSystem.IsWindows(), () => new WindowsCaptureBackend());
  }
  ```
- Port `JxrWriter.cs` verbatim (PLAN.md §3.3's exact WIC-direct encoder choice — WPF's
  `WmpBitmapEncoder` still fails the round-trip acceptance gate for the same documented reason; do
  not re-litigate that choice without re-running the gate). `JxrWriter.Write`'s body is unchanged
  except it now reads `CapturedFrame.ReadPixelScRgb` (§2.2, unchanged signature) — no functional
  edit needed at all, only the namespace/project move.
- Port `tests/RoeSnip.Tests/JxrRoundTripTests.cs` into
  `tests/RoeSnip.Platform.Windows.Tests/JxrRoundTripTests.cs` unchanged — same 3.0-in/&gt;1.0-out
  acceptance assertion.

**Responsibilities — App shell (`src/RoeSnip.App/`):**

- `Program.cs` per §2.8. `CliOptions.Parse` extends the WPF app's grammar (PLAN.md's exact
  `--diag`/`--capture [--monitor N] [--out path] [--jxr]` parsing, unchanged) with two new bare
  verbs: `RoeSnip capture` and `RoeSnip settings`.
- `AppComposition.RunTriggerCapture()`/`RunTriggerSettings()`: try to signal an already-running
  instance over `AppShell/SingleInstance.cs` (below); if one responds, exit 0 immediately (the
  signalled instance does the work, exactly like the WPF app's existing second-instance behavior,
  PLAN.md §3.3). If no instance is running, this process **becomes** the resident instance — run
  the normal `RunTray`-equivalent startup (load settings, attempt hotkey registration where
  supported, hold the single-instance mutex/pipe) and then immediately perform the requested action
  (trigger the capture flow, or open the settings window) — it does NOT exit after one shot. This is
  the plan-level decision that makes `RoeSnip capture` bound to a Linux DE keyboard shortcut work
  correctly as the ongoing "primary activation path" DESIGN-XPLAT.md describes (a keyboard shortcut
  that only ever worked while some other process kept the app alive would be pointless) — see §6
  flag 4.
- `AppShell/SingleInstance.cs`: port the WPF app's mutex + named-pipe pattern
  (`src/RoeSnip/App/TrayApp.cs`'s `SignalExistingInstance`/`ListenForSignalAsync`, PLAN.md's
  §3.3 shape) with one change: the mutex name is OS-conditional —
  `OperatingSystem.IsWindows() ? @"Global\RoeSnip-SingleInstance" : "RoeSnip-SingleInstance"` (the
  `Global\` Terminal-Services-session prefix is meaningless outside Windows — see §5's
  cross-platform-Mutex/NamedPipe facts for why the pipe itself needs no such change).
  Extend the single signal byte to a small signal enum (`1 = TriggerCapture`, `2 = TriggerSettings`,
  matching the two new CLI verbs) instead of the WPF app's fixed single meaning.
- `AppShell/TrayApp.cs`: Avalonia's `TrayIcon` (`Avalonia.Controls.TrayIcon`) + a native menu,
  wired to the same `AppComposition.RunCaptureFlowAsync`/`OpenSettings` calls as the WPF app's
  `TrayApp.cs` (PLAN.md §3.3), but the tray icon itself must be created defensively — per
  DESIGN-XPLAT.md, tray is "STRICTLY optional" (Linux `StatusNotifier` may simply not render it);
  a failure to create/show the tray icon must be caught, logged, and must NOT prevent the rest of
  startup (hotkey registration, the single-instance pipe listener) from proceeding — the app must
  remain fully operable via CLI verbs alone in that case.
- `AppShell/HotkeyManager.cs`: wraps `SharpHook`'s `TaskPoolGlobalHook` (or `SimpleGlobalHook` — pick
  whichever the implementer finds cleaner; both are documented in §5). Per DESIGN-XPLAT.md: start
  the hook on Windows and macOS unconditionally; on Linux, start it ONLY when
  `Environment.GetEnvironmentVariable("XDG_SESSION_TYPE")` is NOT `"wayland"` (libuiohook/SharpHook
  is X11-only, confirmed in §5) — on Wayland, log once to stderr/tray-balloon-if-available that the
  primary activation is a DE-bound `RoeSnip capture` shortcut, and do not attempt to start the hook
  at all (starting-then-failing vs. never-starting both end in "no global hotkey," but never-starting
  avoids a confusing failure log on every single launch). The one-time PrintScreen/Snipping-Tool
  consent flow (`src/RoeSnip/App/TrayApp.cs`'s `ResolvePrintScreenConsent`) ports **unchanged, and
  Windows-only** — guard its entire call behind `OperatingSystem.IsWindows()`; on other OSes,
  `HotkeyManager.Register` just registers the hook combination directly with no registry check.
- `AppShell/SettingsWindow.axaml(.cs)`: Avalonia port of `src/RoeSnip/App/SettingsWindow.xaml(.cs)` —
  same fields (hotkey capture control, save directory picker, auto-save-HDR-copy toggle, tone-map
  knee/peak overrides, run-at-startup toggle, copy-on-select toggle). The "Save HDR" auto-save
  toggle and the tone-map overrides remain visible on every OS (they're just settings values); the
  run-at-startup toggle should call through to `StartupManager` (below), which itself decides
  per-OS behavior.
- `AppShell/StartupManager.cs`: ports `src/RoeSnip/App/StartupManager.cs`'s HKCU Run-key logic
  **unchanged, guarded by `OperatingSystem.IsWindows()`**. On macOS/Linux, DESIGN-XPLAT.md does not
  specify run-at-startup behavior — implement `SetRunAtStartup`/`IsRunAtStartupEnabled` as a
  documented no-op that logs to stderr ("run-at-startup is not yet implemented on this OS") rather
  than throwing or silently lying about the toggle's effect; this is a genuine gap, see §6 flag 5.

**Acceptance:** `dotnet build src/RoeSnip.Platform.Windows` and
`dotnet test tests/RoeSnip.Platform.Windows.Tests` both succeed on this machine; the App shell
compiles once WP-X3's `Overlay/*` stubs exist (or once real `Overlay/*` lands — WP-X2 can develop
against a temporary local stub implementing `RunOverlay`'s signature if X3 hasn't landed yet, exactly
as PLAN.md's own "each package's own acceptance test is designed to be checkable" principle intends).

### 3.3 WP-X3 — Avalonia overlay

**Files owned:** everything under `src/RoeSnip.App/Overlay/**`.

**Consumes (read-only):** `RoeSnip.Core.Capture.{CapturedFrame, MonitorInfo, RectPhysical}`,
`RoeSnip.Core.Imaging.SdrImage`, `RoeSnip.Core.Settings.RoeSnipSettings`, and from
`RoeSnip.App/Program.cs`: `OverlayResult`, `AppComposition`. Does **not** reference
`RoeSnip.Platform.Windows.JxrWriter` or anything under `AppShell/` — exactly the same seam
discipline as PLAN.md §3.2's WP-B (the HDR export and "saved" balloon are threaded back through
`AppComposition` after `RunAsync` returns).

**Responsibilities:** this is a straight port of the WPF app's Overlay UX (the "Phase-A additions"
the briefing calls out — persisted capture cache is already handled by Core/WP-X2; two-stage Esc,
click color inspector, pictogram toolbar are THIS package's job) from WPF windows/controls to
Avalonia windows/controls. Read `src/RoeSnip/Overlay/*.cs` and `.xaml` in full before starting — the
UX spec below is a summary of what must be preserved bit-for-bit in interaction terms, not a
replacement for reading the actual behavior reference:

- **`OverlayController.cs`**: port `OverlayCommand` enum and the `OverlaySession` orchestration
  logic verbatim in *behavior* (two-stage Esc exactly as documented in
  `src/RoeSnip/Overlay/OverlayController.cs`'s own doc comments: stage 1 dismisses an open
  color-info panel across all monitors, stage 2 clears an in-progress snip on whichever monitor has
  one, stage 3 — nothing active — closes the whole overlay; the toolbar's Cancel/X button always
  closes outright, never staged). Selection-lives-on-one-monitor, mouse-enter-activates-that-window,
  all-windows-close-together — all unchanged. `EnsureApplication`'s WPF-`Application`-singleton
  dance has no Avalonia equivalent needed (Avalonia's `Application.Current` is already established
  by `App.axaml.cs`'s startup, which WP-X2 owns) — just create and `Show()` one
  `OverlayWindow` per monitor directly from `OverlayController.RunAsync`.
- **Multi-monitor window placement — the mixed-DPI discipline, ported to Avalonia's own rules**:
  DESIGN-XPLAT.md is explicit here and it is the single highest-risk item in this package. Each
  `OverlayWindow`: `SystemDecorations = SystemDecorations.None`, `Topmost = true`,
  `ShowInTaskbar = false`, `WindowStartupLocation = WindowStartupLocation.Manual`. Set
  `window.Position` (an Avalonia `PixelPoint`, i.e. **physical** pixels) from the correlated
  `Screen.Bounds` (also physical) **before** calling `Show()` — never move a shown window across
  monitors afterward (Avalonia issues #13917/#17834, cited in DESIGN-XPLAT.md, are exactly the
  WPF `SetWindowPos`-during-`SourceInitialized` bug class ported to a new framework; the fix is the
  same shape: position first, show second, never reposition a mixed-DPI window post-show). Size the
  window from `Screen.Bounds` divided by `Screen.Scaling` (Avalonia's own window `Width`/`Height`
  are in DIPs, same "DIPs never leave the view layer" rule as PLAN.md §0.4 — **all**
  selection/annotation/magnifier/crop math stays in physical pixels exactly like the WPF version;
  only the final on-screen rendering divides by `Screen.Scaling` at the point of use, mirroring the
  WPF app's `CompositionTarget.TransformToDevice`/`_scaleX`/`_scaleY` pattern
  (`src/RoeSnip/Overlay/OverlayWindow.xaml.cs`) almost verbatim — Avalonia's `Screen.Scaling` is the
  direct analog of that transform's `M11`/`M22`.
  **This machine is all-96-DPI (scale 1.0 everywhere)**, so this math cannot be exercised by running
  it here — it must be reviewed line-by-line against the WPF version's proven-correct math, not just
  "it ran and looked fine," per DESIGN-XPLAT.md's explicit instruction. Flag any place you're not
  fully confident is correct in your final report rather than shipping a guess.
- **Correlating a `CapturedFrame`'s `MonitorInfo` to the right Avalonia `Screen`**: Core's capture
  backend and Avalonia's own `Screens.All` are two INDEPENDENT enumerations of the same physical
  monitors — there is no shared handle between them (that's exactly why `MonitorInfo.BackendKey` is
  opaque, §2.1). Match by `RectPhysical` equality against `Screen.Bounds` (both describe the same
  virtual-desktop physical-pixel rectangles, so they should match exactly on a correctly functioning
  system); if no exact match is found for a given monitor (e.g. a monitor unplugged between capture
  and overlay show — rare but must not crash), skip that monitor's overlay window and log to stderr
  rather than guessing a match. This correlation function belongs in `OverlayController.cs`.
- **`OverlayWindow.axaml(.cs)`**: preview image via a control bound to the `WriteableBitmap`
  produced by `SdrImageAvaloniaExtensions.ToAvaloniaBitmap()` (below), `Stretch="Fill"`, nearest-
  neighbor scaling (Avalonia's `RenderOptions.BitmapInterpolationMode = BitmapInterpolationMode.None`
  on the `Image` control — the analog of the WPF version's
  `RenderOptions.SetBitmapScalingMode(..., NearestNeighbor)`). Port the drag/click-threshold logic
  verbatim: **the 4px `ClickDragThresholdPx` constant and its exact role** (a mouse-down that hasn't
  yet travelled past the threshold is a color-inspection click candidate, not a selection drag — see
  `src/RoeSnip/Overlay/OverlayWindow.xaml.cs`'s `OnPreviewMouseLeftButtonDown`/`OnPreviewMouseMove`/
  `OnPreviewMouseLeftButtonUp` state machine) must be preserved exactly; this is one of the two
  Phase-A UX features the briefing specifically calls out. Port the click-to-inspect color-info
  panel (`ShowColorInfo`/`DismissColorInfo`) with the same content (swatch, hex auto-copied to
  clipboard, R/G/B bytes from the tone-mapped preview, nits from the raw `CapturedFrame` via
  `ReadPixelNits`, the same &gt;250-nits amber-highlight threshold) and the same dismissal rules
  (next click anywhere, starting a drag, or Esc stage 1). Port inline text annotation via an
  Avalonia `TextBox` positioned over the canvas the same way the WPF version does (native IME
  support is why it's a real `TextBox` rather than a custom editor, in both frameworks) — remember
  this tool is explicitly cuttable if it endangers the milestone (PLAN.md's original allowance,
  still true here); the other five annotation tools are not.
- **`SelectionAdorner.cs`, `Magnifier.cs`, `AnnotationLayer.cs`**: port the hit-testing/rendering
  logic from WPF's `DrawingContext`/`FrameworkElement.OnRender` to Avalonia's own (very similar)
  `DrawingContext`/`Control.Render` APIs. All physical-pixel-in/DIP-out geometry, the 8-handle
  hit-test regions, the pixelated loupe + hex/RGB/nits three-line readout with the same &gt;250-nits
  highlight rule, the Hermite-free rectangle/ellipse/arrow/line/freehand rendering and the linear
  undo stack — all port with the same behavior, framework-appropriate API names.
- **`ToolbarControl.axaml(.cs)`**: port the pictogram toolbar exactly — inline vector `Path` icons
  (no image assets, matching `src/RoeSnip/Overlay/ToolbarControl.xaml`'s own icon geometries can be
  reused as Avalonia `PathGeometry` data strings almost unchanged, since both frameworks use the
  same mini-language for path data), always-visible buttons (no hover-only-reveal, per the product's
  explicit "no hidden controls" requirement — this project's own convention, matching this user's
  established UI-style preference recorded elsewhere), every button `Focusable="False"` so
  Esc/Enter/Ctrl+… keep reaching the window's key handler right after a toolbar click, the same
  color/stroke-width preset swatches, the same below-right attached placement with above-flip when
  it would go off-screen.
- **`ClipboardService.cs`** (per DESIGN-XPLAT.md, clipboard adapters are listed under `RoeSnip.App`,
  not a Platform.* project — no separate assembly needed, single file with runtime OS branches):
  - Windows: reuse the exact P/Invoke PNG+`CF_DIBV5` transaction from
    `src/RoeSnip/Imaging/ClipboardService.cs` (`OpenClipboard`/`EmptyClipboard`/`SetClipboardData`
    ×2/`CloseClipboard`, the `BITMAPV5HEADER` struct, `GlobalAlloc`/`GlobalLock` handling) — these
    `DllImport("user32.dll")`/`DllImport("kernel32.dll")` declarations compile on every host OS
    (only the actual P/Invoke *calls*, guarded by `OperatingSystem.IsWindows()`, need to be
    Windows-only at runtime), so this can live directly in `RoeSnip.App` without a separate
    Platform.Windows dependency for clipboard specifically.
  - macOS: `NSPasteboard` PNG write. Two viable approaches per DESIGN-XPLAT.md ("via helper or
    objc") — prefer a small Objective-C-runtime P/Invoke (`objc_msgSend` via `libobjc.dylib`,
    `NSPasteboard.generalPasteboard`, `setData:forType:NSPasteboardTypePNG`) to avoid growing the
    Swift helper's scope beyond capture; if that proves too fiddly to get right without a macOS
    machine to test on, shelling out to the `scksnap` helper (WP-X5, §3.5) for a "write PNG to
    clipboard" verb is an acceptable fallback — note which approach you took in your final report,
    since neither can be runtime-verified on this machine (§4).
  - Linux (and as an available path on Windows/macOS too, if simpler): Avalonia's own `IClipboard`
    (`TopLevel.GetTopLevel(window)?.Clipboard`) `SetDataObjectAsync` with an `image/png` format —
    per DESIGN-XPLAT.md, Avalonia 12 fixed X11 INCR support for exactly this kind of larger clipboard
    payload (verified: Avalonia 12.0.0's release notes include an X11 INCR clipboard-transfer fix),
    so this path is expected to actually work for full-size screenshots now, not just thumbnails.
- **`SdrImageAvaloniaExtensions.cs`**: the Core/UI seam —
  ```csharp
  public static WriteableBitmap ToAvaloniaBitmap(this SdrImage image)
  {
      var wb = new WriteableBitmap(
          new PixelSize(image.Width, image.Height), new Vector(96, 96),
          PixelFormat.Bgra8888, AlphaFormat.Unpremul);
      using var fb = wb.Lock();
      for (int y = 0; y < image.Height; y++)
      {
          System.Runtime.InteropServices.Marshal.Copy(
              image.Pixels, y * image.Stride, fb.Address + y * fb.RowBytes, image.Stride);
      }
      return wb;
  }
  ```
  (Row-by-row copy, not one bulk copy, because `WriteableBitmap`'s locked buffer's `RowBytes` is not
  guaranteed to equal `SdrImage.Stride` even though both happen to be `Width*4` today — copying
  row-by-row is the same defensive discipline the WPF `BitmapSource.Create` call implicitly didn't
  need but this one does, since `ILockedFramebuffer.RowBytes` is an Avalonia/backend implementation
  detail, not something `SdrImage` controls.)

**Acceptance criteria:** manual/visual, same as PLAN.md §3.2's WP-B acceptance list — overlay shows
the frozen frame, drag selects a region, handles resize it, Esc's three stages behave exactly as
documented, Enter/double-click confirms, Ctrl+C copies (flash cue), Ctrl+S saves, all annotation
tools draw and undo (Text may be cut per the standing allowance), the magnifier and click color
inspector both show RGB/hex + nits with click-to-copy, multi-monitor selection/keyboard-broadcast/
close-together semantics hold, and — the two Phase-A features the briefing calls out by name — the
4px click-vs-drag threshold and the two-stage Esc both behave exactly as in the WPF app.

### 3.4 WP-X4 — Linux backend

**Files owned:** `src/RoeSnip.Platform.Linux/RoeSnip.Platform.Linux.csproj` and everything under
`src/RoeSnip.Platform.Linux/**`.

**Consumes (read-only):** `RoeSnip.Core` (§2) only.

**Responsibilities:**

- **Primary: `PortalScreenshotCapturer.cs`** — `org.freedesktop.portal.Screenshot` via `Tmds.DBus`.
  Call the portal's `Screenshot(string parent_window, Dictionary<string,object> options)` method on
  the session bus (`org.freedesktop.portal.Desktop` at `/org/freedesktop/portal/desktop`), with
  `options["interactive"] = false` (a silent, non-interactive shot — GNOME may still show a
  one-time/per-shot permission prompt per its own portal implementation, §5; this is documented, not
  fought, per DESIGN-XPLAT.md). The method returns a `request` object path; subscribe to that
  request object's `org.freedesktop.portal.Request.Response` signal (via `Tmds.DBus`'s
  `WatchResponseAsync`-style proxy pattern) to get the actual result dictionary, which contains a
  `uri` (a `file://` URI to a temp PNG of the WHOLE virtual desktop). Decode that PNG via SkiaSharp
  (`SkiaSharp` package, §1.6), then slice per monitor using Avalonia's own screen geometry — this
  means `PortalScreenshotCapturer` needs monitor bounds from SOMEWHERE, and Core's `ICaptureBackend`
  contract doesn't have access to Avalonia's `Screens` API (Core has zero UI deps). Resolve this by
  having `LinuxCaptureBackend.EnumerateMonitors()` get its monitor list a different way — via X11
  RandR (`XRRGetScreenResources`/`XRRGetOutputInfo` through the same `libX11`/`libXrandr` P/Invoke
  layer `X11Capturer` already needs for its own fallback, below) rather than depending on Avalonia at
  all; this keeps `RoeSnip.Platform.Linux` independent of `RoeSnip.App`/Avalonia, consistent with
  every other Platform.* project.
  **Verify the pixel scale at runtime**: compare the decoded PNG's dimensions against the SUM of the
  enumerated monitors' bounds; if the PNG is larger (HiDPI portals return physical pixels while RandR
  bounds may be reported in logical/scaled units on some compositors — DESIGN-XPLAT.md flags this
  explicitly), compute and apply the actual scale factor discovered (`pngWidth / summedLogicalWidth`)
  rather than assuming it always matches 1:1. Log the discovered scale to stderr on every capture in
  `--diag`-style output so a mismatch is visible, not silent.
- **Fallback: `X11Capturer.cs`** — raw `XGetImage` via `libX11.so.6` P/Invoke for portal-less X
  sessions (no portal service running, or the call times out/errors). Declare only the specific
  Xlib functions needed (`XOpenDisplay`, `XGetImage` or `XGetSubImage`, `XDefaultRootWindow`,
  `XRRGetScreenResources`/`XRRGetOutputInfo` from `libXrandr.so.2` for monitor enumeration/bounds,
  `XCloseDisplay`) as local `DllImport`s in this file — these compile everywhere (§1.6) and only need
  the actual `.so` at runtime on a real X11 Linux box. Output format is `Bgra8Srgb` (X11 pixmaps are
  SDR by definition; there is no HDR capture path on Linux in v1, per DESIGN-XPLAT.md).
- **`LinuxCaptureBackend : ICaptureBackend`**: composes `PortalScreenshotCapturer` then `X11Capturer`
  into a `FallbackCaptureBackend("Linux (xdg-desktop-portal/X11)", supportsHdrExport: false,
  EnumerateMonitors, new IScreenCapturer[] { portalCapturer, x11Capturer }, CaptureCache.Default)` —
  same reused Core infrastructure WP-X2 uses (§2.3), so this package gets the exact same
  once-broken-skip-forever memo behavior "for free." `EnumerateMonitors` uses the RandR path
  described above regardless of which capturer ultimately succeeds (both need the same monitor
  bounds list). Set every `MonitorInfo.SdrWhiteNits` to `240.0` and `AdvancedColorActive` to
  `false` unconditionally (no ACM/HDR concept attempted on Linux in v1 — see §6 flag 6 for why 240
  specifically). Set every `CapturedFrame.SdrWhiteInBufferUnits` to `1.0` (the documented "n/a"
  sentinel, §2.2 — these are always `Bgra8Srgb` frames). Register via `[ModuleInitializer]`:
  ```csharp
  file static class ModuleInit
  {
      [System.Runtime.CompilerServices.ModuleInitializer]
      internal static void Init() => CaptureBackendRegistry.Register(
          () => OperatingSystem.IsLinux(), () => new LinuxCaptureBackend());
  }
  ```

**Acceptance:** `dotnet build src/RoeSnip.Platform.Linux` succeeds on this Windows machine (compile
gate, §1.7). If WSLg is available (§4), a runtime smoke test of both the portal and X11 paths.

### 3.5 WP-X5 — macOS backend

**Files owned:** `src/RoeSnip.Platform.MacOS/RoeSnip.Platform.MacOS.csproj` and everything under
`src/RoeSnip.Platform.MacOS/**`; `helpers/scksnap/**`.

**Consumes (read-only):** `RoeSnip.Core` (§2) only.

**Responsibilities:**

- **`helpers/scksnap/`** — Swift source implementing the exact capture matrix from
  DESIGN-XPLAT.md: enumerate displays (`SCShareableContent.current` or
  `CGGetActiveDisplayList`/`CGDisplayCopyDisplayMode` for bounds+scale); capture one full display
  (never `captureImageInRect` — that's 15.2+ only, per DESIGN-XPLAT.md; always capture the full
  display and let the .NET side crop) via:
  - macOS 15+ on Apple Silicon: `SCStreamConfiguration.captureDynamicRange = .hdrLocalDisplay` (or
    the current SDK's equivalent constant — verify against the actual Xcode/SDK version the GitHub
    Actions macOS runner provides at build time, since this is exactly the kind of API surface that
    shifts between SDK point releases) via `SCScreenshotManager` for a one-shot HDR capture.
  - macOS 14 / Intel: `SCScreenshotManager` in plain (SDR) mode, or `CGDisplayCreateImage` pre-14.
  Write output as raw FP16 pixels (width/height/stride/EDR headroom/display bounds/scale) plus a
  small metadata header/sidecar to a temp file; print the temp file path (and any error) to stdout
  for the .NET side to read. A TCC (Screen Recording permission) denial must produce a distinct,
  documented exit code — this is a first-class, UI-surfaced error per DESIGN-XPLAT.md, not a generic
  failure. Ad-hoc-sign the helper with a **stable** identifier (matches the `jxr-helper`/CEF-helper
  precedent already established in this codebase's launcher work — see this repo's own memory notes
  on stable code-signing identifiers mattering for TCC attribution) so a user's one-time Screen
  Recording grant survives helper rebuilds.
- **GitHub Actions workflow** (`helpers/scksnap/.github/workflows/` or the repo-root
  `.github/workflows/` per this repo's existing convention — check for one) — macOS runner, builds
  the Swift helper, uploads it as a build artifact (or commits the built binary somewhere the .NET
  side's publish step can pick it up — decide based on how this repo already handles the analogous
  jxa-helper CEF binary, and match that convention rather than inventing a new one).
- **`ScksnapHelperClient.cs`** (the thin .NET shell-out side): locates the helper binary (bundled
  alongside the published app, e.g. `Contents/MacOS/scksnap` in an app-bundle layout, or a
  flat `scksnap` next to the main executable if this app isn't bundled as a `.app` — decide based on
  whatever Avalonia's own macOS publish output shape turns out to be and document the choice),
  invokes it via `Process.Start` with the requested display + capture-full-display semantics,
  reads back the metadata + raw pixel temp file, and constructs a portable `CapturedFrame` with
  `Format = FrameFormat.Fp16ScRgb` and `SdrWhiteInBufferUnits = 1.0` (§2.2's macOS EDR convention).
  `MonitorInfo.SdrWhiteNits` — see §6 flag 3, no OS-reported absolute value exists; use the
  documented placeholder (240.0, matching the same fallback used for Linux and Windows'
  query-failure default, for consistency) unless/until product guidance says otherwise.
  `MonitorInfo.MaxLuminanceNits` — derive from the helper's reported EDR headroom multiplier ×
  `SdrWhiteNits` (e.g. headroom 2.0 → `MaxLuminanceNits = 2.0 * SdrWhiteNits`), so `ToneMapper`'s
  existing `peak` derivation formula (§2.5, unchanged) still produces a sensible shoulder without
  needing its own macOS-specific branch.
- **`MacCaptureBackend : ICaptureBackend`**: since there's exactly one capturer (no DD/WGC-style
  fallback chain — a helper-invocation failure has nowhere else to fall back to), implement
  `ICaptureBackend` directly (not via `FallbackCaptureBackend`) — enumerate displays by invoking the
  helper's list-displays verb, capture by invoking its capture verb per requested monitor.
  `SupportsHdrExport => false` (v1 Save-HDR is Windows-only per DESIGN-XPLAT.md — even though macOS
  frames technically carry real HDR headroom, there is no `.jxr`-equivalent export path defined for
  them in v1; this is a scope decision already made by DESIGN-XPLAT, not something to relitigate).
  Register via `[ModuleInitializer]` exactly like the other two backends, keyed on
  `OperatingSystem.IsMacOS()`.

**Acceptance:** `dotnet build src/RoeSnip.Platform.MacOS` succeeds on this Windows machine (the .NET
side never needs macOS to compile, per §1.5). The Swift helper and its GitHub Actions workflow are
"built, not hardware-validated" — no runtime verification is possible on this machine (§4); do not
claim otherwise in any status report.

### 3.6 Sequencing

- **X1 first** — every other package references `RoeSnip.Core`. X1 can be fully built and tested in
  isolation (its own `.csproj` + test project need nothing from any other package).
- **X2, X3, X4, X5 in parallel** once X1 lands. X2 and X3 do not collide: X2 owns
  `src/RoeSnip.App/{Program.cs, App.axaml(.cs), AppShell/**}` plus all of
  `src/RoeSnip.Platform.Windows/**`; X3 owns only `src/RoeSnip.App/Overlay/**`. The seam between
  them is exactly `Program.cs`'s `AppComposition.RunOverlay` hook (§2.8) and the `OverlayResult`
  record it returns — X3 references `AppComposition`/`OverlayResult` (defined in an X2-owned file)
  but never edits that file, exactly mirroring PLAN.md §3.2's WP-B/WP-A relationship. X4 and X5 are
  fully independent of X2/X3 (they only touch Core-derived types) and of each other.
- **Integration** after all five report done — §4.

---

## 4. Integration & verification sequence

1. `dotnet build RoeSnip.sln` (no `-r` — the design-time build referencing all three platform
   projects, §1.7). This is the first point every package's real code compiles against every other
   package's real code rather than a stub. Expect signature-drift friction exactly as PLAN.md §4
   warns about — fix by re-reading §2 of this document, never by unilaterally changing a contract
   without re-checking every consumer.
2. `dotnet test tests/RoeSnip.Core.Tests` — all green.
3. `dotnet test tests/RoeSnip.Platform.Windows.Tests` — all green (the JXR round-trip acceptance
   gate, unchanged from Phase 1).
4. **Windows interactive parity checklist** (this machine, real monitors, interactive session —
   Desktop Duplication needs one, same caveat as PLAN.md §4 step 3):
   - `dotnet run --project src/RoeSnip.App -- --diag` — one line per real monitor, sane values,
     matches what `src/RoeSnip/`'s own `--diag` reports for the same machine (a direct A/B check
     Phase 1 didn't have available — use it).
   - `dotnet run --project src/RoeSnip.App -- --capture --monitor 0` and `--jxr` variant — PNG (and
     JXR) written, dimensions match, nits stats sane.
   - `dotnet run --project src/RoeSnip.App -- capture` then `dotnet run --project src/RoeSnip.App -- capture` again while the first is still resident — second invocation signals the first (does not
     spawn a second tray icon / second hotkey registration); verify via a distinguishing side effect
     (e.g. the balloon or the overlay appearing only once per signal, not twice).
   - Launch with no args (tray mode): tray icon appears (or its absence is at least logged, not
     silent, if `TrayIcon` creation fails for some reason); hotkey triggers the overlay; full
     selection → annotate → Copy pastes correctly into Paint/Notepad/a browser; Save writes a PNG
     and shows the balloon; the two-stage Esc and the 4px click-vs-drag threshold both behave
     exactly as they do in `src/RoeSnip/` today — this is the direct pixel-for-pixel A/B check that
     matters most, since both apps can run side by side on this exact machine.
   - `dotnet publish src/RoeSnip.App -r win-x64 --self-contained -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true` (same flags as the WPF app's publish profile, PLAN.md §3.3) — verify the published exe repeats the above checks standalone, and verify (via `dotnet-trace`, a quick `dumpbin`/`ildasm` check, or simply inspecting the publish output folder) that it does NOT contain `RoeSnip.Platform.MacOS.dll`/`RoeSnip.Platform.Linux.dll`/`Tmds.DBus.dll` — confirming §1.7's conditional-reference mechanism actually worked for a real RID-specific publish, not just in the abstract.
5. **Linux compile gate + WSLg smoke** (Ubuntu WSL2 is available on this machine per the briefing):
   - `dotnet publish src/RoeSnip.App -r linux-x64 --self-contained` succeeds.
   - If WSLg is set up for GUI passthrough: copy the publish output in, run it under WSLg's X11 (or
     Wayland, if WSLg's compositor is configured that way — check `echo $XDG_SESSION_TYPE` inside
     WSL first, since the hotkey-hook-start decision in §3.2 depends on it), and smoke-test
     `--diag`/`--capture` at minimum; a full overlay interaction smoke test if the GUI passthrough is
     working well enough to drive it. Document exactly what was/wasn't exercised in the final report
     — "compiled" and "ran `--diag` under WSLg" are very different claims, do not conflate them.
6. **macOS: compile-only.** `dotnet publish src/RoeSnip.App -r osx-arm64 --self-contained` and
   `-r osx-x64` both succeed. The `helpers/scksnap` GitHub Actions workflow succeeds (verify via
   `gh run list`/`gh run view` if the workflow has actually been pushed and triggered — do not just
   eyeball the YAML and assume it's correct). Nothing here is runtime-verified — say so plainly in
   `TESTING.md` (below), not just in this plan.
7. **`TESTING.md`** (new file, integrator writes it, not any single WP): one section per OS stating
   exactly what was verified, how, and on what — "Windows: full interactive parity checklist above,
   run on \<date\> on this machine's real HDR/SDR monitor mix", "Linux: compiles; WSLg smoke of
   \<list exactly what worked\>, NOT tested: \<list\>", "macOS: compiles + Actions workflow green;
   zero runtime verification — needs a real Mac before shipping any macOS-facing claim." This
   mirrors DESIGN-XPLAT.md's own "Verification reality" section but as a living, dated record rather
   than a design-time prediction.

**Known integration risks** (in addition to PLAN.md §4's still-applicable ones — signature drift,
`RowPitch` vs. tight-packing assumptions, `[ModuleInitializer]` ordering):

- **The `CaptureBackendRegistry` selection risk is new and specific to this port**: because a
  no-RID/design-time build loads all three Platform.* assemblies simultaneously (§1.7), a bug where
  TWO candidates both report `IsSupported() == true` on the same machine (e.g. a typo checking
  `IsLinux()` when it meant `IsMacOS()`) would silently pick whichever registered first — which is
  by definition non-deterministic across builds. Test this explicitly: on this Windows machine,
  confirm `CaptureBackendRegistry.CreateForCurrentPlatform()` returns a `WindowsCaptureBackend`
  specifically (not just "some backend"), e.g. via a quick integration check in
  `RoeSnip.Core.Tests` or a debug log line at startup.
- **The MonitorInfo/Avalonia Screen correlation (§3.3)** is a genuinely new failure surface Phase 1
  never had (Phase 1's WPF app used `HMONITOR` directly for both capture AND window placement, so
  there was nothing to correlate). Verify it explicitly on this machine's real multi-monitor setup
  if available; if this machine is single-monitor, note that the correlation logic is
  "exercised trivially" (one monitor, one screen, matches by construction) and flag multi-monitor
  correlation as unverified rather than claiming it works.

---

## 5. Platform facts appendix (verified 2026-07-07 — implementers should not need to re-research any of these)

- **Avalonia 12 stable is 12.0.5** on nuget.org (12.0.0 released 2026-04-07, 12.0.5 on 2026-06-23).
  Minimum TFM is net8.0 (netstandard2.0/net4x support was dropped in 12); net9.0/net10.0 are also
  supported but this plan pins net8.0 to match Core and the frozen WPF app.
- **`Avalonia.Skia 12.0.5` depends on `SkiaSharp >= 3.119.4` exactly** (confirmed directly from the
  package's own nuget.org dependency listing) — this is the version this plan pins everywhere
  SkiaSharp is referenced (§1.2). SkiaSharp's own versioning has since moved to an unrelated `4.x`
  line for other packages; do not follow that line for RoeSnip's SkiaSharp pin.
- **SkiaSharp 3.119+ has a known DirectX 12 dependency on Windows**, flagged "by design" by the
  Avalonia maintainers in a GitHub issue, affecting Windows 7/8/8.1 and some older Windows 10
  installs lacking DX12 (an older-GPU/driver limitation, not strictly an OS-version one). This
  machine (Windows 11 Pro) and any HDR-capable gaming/creator machine RoeSnip's whole premise
  targets should have DX12 by construction — low practical risk for this app's actual audience, but
  worth a `TESTING.md` line if a user ever reports a launch failure tied to graphics capability.
- **`.NET NamedPipeServerStream`/`NamedPipeClientStream` are implemented on Unix via Unix domain
  sockets** (since the .NET 6 FIFO→UDS switch) — the exact same
  `NamedPipeServerStream`/`NamedPipeClientStream` code the WPF app already uses for single-instance
  signalling ports to macOS/Linux with zero API changes. This is the "verify the .NET behavior and
  pin the approach" item the briefing called for — confirmed, use the pipe code unchanged.
- **Named `Mutex` also works cross-platform** (pthread process-shared robust mutexes where the OS
  supports them, file-lock-based elsewhere) with one caveat: it's known broken under **NativeAOT**
  compilation on macOS/Linux (a currently-open dotnet/runtime issue) — irrelevant here since this
  plan does not use `PublishAot`. The Windows-specific `Global\` name prefix (Terminal Services
  session visibility) has no meaning on Unix; §3.2 has the mutex use a plain, unprefixed name there.
- **SharpHook 7.1.1** is the current stable version. It wraps libuiohook and offers
  `SimpleGlobalHook` (handlers run on the hook's own thread — must be fast), `EventLoopGlobalHook`
  (dedicated thread, queues on backpressure), and `TaskPoolGlobalHook` (thread-pool handlers,
  configurable parallelism, queues on backpressure) — any of the latter two is appropriate for
  RoeSnip's single hotkey-combo listener. **Confirmed: only X11 is supported on Linux — Wayland has
  no libuiohook support at all** (matches DESIGN-XPLAT.md's claim exactly), which is why §3.2 gates
  hook startup on `XDG_SESSION_TYPE` and why the DE-keyboard-shortcut-to-`RoeSnip capture` path is
  the documented PRIMARY Linux activation, not a fallback.
- **`xdg-desktop-portal`'s `Screenshot` permission behavior genuinely differs GNOME vs. KDE**:
  GNOME's portal implementation requests permission via its own `access` portal even for
  non-interactive screenshot requests (one-time grant on newer portal versions, per-shot on older
  ones, matching DESIGN-XPLAT.md); KDE's Plasma portal has historically skipped the dialog for
  non-interactive requests entirely, though recent Plasma versions (6.5+) added a settings page for
  finer-grained per-app permission control. Document this difference in `TESTING.md` rather than
  treating either behavior as a bug.
- **Avalonia 12.0.0 fixed X11 clipboard INCR support** (large clipboard transfers, e.g. full-size
  screenshot PNGs, previously did not work correctly over the X11 clipboard protocol's INCR
  extension) — confirmed via the Avalonia 12.0.0 release notes. This directly de-risks WP-X3's
  Linux clipboard path (§3.3) for anything beyond tiny images.
- **Cross-host compile of `net8.0-windows10.0.22621.0` projects without `UseWPF`/`UseWindowsForms`**
  (Platform.Windows, §1.4) is believed to compile on non-Windows hosts because the constraint
  requiring an actual Windows host is specifically the WindowsDesktop SDK workload's compiler tasks
  (XAML compiler etc.), not the bare `net8.0-windowsX.Y.Z` TFM/CsWinRT-projection metadata packages
  themselves — this was NOT empirically re-verified against this exact package combination during
  planning (no non-Windows host was available in this pass); the integrator should do one clean
  `dotnet build` of just `RoeSnip.Platform.Windows.csproj` and confirm before relying on it for any
  future Linux/macOS CI runner, per §6 flag 2.

---

## 6. Plan-time flags

1. **Package-version currency is a moving target.** All versions in §1.2 were verified against
   nuget.org on 2026-07-07 (today). If implementation of any work package happens materially later,
   re-verify `Avalonia`/`Avalonia.Skia`'s SkiaSharp floor specifically before pinning — the whole
   point of pinning Core's SkiaSharp reference to Avalonia's own floor (rather than "whatever's
   newest") is to guarantee one resolved SkiaSharp version across the dependency graph; re-verify
   this pairing, don't assume it stays 3.119.4 forever.
2. **Platform.Windows's cross-host compilability was not empirically re-verified in this planning
   pass** (§5's last bullet) — flagged explicitly rather than silently assumed. If a future CI
   pipeline needs to compile the whole solution on a Linux runner (e.g. for the Linux publish gate),
   confirm this first; if it turns out `net8.0-windows10.0.22621.0` genuinely cannot restore/compile
   on Linux for some reason not anticipated here, the fallback is to give Platform.Windows its own
   solution-level build configuration that's skipped on non-Windows CI runners specifically (not a
   change to the RID-conditional `ProjectReference` logic in §1.7, which is about App's *reference*
   to Platform.Windows, not Platform.Windows's own standalone compilability).
3. **macOS `MonitorInfo.SdrWhiteNits` has no OS-reported absolute value to use.** Windows has the
   ACM slider (`DisplayConfigGetDeviceInfo`'s SDR white level); macOS's EDR model expresses headroom
   as a multiplier above 1.0, not nits. This plan recommends a placeholder default of **240.0**
   (matching the existing Windows query-failure fallback, for consistency rather than any macOS-
   specific research) so the nits-readout math has *something* non-degenerate to multiply by,
   but this is a genuine, unresolved product-design gap, not a verified fact — a future refinement
   could instead show "headroom ×2.1" style readouts on macOS instead of an absolute nits number,
   which may be the more honest UI for that platform's actual HDR model. Flag for product-owner
   input before shipping any macOS build that surfaces this number to a user.
4. **The "`RoeSnip capture`/`RoeSnip settings` verbs become the resident instance if none is
   running" behavior (§3.2) is a plan-level fill-in, not something DESIGN-XPLAT.md specifies
   explicitly.** DESIGN-XPLAT.md says the app is "fully operable via CLI verbs... that signal the
   running instance," which only makes sense as an ongoing activation story (e.g. a DE keyboard
   shortcut used repeatedly) if invoking the verb with no instance running results in a
   still-resident process afterward, not a one-shot capture followed by immediate exit. This plan
   makes that behavior explicit; if a reviewer disagrees with it, the alternative (spawn, capture,
   exit every time) is a much smaller, more contained change to §3.2's `RunTriggerCapture` — flag it
   during implementation if this reading turns out to be wrong rather than silently picking the
   other behavior.
5. **Run-at-startup on macOS/Linux is unspecified in DESIGN-XPLAT.md and left as a documented no-op
   in this plan** (§3.2's `StartupManager`) — a real implementation would need a `LaunchAgents`
   plist on macOS or an XDG autostart `.desktop` file on Linux; neither is in scope for this plan
   pass. Flagged so the Settings window's run-at-startup toggle isn't mistaken for "verified to
   work" on those OSes just because it's visible in the UI.
6. **Linux/macOS `MonitorInfo.SdrWhiteNits` default of 240.0** (§3.4/§3.5) is chosen purely for
   consistency with the existing Windows query-failure fallback (DESIGN.md's own documented
   default), not because 240 nits has any particular meaning on those platforms — same caveat as
   flag 3, called out separately here because it affects Linux too (which has no HDR story at all in
   v1, making the number's only real use the SDR-passthrough nits readout's absolute-scale
   correctness for a "what does 100% white read as" sanity check).
7. **`RoeSnip.App`'s `OutputType` is uniformly `Exe`, not conditionally `WinExe` on Windows**
   (§1.7) — a real, minor UX regression versus the frozen WPF app (a double-clicked Windows publish
   will show a console window it didn't before). This was a deliberate simplicity-over-polish choice
   for this planning pass rather than risking an unverified conditional-`OutputType`/apphost
   interaction; revisit with a Windows-conditional `OutputType` once the app is otherwise working
   end-to-end, if the console-window flash is judged worth fixing.
8. **The exact on-disk/bundle layout for shipping `scksnap` alongside the published macOS app**
   (§3.5 — `.app` bundle `Contents/MacOS/` vs. a flat sibling file) was not resolved in this
   planning pass because it depends on exactly how Avalonia's own `osx-arm64`/`osx-x64` publish
   output is shaped, which cannot be inspected from this Windows machine. WP-X5 must decide this
   during implementation based on what `dotnet publish -r osx-arm64` actually produces, and should
   document the decision in its final report rather than guessing silently.
9. **`Tmds.DBus` vs. `Tmds.DBus.Protocol`** (§1.2): this plan picks the higher-level, proxy-interface
   library for ergonomics on a single portal call. If WP-X4 finds the reflection/codegen-based proxy
   pattern awkward for the `Request.Response` signal-waiting step specifically (portal calls are a
   two-step "call a method, then wait for a signal on the returned object path" dance that not every
   D-Bus client library models equally cleanly), switching to `Tmds.DBus.Protocol`'s lower-level API
   for just that piece is a reasonable in-flight adjustment — note it in the final report rather than
   silently fighting the higher-level library for hours.
