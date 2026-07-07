# RoeSnip — Implementation Plan (PLAN.md)

*Authoritative execution plan for parallel implementer agents. Read DESIGN.md first — this document
does not repeat rationale, only the exact contracts, file ownership, and instructions needed to build
it. Where this plan and DESIGN.md conflict, DESIGN.md wins for **behavior**; this plan wins for
**file layout / exact signatures**. Genuine gaps are called out in "Plan-time flags" at the bottom —
implementers must not silently invent behavior beyond what's written here or in DESIGN.md.*

---

## 0. Ground rules for implementer agents

1. **File ownership is absolute.** Each work package (WP-A / WP-B / WP-C) may only create/edit the
   files listed under it in §3. If you believe you need to touch a file owned by another package,
   stop and use the seam that's already defined for you (§2 contracts, §3 "AppComposition hooks").
   Never edit `src/RoeSnip/Program.cs` unless you are WP-A.
2. **Code against the contracts in §2 verbatim.** Do not rename types/members, change signatures, or
   "improve" them unilaterally. If a contract is genuinely insufficient for your package, note it in
   your final report rather than silently diverging — the integrator reconciles it once.
3. **The whole app is one project** (`src/RoeSnip/RoeSnip.csproj`), not one assembly per package. This
   means the solution will not fully `dotnet build` until all three packages' files exist — that is
   expected. Each package's own acceptance test (§3) is designed to be checkable with the hook pattern
   in §2.4 so WP-A in particular can be built and smoke-tested before WP-B/WP-C land.
4. Target platform is Windows only. All coordinates that cross the `Capture`/`Overlay`/`Imaging`
   boundary are **physical pixels**, never WPF DIPs, never DPI-scaled — see DESIGN.md's mixed-DPI
   pattern. Any DIP conversion happens only at the WPF view layer inside `Overlay/OverlayWindow.xaml.cs`
   and must not leak into any contract type.
5. `System.Half` (from `System.Runtime.Intrinsics` era BCL, in `System` namespace since .NET 5) is the
   FP16 storage type; convert to `float`/`Vector4` for math, convert back only when writing bytes.

---

## 1. Project setup spec

### 1.1 Directory / solution layout

```
roesnip/
  RoeSnip.sln
  DESIGN.md
  PLAN.md
  README.md                       # WP-C
  src/RoeSnip/
    RoeSnip.csproj
    app.manifest
    Program.cs                    # WP-A (composition root; see §2.4)
    Interop/
      NativeMethods.cs            # WP-A (all P/Invoke; §5). Others consume, never edit.
    Capture/
      MonitorInfo.cs               # WP-A
      IScreenCapturer.cs            # WP-A
      DesktopDuplicationCapturer.cs  # WP-A
      WgcCapturer.cs                # WP-A
      CaptureService.cs             # WP-A
      CapturedFrame.cs              # WP-A
    Color/
      ColorMath.cs                 # WP-A
      ToneMapper.cs                 # WP-A
      Dither.cs                     # WP-A
    Imaging/
      SdrImage.cs                  # WP-A
      PngWriter.cs                  # WP-A
      JxrWriter.cs                  # WP-C
      ClipboardService.cs           # WP-B
    App/
      TrayApp.cs                   # WP-C
      HotkeyManager.cs              # WP-C
      Settings.cs                   # WP-C
      SettingsWindow.xaml(.cs)      # WP-C
      StartupManager.cs             # WP-C
    Overlay/
      OverlayController.cs         # WP-B
      OverlayWindow.xaml(.cs)       # WP-B
      SelectionAdorner.cs           # WP-B
      Magnifier.cs                  # WP-B
      AnnotationLayer.cs            # WP-B
      ToolbarControl.xaml(.cs)      # WP-B
  tests/RoeSnip.Tests/
    RoeSnip.Tests.csproj
    ColorMathTests.cs              # WP-A
    ToneMapperTests.cs              # WP-A
    JxrRoundTripTests.cs            # WP-C
    SettingsTests.cs                # WP-C
```

Create the solution with the CLI (do not hand-author the `.sln` file):

```
dotnet new sln -n RoeSnip
dotnet new wpf -o src/RoeSnip -n RoeSnip --framework net8.0
dotnet new xunit -o tests/RoeSnip.Tests -n RoeSnip.Tests --framework net8.0
dotnet sln add src/RoeSnip/RoeSnip.csproj tests/RoeSnip.Tests/RoeSnip.Tests.csproj
dotnet add tests/RoeSnip.Tests/RoeSnip.Tests.csproj reference src/RoeSnip/RoeSnip.csproj
```

Then hand-edit both `.csproj` files to exactly the content below (the templates generate a
reasonable skeleton but wrong TFM/packages).

### 1.2 `src/RoeSnip/RoeSnip.csproj`

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <OutputType>WinExe</OutputType>
    <TargetFramework>net8.0-windows10.0.22621.0</TargetFramework>
    <RootNamespace>RoeSnip</RootNamespace>
    <AssemblyName>RoeSnip</AssemblyName>
    <UseWPF>true</UseWPF>
    <UseWindowsForms>true</UseWindowsForms>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <AllowUnsafeBlocks>true</AllowUnsafeBlocks>
    <ApplicationManifest>app.manifest</ApplicationManifest>
    <InvariantGlobalization>true</InvariantGlobalization>
    <Platforms>x64</Platforms>
    <RuntimeIdentifiers>win-x64</RuntimeIdentifiers>
  </PropertyGroup>

  <ItemGroup>
    <!-- Pinned 2026-07: verified on nuget.org. WIC bindings live in Vortice.Direct2D1, NOT a
         standalone "Vortice.WIC" package — see Plan-time flags. -->
    <PackageReference Include="Vortice.Direct3D11" Version="3.8.3" />
    <PackageReference Include="Vortice.DXGI" Version="3.8.3" />
    <PackageReference Include="Vortice.Direct2D1" Version="3.8.3" />
  </ItemGroup>

</Project>
```

`SharpGen.Runtime` is a transitive dependency of the Vortice packages — do not add it explicitly.

### 1.3 `src/RoeSnip/app.manifest`

```xml
<?xml version="1.0" encoding="utf-8"?>
<assembly xmlns="urn:schemas-microsoft-com:asm.v1" manifestVersion="1.0">
  <assemblyIdentity version="1.0.0.0" name="RoeSnip.app"/>
  <trustInfo xmlns="urn:schemas-microsoft-com:asm.v2">
    <security>
      <requestedPrivileges xmlns="urn:schemas-microsoft-com:asm.v3">
        <requestedExecutionLevel level="asInvoker" uiAccess="false" />
      </requestedPrivileges>
    </security>
  </trustInfo>
  <compatibility xmlns="urn:schemas-microsoft-com:compatibility.v1">
    <application>
      <!-- Vista, 7, 8, 8.1, 10/11 -->
      <supportedOS Id="{e2011457-1546-43c5-a5fe-008deee3d3f0}"/>
      <supportedOS Id="{35138b9a-5d96-4fbd-8e2d-a2440225f93a}"/>
      <supportedOS Id="{4a2f28e3-53b9-4441-ba9c-d69d4a4a6e38}"/>
      <supportedOS Id="{1f676c76-80e1-4239-95bb-83d0f6d0da78}"/>
      <supportedOS Id="{8e0f7a12-bfb3-4fe8-b9a5-48fd50a15a9a}"/>
    </application>
  </compatibility>
  <application xmlns="urn:schemas-microsoft-com:asm.v3">
    <windowsSettings>
      <dpiAwareness xmlns="http://schemas.microsoft.com/SMI/2016/WindowsSettings">PerMonitorV2</dpiAwareness>
      <dpiAwarenessPerProfile xmlns="http://schemas.microsoft.com/SMI/2016/WindowsSettings">true</dpiAwarenessPerProfile>
    </windowsSettings>
  </application>
</assembly>
```

### 1.4 `tests/RoeSnip.Tests/RoeSnip.Tests.csproj`

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <TargetFramework>net8.0-windows10.0.22621.0</TargetFramework>
    <RootNamespace>RoeSnip.Tests</RootNamespace>
    <UseWPF>true</UseWPF>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <IsPackable>false</IsPackable>
    <IsTestProject>true</IsTestProject>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="Microsoft.NET.Test.Sdk" Version="18.7.0" />
    <PackageReference Include="xunit" Version="2.9.3" />
    <PackageReference Include="xunit.runner.visualstudio" Version="3.1.5">
      <IncludeAssets>runtime; build; native; contentfiles; analyzers; buildtransitive</IncludeAssets>
      <PrivateAssets>all</PrivateAssets>
    </PackageReference>
  </ItemGroup>

  <ItemGroup>
    <ProjectReference Include="..\..\src\RoeSnip\RoeSnip.csproj" />
  </ItemGroup>

</Project>
```

(`xunit` 2.9.3 is the last v2 release; the v3 rewrite is unnecessary churn for this project — pin v2.)

---

## 2. Shared contracts

These types are the seams between work packages. **Copy them verbatim** into the indicated files.
Ownership of the *file* stays with the package noted in §3, but every package may *read/reference*
any type below regardless of which file it lives in.

### 2.1 `Capture/MonitorInfo.cs` (owned by WP-A)

```csharp
using System;

namespace RoeSnip.Capture;

/// <summary>Physical-pixel rectangle. Used everywhere a rect crosses a module boundary
/// (monitor bounds, selection, crop). Never DIPs.</summary>
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

/// <summary>One physical display, enumerated once per capture trigger.</summary>
public sealed record MonitorInfo(
    int Index,                 // stable ordinal for this capture session, 0-based
    string DeviceName,          // GDI device name, e.g. "\\.\DISPLAY1" — matches DXGI_OUTPUT_DESC.DeviceName
    nint HMonitor,               // HMONITOR
    RectPhysical BoundsPx,        // this monitor's rect in virtual-desktop physical pixels
    int DpiX,
    int DpiY,
    bool AdvancedColorActive,     // informational only (DXGI_COLOR_SPACE_RGB_FULL_G2084_NONE_P2020) —
                                   // NEVER branch capture/tone-map logic on this; branch on delivered format instead
    double SdrWhiteNits,          // DisplayConfigGetDeviceInfo SDR_WHITE_LEVEL; default 240.0 on query failure
    double MaxLuminanceNits,      // IDXGIOutput6.GetDesc1().MaxLuminance; default 1000.0 if 0 or absurd (<10 or >10000)
    bool IsPrimary
);
```

### 2.2 `Capture/CapturedFrame.cs` (owned by WP-A)

```csharp
using System;
using System.Numerics;
using RoeSnip.Color;

namespace RoeSnip.Capture;

public enum FrameFormat
{
    Fp16ScRgb,   // R16G16B16A16_FLOAT, linear scRGB, 1.0 = 80 nits. 8 bytes/pixel (4 x System.Half).
    Bgra8Srgb,   // B8G8R8A8_UNORM, already sRGB-encoded passthrough. 4 bytes/pixel.
}

/// <summary>Owns one monitor's raw captured pixels for the lifetime of a capture session.
/// The buffer is exactly as delivered by the capturer: <see cref="Stride"/> is the real row
/// pitch (may exceed Width * BytesPerPixel due to driver padding) — always index as
/// <c>row * Stride + col * BytesPerPixel</c>, never assume tightly packed rows.
/// Lifetime: created by CaptureService.CaptureAll(); the caller (AppComposition) owns and
/// must Dispose() every frame once the capture session (overlay + any exports) is complete.</summary>
public sealed class CapturedFrame : IDisposable
{
    public FrameFormat Format { get; }
    public int Width { get; }
    public int Height { get; }
    public int Stride { get; }
    public MonitorInfo Monitor { get; }
    public int BytesPerPixel => Format == FrameFormat.Fp16ScRgb ? 8 : 4;

    private byte[]? _pixels; // null after Dispose()

    public CapturedFrame(FrameFormat format, int width, int height, int stride, byte[] pixels, MonitorInfo monitor)
    {
        Format = format;
        Width = width;
        Height = height;
        Stride = stride;
        Monitor = monitor;
        _pixels = pixels;
    }

    public ReadOnlySpan<byte> Row(int y)
    {
        var pixels = _pixels ?? throw new ObjectDisposedException(nameof(CapturedFrame));
        return pixels.AsSpan(y * Stride, Width * BytesPerPixel);
    }

    /// <summary>Reads pixel (x,y) as linear scRGB (1.0 = 80 nits), regardless of source format.
    /// For Bgra8Srgb frames this decodes the sRGB EOTF and rescales so that byte 255 (linear 1.0)
    /// corresponds to Monitor.SdrWhiteNits, i.e. scRGB = srgbLinear01 * (Monitor.SdrWhiteNits / 80.0).
    /// Used by the magnifier's nits readout — the "killer HDR feature" (DESIGN.md).</summary>
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

    /// <summary>Photometric luminance in nits for the magnifier readout: max(r,g,b) * 80.</summary>
    public double ReadPixelNits(int x, int y)
    {
        var v = ReadPixelScRgb(x, y);
        return Math.Max(v.X, Math.Max(v.Y, v.Z)) * 80.0;
    }

    public void Dispose() => _pixels = null;
}
```

`ColorMath.SrgbByteToLinear(byte)` is defined by WP-A in `Color/ColorMath.cs` (§3.1) — `CapturedFrame`
references it, both are WP-A-owned, no cross-package coupling.

### 2.3 `Capture/IScreenCapturer.cs`, `CaptureService.cs` (owned by WP-A)

```csharp
namespace RoeSnip.Capture;

public sealed class CaptureException : Exception
{
    public CaptureException(string message, Exception? inner = null) : base(message, inner) { }
}

public interface IScreenCapturer
{
    /// <summary>Captures a single frame of the given monitor. Throws CaptureException on any
    /// unrecoverable failure (including after this capturer's own internal retries) — callers
    /// decide fallback policy, this method never falls back to a different capturer itself.</summary>
    CapturedFrame Capture(MonitorInfo monitor);
}
```

```csharp
namespace RoeSnip.Capture;

public static class MonitorEnumerator
{
    /// <summary>Enumerates all active monitors. Never throws for a single bad monitor entry —
    /// logs to stderr and omits it. Returns empty list only if enumeration itself fails entirely.</summary>
    public static IReadOnlyList<MonitorInfo> Enumerate();
}

public sealed class CaptureService
{
    public CaptureService();

    /// <summary>Captures every monitor in <paramref name="monitors"/> (or all enumerated monitors
    /// if null). Per monitor: try DesktopDuplicationCapturer; on CaptureException, try WgcCapturer;
    /// on CaptureException from both, log to stderr and OMIT that monitor from the result (does not
    /// throw). If <paramref name="onlyMonitorIndex"/> is set, only that monitor is attempted.
    /// Returns frames in the same order as the input monitor list. Empty result means every
    /// monitor failed — callers (CLI / AppComposition) treat that as a hard failure.</summary>
    public IReadOnlyList<CapturedFrame> CaptureAll(
        IReadOnlyList<MonitorInfo>? monitors = null,
        int? onlyMonitorIndex = null);
}
```

### 2.4 Composition root & cross-package hooks — `Program.cs` (owned by WP-A)

This is the one seam every package plugs into. **WP-B and WP-C never edit this file.** They register
themselves into the nullable hook properties below from their *own* files using a
`[ModuleInitializer]` method (runs automatically before `Main`, no call-site needed in `Program.cs`).
This lets WP-A's own CLI paths (`--diag`, `--capture` without `--jxr`) build and run correctly even
when zero WP-B/WP-C files exist yet, because `AppComposition` only references contract types declared
in this same file, never WP-B/WP-C concrete classes.

```csharp
namespace RoeSnip;

/// <summary>Data-only result of one overlay session. The Overlay package (WP-B) produces this;
/// it performs Copy (clipboard) and Save (PNG dialog + file write) itself before returning, using
/// only WP-A leaf APIs (PngWriter) and its own ClipboardService — so those two actions need no
/// hook. Only the cross-cutting bits (HDR export, "saved" tray balloon) are threaded back through
/// AppComposition, because they need WP-C types (JxrWriter, ITrayNotifier) that Overlay must not
/// reference directly.</summary>
public sealed record OverlayResult(
    Capture.MonitorInfo Monitor,
    Capture.RectPhysical SelectionPx,      // selection rect, relative to Monitor.BoundsPx origin
    Imaging.SdrImage RenderedImage,         // tone-mapped crop with annotations burned in
    Capture.CapturedFrame SourceFrame,       // original (uncropped) frame for this monitor — for HDR export
    bool CopyPerformed,                      // true if Overlay already wrote PNG+CF_DIBV5 to the clipboard
    string? SavedPngPath,                    // non-null if the user used Save and it succeeded
    bool SaveHdrRequested                    // true if the user clicked "Save HDR" (independent of settings.AutoSaveHdrCopy)
);

/// <summary>Settings data shape (DESIGN.md §6). Persistence (JSON load/save, fail-closed-on-unreadable)
/// is WP-C's job in App/Settings.cs; this record is the pure shape so WP-A's composition root and
/// WP-B's overlay can reference it without depending on WP-C's persistence file.</summary>
public sealed record RoeSnipSettings
{
    public int SchemaVersion { get; init; } = 1;
    public uint HotkeyModifiers { get; init; } = 0;              // MOD_* flags (0 = PrintScreen alone)
    public uint HotkeyVirtualKey { get; init; } = 0x2C;          // VK_SNAPSHOT
    public string SaveDirectory { get; init; } = DefaultSaveDirectory();
    public bool AutoSaveHdrCopy { get; init; } = false;
    public double? ToneMapKneeOverride { get; init; } = null;     // null => ToneMapper default (0.90)
    public double? ToneMapPeakOverride { get; init; } = null;     // null => derive from monitor
    public bool RunAtStartup { get; init; } = false;
    public bool CopyOnSelect { get; init; } = false;              // confirming a selection also performs Copy

    public static RoeSnipSettings Default { get; } = new();

    private static string DefaultSaveDirectory() =>
        System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "RoeSnip");
}

/// <summary>Implemented by App/TrayApp.cs (WP-C). Passed into AppComposition.RunCaptureFlowAsync
/// so the interactive flow can surface balloons without Program.cs referencing TrayApp directly.</summary>
public interface ITrayNotifier
{
    void ShowSavedBalloon(string filePath);
    void ShowError(string message);
}

public enum CliMode { None, Diag, Capture }

public sealed record CliOptions(CliMode Mode, int? Monitor, string? Out, bool Jxr)
{
    public static CliOptions Parse(string[] args);
    // Grammar: --diag | --capture [--monitor N] [--out path] [--jxr]
    // Unknown/malformed args => Mode=None and Program.Main prints usage to stderr, exit 1.
}

/// <summary>Composition root. WP-A owns this class and file. WP-B/WP-C register their
/// capability into the hooks via [ModuleInitializer] in their own files; they never call
/// anything here except by having Program.cs's own logic invoke the hooks.</summary>
public static class AppComposition
{
    // Set by App/Settings.cs (WP-C). Null => RoeSnipSettings.Default is used everywhere.
    public static Func<RoeSnipSettings>? LoadSettings { get; set; }

    // Set by Overlay/OverlayController.cs (WP-B).
    public static Func<
        IReadOnlyList<(Capture.CapturedFrame Frame, Imaging.SdrImage Preview)>,
        RoeSnipSettings,
        Task<OverlayResult?>>? RunOverlay { get; set; }

    // Set by Imaging/JxrWriter.cs (WP-C). Writes frame (cropped to cropPx) as .jxr to path.
    public static Action<string, Capture.CapturedFrame, Capture.RectPhysical>? WriteJxr { get; set; }

    // Set by App/TrayApp.cs (WP-C). Runs the tray message loop; returns process exit code on quit.
    public static Func<string[], int>? RunTrayApp { get; set; }

    /// <summary>WP-A only. Implements --diag.</summary>
    public static int RunDiagCli();

    /// <summary>WP-A only for the PNG path. If cli.Jxr and WriteJxr is null, prints a warning to
    /// stderr but still writes the PNG and returns 0 (graceful degrade, so this method is fully
    /// testable before WP-C exists) — a bare `--capture --jxr` with WriteJxr unavailable is NOT
    /// treated as a hard failure.</summary>
    public static int RunCaptureCli(CliOptions cli);

    /// <summary>Entry point for launching the tray app (no CLI args). If RunTrayApp is null
    /// (App/* not built yet), prints an error and returns 1.</summary>
    public static int RunTray(string[] args);

    /// <summary>The interactive capture flow: capture all monitors, run the overlay, then handle
    /// the cross-cutting follow-ups (HDR auto-save / Save-HDR button, "saved" balloon). Called by
    /// WP-C's HotkeyManager (on hotkey) and TrayApp's "Capture" menu item, passing itself as
    /// notifier. If RunOverlay is null, calls notifier.ShowError and returns.</summary>
    public static async Task RunCaptureFlowAsync(RoeSnipSettings settings, ITrayNotifier? notifier);
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
            _ => AppComposition.RunTray(args), // includes single-instance signalling, see §3.3
        };
    }
}
```

### 2.5 `Color/ToneMapper.cs` (owned by WP-A)

```csharp
namespace RoeSnip.Color;

public readonly record struct ToneMapOptions(
    double Knee = 0.90,
    double? PeakOverride = null,   // null => derive: clamp(min(M, MaxLuminanceNits/SdrWhiteNits), 2.0, double.MaxValue)
    double Epsilon = 1e-3           // M <= 1.0 + Epsilon => exact SDR pass-through, no shoulder, no dither
);

/// <summary>The tone-map pipeline. See DESIGN.md "Tone-map pipeline" for the full spec; the exact
/// Hermite shoulder formula is pinned in PLAN.md §3.1 so all implementers (and tests) agree on it.
/// Only accepts Fp16ScRgb frames — Bgra8Srgb frames must go through SdrImage.FromCapturedFrame's
/// passthrough branch instead (do not call MapToSdr on a Bgra8Srgb frame; it throws).</summary>
public static class ToneMapper
{
    public static Imaging.SdrImage MapToSdr(Capture.CapturedFrame frame, ToneMapOptions opts);
}
```

### 2.6 `Imaging/SdrImage.cs` (owned by WP-A)

```csharp
namespace RoeSnip.Imaging;

/// <summary>BGRA8, straight alpha (255 = opaque), tightly packed rows (Stride == Width * 4),
/// top-down. This is the one and only "what you see is what you get" image type — the overlay
/// preview, the clipboard payload, and the saved PNG are all built from this type.</summary>
public sealed class SdrImage
{
    public int Width { get; }
    public int Height { get; }
    public int Stride => Width * 4;
    public byte[] Pixels { get; } // BGRA8, length == Stride * Height

    public SdrImage(int width, int height, byte[] pixels);

    /// <summary>The single entry point every call site (CLI, overlay preview, exports) must use
    /// to turn a CapturedFrame into an SdrImage — this is what encodes the format branch from
    /// DESIGN.md ("BGRA8-source frames bypass the tone-mapper entirely"), so no call site needs
    /// to know or duplicate that branch.</summary>
    public static SdrImage FromCapturedFrame(Capture.CapturedFrame frame, Color.ToneMapOptions opts) =>
        frame.Format == Capture.FrameFormat.Fp16ScRgb
            ? Color.ToneMapper.MapToSdr(frame, opts)
            : FromBgra8Passthrough(frame);

    private static SdrImage FromBgra8Passthrough(Capture.CapturedFrame frame);

    /// <summary>Crops in physical pixels relative to this image's own (0,0); throws if out of bounds.</summary>
    public SdrImage Crop(Capture.RectPhysical rectPx);

    /// <summary>WPF BitmapSource, Bgra32, 96 DPI both axes (so 1 device pixel == 1 DIP; required
    /// for the mixed-DPI overlay pattern in DESIGN.md).</summary>
    public System.Windows.Media.Imaging.BitmapSource ToBitmapSource();
}
```

### 2.7 `Overlay/OverlayController.cs` (owned by WP-B)

```csharp
namespace RoeSnip.Overlay;

public static class OverlayController
{
    /// <summary>One OverlayWindow per monitor; runs until the user cancels (Esc) or confirms
    /// (Enter / double-click / toolbar action). Returns null on cancel. On confirm, performs
    /// Copy/Save side effects itself (clipboard + PNG dialog) per DESIGN.md, then returns a
    /// populated OverlayResult. Matches the AppComposition.RunOverlay hook signature exactly —
    /// register this method via [ModuleInitializer] (see file-bottom snippet below).</summary>
    public static Task<OverlayResult?> RunAsync(
        IReadOnlyList<(Capture.CapturedFrame Frame, Imaging.SdrImage Preview)> monitors,
        RoeSnipSettings settings);
}
```

At the bottom of `OverlayController.cs`:

```csharp
file static class ModuleInit
{
    [System.Runtime.CompilerServices.ModuleInitializer]
    internal static void Init() => AppComposition.RunOverlay = OverlayController.RunAsync;
}
```

---

## 3. Work packages

### 3.1 WP-A — M1 core (capture + tone-map + CLI)

**Files owned:** `RoeSnip.csproj`, `app.manifest`, `RoeSnip.sln` (create once, all packages add
references to it — see §4 for who touches the `.sln`), `Program.cs`, `Interop/NativeMethods.cs`,
all of `Capture/*`, all of `Color/*`, `Imaging/SdrImage.cs`, `Imaging/PngWriter.cs`,
`tests/RoeSnip.Tests/RoeSnip.Tests.csproj`, `tests/RoeSnip.Tests/ColorMathTests.cs`,
`tests/RoeSnip.Tests/ToneMapperTests.cs`.

**Responsibilities**

- `Interop/NativeMethods.cs` — every P/Invoke signature in §5, in one file. WP-B/WP-C `using
  RoeSnip.Interop;` and call `NativeMethods.X(...)`; they never add their own DllImports for
  anything already listed in §5. If a package needs a Win32 call not in §5, it may add its own
  P/Invoke declaration **inside its own file** (do not add to NativeMethods.cs — that would be an
  edit to a WP-A file).

- `Capture/MonitorInfo.cs` — the `RectPhysical`/`MonitorInfo` records (§2.1) plus
  `MonitorEnumerator.Enumerate()`. Algorithm:
  1. `EnumDisplayMonitors` → for each HMONITOR, `GetMonitorInfo` (MONITORINFOEX) for
     `rcMonitor`/`szDevice`/primary flag; `GetDpiForMonitor(MDT_EFFECTIVE_DPI)` for DPI.
  2. `IDXGIFactory1` → `EnumAdapters` → `EnumOutputs` → `QueryInterface<IDXGIOutput6>` →
     `GetDesc1()` for `DeviceName`, `DesktopCoordinates` (bounds — should match step 1's
     `rcMonitor`), `ColorSpace` (→ `AdvancedColorActive`), `MaxLuminance` (→ `MaxLuminanceNits`,
     clamp to 1000.0 if `<= 0` or `> 10000`). **Create the D3D11 device on the adapter that owns
     the output** — never assume the default adapter (hybrid-GPU laptops).
  3. `GetDisplayConfigBufferSizes(QDC_ONLY_ACTIVE_PATHS)` then `QueryDisplayConfig` → for each
     `DISPLAYCONFIG_PATH_INFO`, call `DisplayConfigGetDeviceInfo` with a
     `DISPLAYCONFIG_SOURCE_DEVICE_NAME` request (type `GET_SOURCE_NAME`) to get the GDI device
     name for that path's source; match it to the DXGI `DeviceName` from step 2. On match, call
     `DisplayConfigGetDeviceInfo` again with `DISPLAYCONFIG_SDR_WHITE_LEVEL` (type 11) on the same
     adapterId/sourceId; `nits = SDRWhiteLevel / 1000.0 * 80.0`. On any failure at this step
     (mismatched match, non-zero return code, exception): log to stderr, default `SdrWhiteNits =
     240.0`, continue — never throw out of `Enumerate()`.
  4. Merge steps 1–3 by `DeviceName` into `MonitorInfo` records, `Index` assigned in enumeration
     order. If a monitor from step 1 has no DXGI match (rare/virtual displays), still emit it with
     `AdvancedColorActive = false`, `MaxLuminanceNits = 1000.0`, `SdrWhiteNits = 240.0`.

- `Capture/CapturedFrame.cs` — record from §2.2 verbatim.

- `Capture/IScreenCapturer.cs` — `CaptureException`, `IScreenCapturer` from §2.3.

- `Capture/DesktopDuplicationCapturer.cs` — primary path per DESIGN.md "Capture path" step 2:
  - Build (or reuse a cached) `ID3D11Device` for the monitor's owning adapter.
  - `IDXGIOutput5.DuplicateOutput1(device, flags: 0, supportedFormats: [R16G16B16A16_FLOAT,
    B8G8R8A8_UNORM])` → `IDXGIOutputDuplication`. (Vortice's exact overload may differ slightly
    from the raw COM signature — check `Vortice.DXGI` source/samples if the call doesn't compile
    as written; the COM contract is confirmed via Microsoft docs.)
  - `AcquireNextFrame`: retry loop, up to 5 attempts, 100 ms apart, only retrying on
    `DXGI_ERROR_WAIT_TIMEOUT`. Any other HRESULT is a hard failure for this attempt.
  - Read `DXGI_OUTDUPL_DESC.ModeDesc.Format` from the duplication object to decide
    `FrameFormat.Fp16ScRgb` vs `FrameFormat.Bgra8Srgb` — **this format, not
    `AdvancedColorActive`, is what the pipeline branches on.**
  - Copy the acquired frame's texture into a staging texture (`D3D11_USAGE_STAGING`,
    `CPU_ACCESS_READ`) **before** calling `ReleaseFrame`. `Map` the staging texture, copy
    `Height` rows of `RowPitch` bytes each into a single `byte[Height * RowPitch]` buffer, then
    `Unmap`. Construct `CapturedFrame` with `Stride = RowPitch` (do not repack to a tighter
    stride — keep it simple and match what DESIGN.md says about respecting RowPitch).
  - On `DXGI_ERROR_ACCESS_LOST`: re-create the device/output/duplication once and retry the whole
    capture once. If that also fails, throw `CaptureException` (CaptureService then tries WGC).
  - Any other unhandled HRESULT → wrap and throw `CaptureException`.

- `Capture/WgcCapturer.cs` — fallback path per DESIGN.md step 3:
  - `IGraphicsCaptureItemInterop.CreateForMonitor(hmonitor, iid_IGraphicsCaptureItem)` (§5).
  - `Direct3D11CaptureFramePool.CreateFreeThreaded` (NOT plain `Create` — that needs a
    `DispatcherQueue` and throws from CLI/console contexts), pixel format matching the monitor
    (`Fp16ScRgb` if advanced color is active, else `Bgra8Srgb` — WGC doesn't expose a "delivered
    format" the way DD does, so this is the one path where `AdvancedColorActive` legitimately
    picks the request format).
  - `session.IsCursorCaptureEnabled = false`. Attempt `session.IsBorderRequired = false` guarded
    by `try/catch` (property may not exist on older Windows builds; also unpackaged apps lack the
    capability so the border may show anyway — that is accepted per DESIGN.md, not a bug).
  - Single-frame wait via the pool's `FrameArrived` event + a `ManualResetEventSlim` (or
    equivalent), not polling.
  - On any failure, throw `CaptureException`.

- `Capture/CaptureService.cs` — orchestration per §2.3: DD-then-WGC-then-omit, per monitor,
  independently (one monitor's total failure does not affect others).

- `Color/ColorMath.cs` — pure functions, no DirectX dependency (unit-testable):
  ```csharp
  public static class ColorMath
  {
      public static float SrgbEncode(float linear01);       // proper piecewise sRGB EOTF^-1
      public static float SrgbDecode(float encoded01);        // piecewise sRGB EOTF
      public static byte SrgbByteToLinear01AsByte(byte encoded) => ...; // convenience if needed
      public static float SrgbByteToLinear(byte encoded) => SrgbDecode(encoded / 255f);
      public static byte QuantizeRoundNearest(float linear01_after_encode); // clamp01 then round(*255)
  }
  ```
  `SrgbEncode`: `linear <= 0.0031308f ? linear*12.92f : 1.055f*MathF.Pow(linear,1f/2.4f)-0.055f`.
  `SrgbDecode`: `encoded <= 0.04045f ? encoded/12.92f : MathF.Pow((encoded+0.055f)/1.055f,2.4f)`.

- `Color/ToneMapper.cs` — `MapToSdr` per DESIGN.md, with the shoulder curve pinned exactly:
  1. `scale = 80.0 / frame.Monitor.SdrWhiteNits`.
  2. First pass over all pixels: convert FP16→float, multiply by `scale`, clamp negatives to 0,
     track `M = max over all pixels and channels`.
  3. `knee = opts.PeakOverride is null ? opts.Knee : opts.Knee` (Knee is never overridden by
     Peak — they're independent fields; `knee = settings.ToneMapKneeOverride ?? opts.Knee` is
     resolved by the **caller** building `ToneMapOptions`, not inside ToneMapper).
     `peak = opts.PeakOverride ?? Math.Clamp(Math.Min(M, frame.Monitor.MaxLuminanceNits /
     frame.Monitor.SdrWhiteNits), 2.0, double.MaxValue)`.
  4. If `M <= 1.0 + opts.Epsilon`: second pass, every pixel: `encoded = SrgbEncode(clamp01(c))`,
     byte = round-to-nearest, **no dither**. (Pass-through path.)
  5. Else (shoulder mode), second pass, per pixel, per channel triple `(r,g,b)`:
     - `m = max(r,g,b)`.
     - If `m <= knee`: pixel is pass-through (no dither) even though the frame overall is in
       shoulder mode.
     - If `m > knee`: `m' = min(m, peak)`; `t = (m' - knee) / (peak - knee)`;
       `h00 = 2t³-3t²+1`, `h10 = t³-2t²+t`, `h01 = -2t³+3t²`;
       `f = h00*knee + h10*(peak-knee) + h01*1.0` (this is the Hermite basis with tangents
       `f'(knee)=1`, `f'(peak)=0`, giving `f(knee)=knee` and `f(peak)=1.0` — C1-continuous with
       the pass-through region below the knee; do not use a different basis, this exact one is
       what the golden tests in `ToneMapperTests.cs` assert against).
       Scale all three channels by `f/m` (hue-preserving), clamp to [0,1], `SrgbEncode`, then
       **dither this pixel only**: add an ordered-dither offset in `Dither.cs` of ±0.5 LSB
       (e.g. a 4x4 Bayer matrix indexed by `(x % 4, y % 4)`, scaled to ±0.5/255 in normalized
       units) before rounding.
  6. Alpha channel is passed through as opaque (255) — captured alpha from desktop duplication is
     not meaningful; `SdrImage` is always fully opaque.
  7. Use `Parallel.For` over rows for both passes; keep the per-pixel math in one method (no
     helper-call overhead) for the 4K/~8.3MP/<150ms budget.

- `Color/Dither.cs`:
  ```csharp
  public static class Dither
  {
      // 4x4 Bayer matrix, values pre-scaled to a ±0.5/255 (in [0,1]-normalized-linear-encoded-byte space) offset.
      public static float Offset01(int x, int y); // returns a value in [-0.5/255, +0.5/255]
  }
  ```

- `Imaging/SdrImage.cs` — per §2.6.

- `Imaging/PngWriter.cs`:
  ```csharp
  public static class PngWriter
  {
      public static void WriteFile(string path, SdrImage image);   // PngBitmapEncoder, Bgra32 frame
      public static byte[] Encode(SdrImage image);                  // same, to a MemoryStream — used by ClipboardService (WP-B) and tests
  }
  ```

- `Program.cs` — per §2.4. `RunDiagCli`: `MonitorEnumerator.Enumerate()`, print one line per
  monitor (device name, resolution, advanced-color on/off, SDR white nits, max nits), return 0
  (or 1 if enumeration returned empty). `RunCaptureCli`: `CaptureService().CaptureAll(monitors:
  null, onlyMonitorIndex: cli.Monitor)`; for each returned frame build `SdrImage.FromCapturedFrame`
  with default `ToneMapOptions`; print per-monitor diagnostics (device name, resolution, advanced
  color on/off, capture format, SDR white nits, min/max/avg captured pixel value in nits — compute
  the last three during the tone-map first pass or a cheap extra scan); `PngWriter.WriteFile` to
  `cli.Out` (default: current directory, `roesnip_capture_monitorN.png`); if `cli.Jxr`, call
  `AppComposition.WriteJxr?.Invoke(...)` else print a stderr note ("HDR export unavailable — App
  package not present in this build") but still return 0 if the PNG wrote successfully. Return 1
  if `CaptureAll` returned an empty list.

**Unit tests (WP-A owns; must pass with `dotnet test`)**

`ColorMathTests.cs`:
- sRGB transfer golden pairs at `0.0`, the `0.0031308` boundary (both sides), `0.5` (round-trip
  encode(decode(0.5)) ≈ 0.5), `1.0`.
- `SrgbEncode(0.5) * 255` rounds to `188` (matches DESIGN.md's stated golden pair).

`ToneMapperTests.cs` (construct synthetic 1x1 or NxN `CapturedFrame`s with a fake `MonitorInfo`,
`SdrWhiteNits` as needed):
- scRGB `3.0` @ SDR white 240 → byte 255 (all channels equal, isolated pixel: scale=1/3,
  c_lin=1.0, M=1.0 which is `<= 1.0+ε` → pass-through → `SrgbEncode(1.0)*255 = 255`).
- scRGB `1.5` @ 240 → byte 188 (same reasoning: c_lin=0.5, pass-through).
- `0.0` → byte `0`; negative scRGB input → clamped to `0`.
- SDR-parity: a whole frame with every pixel `<= 1.0` after normalization maps EXACTLY to
  `round(SrgbEncode(...))` for every pixel — assert byte-for-byte against an independently
  computed reference array, not just spot values.
- Shoulder continuity: pick `m` just below and just above `knee` (e.g. `knee - 1e-6`,
  `knee + 1e-6`) in a frame where `M > 1+ε`; assert the mapped output bytes are adjacent (differ
  by at most 1 LSB) — proves C1 continuity in practice.
  `f(peak) == 1.0` exactly (within float epsilon). A value with `m > peak` clamps (same output as
  `m == peak`).
- `Bgra8Srgb` passthrough: construct a `CapturedFrame` with `Format = Bgra8Srgb` and known bytes;
  `SdrImage.FromCapturedFrame` returns those bytes unchanged (alpha forced to 255).

### 3.2 WP-B — M2 overlay UX

**Files owned:** `Overlay/OverlayController.cs`, `Overlay/OverlayWindow.xaml`,
`Overlay/OverlayWindow.xaml.cs`, `Overlay/SelectionAdorner.cs`, `Overlay/Magnifier.cs`,
`Overlay/AnnotationLayer.cs`, `Overlay/ToolbarControl.xaml`, `Overlay/ToolbarControl.xaml.cs`,
`Imaging/ClipboardService.cs`.

**Consumes (read-only) from other packages:** `Capture.CapturedFrame`, `Capture.MonitorInfo`,
`Capture.RectPhysical`, `Imaging.SdrImage`, `Imaging.PngWriter` (WP-A); `RoeSnipSettings`,
`OverlayResult`, `AppComposition` (WP-A, `Program.cs`). Does **not** reference `Imaging.JxrWriter`
or any `App/*` type — those cross-cutting actions happen in `AppComposition.RunCaptureFlowAsync`
after `RunAsync` returns (see §2.4/§2.7).

**Responsibilities**

- `OverlayController.cs` — `RunAsync` per §2.7:
  - Creates one `OverlayWindow` per `(frame, preview)` pair, all borderless/topmost/
    `ShowInTaskbar=false`, `WindowStartupLocation=Manual`. In each window's `SourceInitialized`
    handler, call `NativeMethods.SetWindowPos` with the monitor's physical bounds (before first
    render, to avoid the WM_DPICHANGED size-bounce per DESIGN.md). The preview `Image` uses
    `Stretch=Fill` at the monitor's physical size.
  - Selection lives on exactly one monitor at a time: starting a drag on monitor A clears any
    selection on B. Track "active" monitor via mouse-enter → `Activate()`.
  - Keyboard is broadcast, not per-window: `OverlayController` listens for
    Esc/Enter/Ctrl+C/Ctrl+S at the controller level (e.g. each `OverlayWindow` forwards its
    `PreviewKeyDown` to a shared handler) so any monitor's window can drive the whole session; all
    windows close together on Esc/confirm.
  - On confirm (Enter / double-click / toolbar "Copy" or "Save"):
    - Render the annotated crop: `DrawingVisual` + `RenderTargetBitmap` at 96 DPI (1:1 physical
      pixel mapping) over the selection rect of that monitor's preview `SdrImage`, burn in
      `AnnotationLayer`'s shapes, convert to an `SdrImage` (`RenderedImage`).
    - Copy path (if `Ctrl+C`, toolbar "Copy", or `settings.CopyOnSelect` true on confirm):
      `ClipboardService.CopyToClipboard(renderedImage)` (PNG + CF_DIBV5, §3.2 below), trigger a
      brief shutter-flash visual (e.g. a white `Rectangle` opacity animation over the selection),
      set `CopyPerformed = true`.
    - Save path (if `Ctrl+S` or toolbar "Save"): show `Microsoft.Win32.SaveFileDialog` defaulting
      to `settings.SaveDirectory` and filename `roesnip_{yyyyMMdd_HHmmss}.png`; on success,
      `Imaging.PngWriter.WriteFile(path, renderedImage)`, set `SavedPngPath = path`.
    - "Save HDR" toolbar button: sets `SaveHdrRequested = true` on the eventual result — **does
      not** call any HDR-export API itself (no `JxrWriter` reference); the actual write happens in
      `AppComposition.RunCaptureFlowAsync` after this method returns, using `SourceFrame` +
      `SelectionPx`.
    - Return the populated `OverlayResult`.
  - On Esc: close all windows, return `null`.

- `OverlayWindow.xaml`/`.xaml.cs` — frozen preview display, dimming outside selection (e.g. an
  `OpacityMask`/four dimming rectangles), mouse drag-to-select, drag handles after initial
  selection (delegated to `SelectionAdorner`), hosts `ToolbarControl` and `Magnifier`. All
  selection/crosshair math in physical pixels — convert WPF mouse DIPs using the window's
  `PresentationSource.CompositionTarget.TransformToDevice` at the point of use, never store DIPs
  anywhere that crosses back into `Capture`/`Imaging` types.

- `SelectionAdorner.cs` — drag handles (8-point), size badge (e.g. "1920 x 1080") in physical
  pixels, hit-testing for resize.

- `Magnifier.cs` — zoom loupe near the cursor. Hex/RGB readout: sample `preview` (the `SdrImage`)
  at the cursor's physical pixel — this is what the user "sees". Nits readout: sample `frame`
  (the `CapturedFrame`) at the same coordinate via `frame.ReadPixelNits(x, y)` — this is the
  "reads the FP16 source" killer feature; it can show a nits value implying a highlight even when
  the hex looks like plain white. Click-to-copy the hex string to the clipboard (plain text,
  `System.Windows.Clipboard.SetText` — a separate, simple clipboard write, not through
  `ClipboardService`).

- `AnnotationLayer.cs` — shape model (`Rectangle`, `Ellipse`, `Arrow`, `Line`, `Freehand`, `Text`),
  color + stroke width per shape, hit-testing for selection/move, undo stack (`Ctrl+Z`) as a
  simple command list. **Text tool is implemented last and is cuttable** if inline editing/IME
  support threatens the milestone — if cut, ship without it and note it in the final report; the
  other five tools must work.

- `ToolbarControl.xaml`/`.xaml.cs` — tool buttons, color picker, stroke width, undo, then Copy /
  Save / Save HDR buttons, positioned attached to the selection (e.g. below-right, flipping above
  if it would go off-screen).

- `Imaging/ClipboardService.cs`:
  ```csharp
  namespace RoeSnip.Imaging;
  public static class ClipboardService
  {
      // Writes both a PNG (registered "PNG" clipboard format, via PngWriter.Encode) and a
      // CF_DIBV5 (BITMAPV5HEADER, 32bpp BGRA, alpha channel preserved) to the clipboard in one
      // OpenClipboard/EmptyClipboard/SetClipboardData(x2)/CloseClipboard transaction, so both
      // "paste as PNG" (browsers, Discord) and "paste as bitmap" (Word, Paint) consumers work.
      public static void CopyToClipboard(SdrImage image);
  }
  ```

**Acceptance criteria:** manual/visual — overlay shows the frozen frame, drag selects a region,
handles resize it, Esc cancels, Enter/double-click confirms, Ctrl+C copies (flash cue), Ctrl+S
saves, annotation tools draw and undo, magnifier shows RGB/hex + nits and click-to-copy works,
multi-monitor: dragging on one monitor's window doesn't start a selection on another, closing one
closes all. (No automated UI test is required in v1 — DESIGN.md's test plan marks this "manual".)

### 3.3 WP-C — M3 app shell

**Files owned:** `App/TrayApp.cs`, `App/HotkeyManager.cs`, `App/Settings.cs`,
`App/SettingsWindow.xaml`, `App/SettingsWindow.xaml.cs`, `App/StartupManager.cs`,
`Imaging/JxrWriter.cs`, `tests/RoeSnip.Tests/JxrRoundTripTests.cs`,
`tests/RoeSnip.Tests/SettingsTests.cs`, a publish profile (see below), `README.md`.

**Consumes (read-only):** `RoeSnipSettings`, `OverlayResult`, `ITrayNotifier`, `AppComposition`,
`CliOptions` (WP-A, `Program.cs`); `CapturedFrame`, `RectPhysical` (WP-A, `Capture/*`);
`NativeMethods` (WP-A, `Interop/*`).

**Responsibilities**

- `App/Settings.cs`:
  ```csharp
  namespace RoeSnip.App;
  public static class SettingsStore
  {
      // %APPDATA%\RoeSnip\settings.json. Fail-closed: if the file is missing, empty, or fails to
      // parse, return RoeSnipSettings.Default WITHOUT writing/overwriting the file. Unknown JSON
      // fields are ignored (forward compat); missing fields use the record's own defaults.
      public static RoeSnipSettings Load();
      // Atomic write: write to a temp file in the same directory, then File.Replace/Move.
      public static void Save(RoeSnipSettings settings);
  }
  ```
  At the bottom: `[ModuleInitializer]` sets `AppComposition.LoadSettings = SettingsStore.Load;`.

- `App/HotkeyManager.cs` — message-only window (`HWND_MESSAGE` parent) that calls
  `NativeMethods.RegisterHotKey`. Handles `WM_HOTKEY` in its `WndProc`, invokes a callback (wired
  by `TrayApp` to `AppComposition.RunCaptureFlowAsync`). PrintScreen consent flow per DESIGN.md:
  on first run, read `HKCU\Control Panel\Keyboard\PrintScreenKeyForSnippingEnabled`; if non-zero,
  show a one-time dialog asking whether to disable it (writes the registry value to 0 if the user
  agrees) or instead register `Ctrl+PrintScreen`. **`RegisterHotKey` returning `true` is not proof
  of delivery** — after registering, this class cannot itself verify end-to-end delivery (that
  requires the user to actually press the key and see the overlay appear); document this
  limitation in a code comment, do not attempt to fake a verification.

- `App/TrayApp.cs`:
  ```csharp
  namespace RoeSnip.App;
  public sealed class TrayApp : ITrayNotifier
  {
      public static int Run(string[] args); // NotifyIcon + context menu (Capture, Settings, About, Exit) + message loop
      public void ShowSavedBalloon(string filePath); // balloon with "open folder" action (Process.Start w/ /select,path)
      public void ShowError(string message);
  }
  ```
  `Run` constructs `Settings` (via `SettingsStore.Load()` directly — it's in the same package),
  `HotkeyManager`, wires the hotkey callback and the "Capture" menu item both to
  `AppComposition.RunCaptureFlowAsync(settings, notifier: this)`, and also owns the single-instance
  plumbing: a named pipe (e.g. `\\.\pipe\RoeSnip-SingleInstance`) or `WM_COPYDATA` receiver; if a
  named mutex (`Global\RoeSnip-SingleInstance`) is already held on startup, instead of creating a
  second tray icon, signal the existing instance to run the capture flow and exit immediately with
  code 0. (This mutex/signal check belongs in `TrayApp.Run`, since `Program.Main` only calls
  `AppComposition.RunTray(args)` — no separate file needed, and no edit to `Program.cs`.)
  Shutter-flash cue on copy is implemented in WP-B's overlay; `TrayApp` only handles the
  post-close balloon.
  At the bottom: `[ModuleInitializer]` sets `AppComposition.RunTrayApp = TrayApp.Run;`.

- `App/SettingsWindow.xaml`/`.xaml.cs` — minimal WPF window bound to a `RoeSnipSettings`-shaped
  view model; on save, calls `SettingsStore.Save(...)` and re-registers the hotkey if it changed.
  Exposes: hotkey (display + "change" capture control), save directory (folder picker),
  auto-save-HDR-copy toggle, tone-map knee/peak override fields (advanced, optional/nullable —
  blank means "use default"), run-at-startup toggle (wired to `StartupManager`), copy-on-select
  toggle.

- `App/StartupManager.cs`:
  ```csharp
  namespace RoeSnip.App;
  public static class StartupManager
  {
      public static void SetRunAtStartup(bool enabled); // HKCU\Software\Microsoft\Windows\CurrentVersion\Run, value "RoeSnip" = quoted exe path
      public static bool IsRunAtStartupEnabled();
  }
  ```

- `Imaging/JxrWriter.cs`:
  ```csharp
  namespace RoeSnip.Imaging;
  public static class JxrWriter
  {
      // Writes frame cropped to cropPx as .jxr, no annotations, raw pixel data:
      //   Fp16ScRgb frames: FP16 scRGB values written as-is (headroom preserved).
      //   Bgra8Srgb frames: also supported (degenerate SDR-only case) — decode to linear scRGB
      //     via ColorMath.SrgbByteToLinear before encoding, so the file is still valid scRGB.
      // Encoder selection per DESIGN.md: prefer WPF WmpBitmapEncoder IF the acceptance test
      // (JxrRoundTripTests) proves float survival; else use Vortice.Direct2D1's WIC bindings
      // directly with a 128bppRGBAFloat scRGB pixel format.
      public static void Write(string path, Capture.CapturedFrame frame, Capture.RectPhysical cropPx);
  }
  ```
  At the bottom: `[ModuleInitializer]` sets `AppComposition.WriteJxr = JxrWriter.Write;`.

- Publish profile: `src/RoeSnip/Properties/PublishProfiles/win-x64.pubxml`:
  ```xml
  <Project>
    <PropertyGroup>
      <Configuration>Release</Configuration>
      <Platform>x64</Platform>
      <RuntimeIdentifier>win-x64</RuntimeIdentifier>
      <SelfContained>true</SelfContained>
      <PublishSingleFile>true</PublishSingleFile>
      <IncludeNativeLibrariesForSelfExtract>true</IncludeNativeLibrariesForSelfExtract>
      <PublishReadyToRun>false</PublishReadyToRun>
    </PropertyGroup>
  </Project>
  ```
  (`IncludeNativeLibrariesForSelfExtract` is required for WPF single-file per DESIGN.md.)

- `README.md` — what RoeSnip is (one paragraph from DESIGN.md's "Why this exists"), build/run
  instructions (`dotnet build`, `dotnet test`, `dotnet run -- --diag`), publish command, default
  hotkey and where settings live.

**Tests (WP-C owns):**

`JxrRoundTripTests.cs` — build a synthetic `Fp16ScRgb` `CapturedFrame` containing a pixel value of
`3.0` in all channels; `JxrWriter.Write` to a temp file; read it back (via the same decoder path
`JxrWriter` would use, or a minimal WIC/WPF decode) and assert a value `> 1.0` survives (i.e., not
flattened to 8-bit/[0,1]). This is the acceptance gate DESIGN.md requires before trusting
`WmpBitmapEncoder` — if it fails, `JxrWriter` must use the WIC 128bppRGBAFloat fallback instead,
and the test must then pass against that path.

`SettingsTests.cs` — `Load()` on a missing directory/file returns `Default` and does not create
the file; `Load()` on a corrupt JSON file returns `Default` and leaves the corrupt file untouched
(fail-closed); `Save()` then `Load()` round-trips every field.

---

## 4. Integration & test sequence

**Build order for the integrator** (after all three packages report done):

1. `dotnet build RoeSnip.sln` — first full compile with all files present. Expect friction here:
   this is the first point at which WP-A's `Program.cs` (referencing `OverlayResult`,
   `RoeSnipSettings`, hook signatures) actually resolves against WP-B/WP-C's real implementations
   rather than "hooks are null." Fix signature drift by re-reading §2 — do not change a contract
   without re-checking every consumer.
2. `dotnet test tests/RoeSnip.Tests/RoeSnip.Tests.csproj` — all four test files must pass:
   `ColorMathTests`, `ToneMapperTests`, `JxrRoundTripTests`, `SettingsTests`.
3. `dotnet run --project src/RoeSnip -- --diag` on the integrator's real machine (interactive
   session required — Desktop Duplication fails with `E_ACCESSDENIED` from service-like/RDP
   session-0 contexts, per DESIGN.md). Verify: one line per real monitor, sane resolution, correct
   advanced-color flag if the integrator has an HDR/ACM display, plausible SDR white nits (default
   240 if unsure).
4. `dotnet run --project src/RoeSnip -- --capture --monitor 0` — verify exit code 0, PNG file
   exists, dimensions match the diag output for monitor 0, and the printed min/max/avg nits are
   sane (avg not near 0 or near max unless the monitor is genuinely all-black/all-white).
5. `dotnet run --project src/RoeSnip -- --capture --monitor 0 --jxr` — verify a `.jxr` file is
   also produced once WP-C has landed.
6. Launch `dotnet run --project src/RoeSnip` with no args (tray mode): verify the tray icon
   appears, hotkey triggers the overlay, selection + annotate + Copy pastes correctly into e.g.
   Paint/Notepad (as an image)/a browser, Save writes a PNG and shows the balloon, launching a
   second instance while the first is running triggers the first's capture flow and the second
   process exits immediately.
7. `dotnet publish src/RoeSnip -p:PublishProfile=win-x64` — verify the single-file exe launches
   without the .NET SDK installed (or at least runs standalone) and repeats step 6 sanity checks.

**Known integration risks**

- **Signature drift.** Because WP-A, WP-B, WP-C are written concurrently by separate agents
  against a written spec (not against each other's actual code), the most likely integration
  failure is a small signature mismatch (e.g. tuple field names, nullability, parameter order).
  The integrator's first `dotnet build` will surface these as compile errors — fix by matching §2
  exactly, in whichever file drifted; do not "fix" by changing the contract in a way that breaks
  a third consumer.
- **`[ModuleInitializer]` ordering.** Multiple module initializers across different files all run
  before `Main`, order is unspecified between files but each hook is independent (no ordering
  dependency between `LoadSettings`, `RunOverlay`, `WriteJxr`, `RunTrayApp`), so this is safe —
  but if any package accidentally sets a hook conditionally (e.g. inside an `if`), it could end up
  null at runtime despite the file being present. Module initializers should be unconditional.
  If `[ModuleInitializer]`'s target method is on a `file`-scoped class, confirm it still runs
  (it does — accessibility doesn't affect module-initializer execution) and confirm the tests
  project doesn't accidentally trigger it in a way that pollutes other tests (each xunit test
  runs in the same process/AppDomain by default — if any test reads `AppComposition`'s hooks,
  isolate that in its own file/collection).
- **`RowPitch` vs tight packing.** The single most likely silent-corruption bug: any code that
  assumes `CapturedFrame.Stride == Width * BytesPerPixel`. Re-verify every place that indexes
  pixels (ToneMapper, SdrImage passthrough, Magnifier, JxrWriter) uses `Stride`, not a recomputed
  width-based stride.
- **DPI on the overlay.** If `SourceInitialized`/`SetWindowPos` ordering is wrong, the overlay
  window can render at the wrong size for one frame before snapping — verify visually on a
  mixed-DPI multi-monitor setup if the integrator has one; if not, at least verify single-monitor
  and note mixed-DPI as unverified.
- **Vortice API surface drift.** Vortice.Windows 3.8.3's exact C# method names/overloads for
  `DuplicateOutput1`/`GetDesc1` etc. were not hand-verified against the library's source in this
  planning pass (only the underlying COM contract was confirmed via Microsoft docs). WP-A's
  implementer should treat any compile mismatch here as "consult the installed package's XML docs
  / decompiled source," not as a reason to deviate from the DESIGN.md behavior.
- **JXR encoder choice.** `JxrWriter`'s behavior literally branches on whether
  `JxrRoundTripTests` passes with `WmpBitmapEncoder` — WP-C must actually run that test locally
  before considering the encoder choice final, per DESIGN.md's explicit acceptance-gate language.

---

## 5. P/Invoke appendix — `Interop/NativeMethods.cs` (owned by WP-A)

```csharp
using System;
using System.Runtime.InteropServices;

namespace RoeSnip.Interop;

public static class NativeMethods
{
    // ---------- Basic structs ----------

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    public struct LUID { public uint LowPart; public int HighPart; }

    // ---------- Monitor enumeration ----------

    public delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdcMonitor, ref RECT lprcMonitor, IntPtr dwData);

    [DllImport("user32.dll")]
    public static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr lprcClip, MonitorEnumProc lpfnEnum, IntPtr dwData);

    public const uint MONITORINFOF_PRIMARY = 0x1;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct MONITORINFOEX
    {
        public uint cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string szDevice;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFOEX lpmi);

    // ---------- DPI ----------

    public enum MONITOR_DPI_TYPE { MDT_EFFECTIVE_DPI = 0, MDT_ANGULAR_DPI = 1, MDT_RAW_DPI = 2 }

    [DllImport("shcore.dll")]
    public static extern int GetDpiForMonitor(IntPtr hmonitor, MONITOR_DPI_TYPE dpiType, out uint dpiX, out uint dpiY);

    // ---------- QueryDisplayConfig / SDR white level ----------

    public const uint QDC_ONLY_ACTIVE_PATHS = 0x00000002;

    [StructLayout(LayoutKind.Sequential)]
    public struct DISPLAYCONFIG_RATIONAL { public uint Numerator; public uint Denominator; }

    [StructLayout(LayoutKind.Sequential)]
    public struct DISPLAYCONFIG_PATH_SOURCE_INFO
    {
        public LUID adapterId;
        public uint id;
        public uint modeInfoIdx;
        public uint statusFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct DISPLAYCONFIG_PATH_TARGET_INFO
    {
        public LUID adapterId;
        public uint id;
        public uint modeInfoIdx;
        public uint outputTechnology;
        public uint rotation;
        public uint scaling;
        public DISPLAYCONFIG_RATIONAL refreshRate;
        public uint scanLineOrdering;
        public int targetAvailable;
        public uint statusFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct DISPLAYCONFIG_PATH_INFO
    {
        public DISPLAYCONFIG_PATH_SOURCE_INFO sourceInfo;
        public DISPLAYCONFIG_PATH_TARGET_INFO targetInfo;
        public uint flags;
    }

    // DISPLAYCONFIG_MODE_INFO is a tagged union (infoType(4) + id(4) + LUID adapterId(8) +
    // 48-byte union) = 64 bytes total. We never read its fields (only need array element size
    // for correct marshaling), so declare it opaque at the correct size.
    [StructLayout(LayoutKind.Sequential, Size = 64)]
    public struct DISPLAYCONFIG_MODE_INFO { }

    [DllImport("user32.dll")]
    public static extern int GetDisplayConfigBufferSizes(uint flags, out uint numPathArrayElements, out uint numModeInfoArrayElements);

    [DllImport("user32.dll")]
    public static extern int QueryDisplayConfig(
        uint flags,
        ref uint numPathArrayElements, [In, Out] DISPLAYCONFIG_PATH_INFO[] pathArray,
        ref uint numModeInfoArrayElements, [In, Out] DISPLAYCONFIG_MODE_INFO[] modeInfoArray,
        IntPtr currentTopologyId);

    [StructLayout(LayoutKind.Sequential)]
    public struct DISPLAYCONFIG_DEVICE_INFO_HEADER
    {
        public uint type;   // DISPLAYCONFIG_DEVICE_INFO_TYPE
        public uint size;
        public LUID adapterId;
        public uint id;
    }

    public const uint DISPLAYCONFIG_DEVICE_INFO_GET_SOURCE_NAME = 1;
    public const uint DISPLAYCONFIG_DEVICE_INFO_GET_SDR_WHITE_LEVEL = 11;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct DISPLAYCONFIG_SOURCE_DEVICE_NAME
    {
        public DISPLAYCONFIG_DEVICE_INFO_HEADER header;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string viewGdiDeviceName; // matches DXGI_OUTPUT_DESC.DeviceName
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct DISPLAYCONFIG_SDR_WHITE_LEVEL
    {
        public DISPLAYCONFIG_DEVICE_INFO_HEADER header;
        public uint SDRWhiteLevel; // nits = SDRWhiteLevel / 1000.0 * 80.0
    }

    [DllImport("user32.dll", EntryPoint = "DisplayConfigGetDeviceInfo")]
    public static extern int DisplayConfigGetDeviceInfoSourceName(ref DISPLAYCONFIG_SOURCE_DEVICE_NAME requestPacket);

    [DllImport("user32.dll", EntryPoint = "DisplayConfigGetDeviceInfo")]
    public static extern int DisplayConfigGetDeviceInfoSdrWhiteLevel(ref DISPLAYCONFIG_SDR_WHITE_LEVEL requestPacket);

    // ---------- Hotkeys ----------

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    public const uint MOD_ALT = 0x0001, MOD_CONTROL = 0x0002, MOD_SHIFT = 0x0004, MOD_WIN = 0x0008, MOD_NOREPEAT = 0x4000;
    public const int WM_HOTKEY = 0x0312;
    public const int VK_SNAPSHOT = 0x2C;

    // ---------- Window positioning ----------

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

    public static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
    public const uint SWP_NOACTIVATE = 0x0010, SWP_SHOWWINDOW = 0x0040, SWP_NOZORDER = 0x0004;

    // ---------- Clipboard ----------

    [DllImport("user32.dll", SetLastError = true)] public static extern bool OpenClipboard(IntPtr hWndNewOwner);
    [DllImport("user32.dll", SetLastError = true)] public static extern bool CloseClipboard();
    [DllImport("user32.dll", SetLastError = true)] public static extern bool EmptyClipboard();
    [DllImport("user32.dll", SetLastError = true)] public static extern IntPtr SetClipboardData(uint uFormat, IntPtr hMem);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern uint RegisterClipboardFormat(string lpszFormat);

    [DllImport("kernel32.dll", SetLastError = true)] public static extern IntPtr GlobalAlloc(uint uFlags, UIntPtr dwBytes);
    [DllImport("kernel32.dll", SetLastError = true)] public static extern IntPtr GlobalLock(IntPtr hMem);
    [DllImport("kernel32.dll", SetLastError = true)] public static extern bool GlobalUnlock(IntPtr hMem);

    public const uint GMEM_MOVEABLE = 0x0002;
    public const uint CF_DIBV5 = 17;
    // "PNG" is the well-known registered clipboard format name recognized by browsers, Discord,
    // Paint.NET, etc. Call RegisterClipboardFormat("PNG") to get its uFormat value at runtime.

    // ---------- Windows.Graphics.Capture interop ----------

    [ComImport]
    [Guid("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IGraphicsCaptureItemInterop
    {
        IntPtr CreateForWindow([In] IntPtr window, [In] ref Guid iid);
        IntPtr CreateForMonitor([In] IntPtr monitor, [In] ref Guid iid);
    }
}
```

Notes:
- `DisplayConfigGetDeviceInfo` is one native function with two different request-struct shapes
  used here; C# needs two `DllImport`s with distinct names sharing `EntryPoint`, as written above.
- The `DISPLAYCONFIG_MODE_INFO` 64-byte size was derived from the documented union members
  (`DISPLAYCONFIG_TARGET_MODE` containing `DISPLAYCONFIG_VIDEO_SIGNAL_INFO`, the largest union
  member at 48 bytes) + the 16-byte common header — verify against `wingdi.h` if `QueryDisplayConfig`
  throws a marshaling error.
- The `IGraphicsCaptureItemInterop` GUID above is the widely-published, stable IID used by
  community WGC interop wrappers; if `CreateForMonitor` fails to resolve/QueryInterface at
  runtime, double check against the current Windows SDK's `windows.graphics.capture.interop.h`.

---

## Plan-time flags

1. **"Vortice.WIC" does not exist as a NuGet package.** DESIGN.md's Stack section says "Vortice.WIC
   if the JXR fallback is needed." Verified on nuget.org: WIC bindings ship inside
   **`Vortice.Direct2D1`** (3.8.3, net8.0-compatible), not a standalone package. Plan pins
   `Vortice.Direct2D1` for this purpose. Behavior is unaffected; this is a packaging-name
   correction only.
2. **DESIGN.md's own milestone list (M2) mentions "WGC fallback" as part of the overlay milestone**,
   but its architecture file tree puts `WgcCapturer.cs` under `Capture/`, and this plan's WP-A file
   list (per the planning brief's explicit "Capture/*" wildcard) assigns all of `Capture/*`
   including `WgcCapturer.cs` to WP-A. Resolved by having WP-A implement the WGC fallback fully as
   part of M1/WP-A — it's a `Capture/` file and DD-alone is sufficient for the CLI test mode on an
   interactive session, so this doesn't block WP-A's own `--diag`/`--capture` acceptance test; WP-B
   simply gets a working fallback for free when it needs it for RDP/session-0-denied contexts.
3. **`OverlayController.RunAsync`'s signature was extended** from the planning brief's suggested
   `Task<OverlayResult?> RunAsync(IReadOnlyList<(CapturedFrame frame, SdrImage preview)>)` to also
   take a `RoeSnipSettings` parameter. This isn't a DESIGN.md contradiction — DESIGN.md's own §6
   requires the overlay to honor `copy-on-select` and a default save directory, which are settings
   values; the brief's signature said "or similar," so this plan reads that as license to add the
   one parameter needed for correctness rather than have Overlay reach for global state.
4. **Cross-package call graph is not a strict DAG matching milestone order** (WP-B's "Save HDR"
   button conceptually needs WP-C's `JxrWriter`; WP-C's tray balloon needs to react to WP-B's save
   result). Resolved by threading those specific actions back through `AppComposition` (owned by
   WP-A, §2.4) instead of having WP-B call WP-C directly or vice versa — this keeps file ownership
   disjoint and avoids a two-way compile dependency between WP-B and WP-C. This is a plan-level
   design decision, not a DESIGN.md gap, called out here because it's the least obvious part of
   the contract and worth an implementer double-checking against.
5. **Vortice.Windows 3.8.3's exact C# method overloads** for `IDXGIOutput5.DuplicateOutput1`,
   `IDXGIOutput6.GetDesc1`, etc. were confirmed against the underlying COM/Win32 signatures (via
   Microsoft Learn) but not against the library's actual generated C# bindings (no runnable sample
   was found for this specific library+API combination during planning). WP-A's implementer should
   expect to adjust call syntax to match the installed package version; the semantics in §3.1 are
   the authoritative spec regardless of exact call syntax.
6. **JXR export of a pure-SDR (`Bgra8Srgb`) frame** isn't addressed by DESIGN.md (which frames
   "Save HDR" as inherently about FP16 content). This plan has `JxrWriter` handle it anyway
   (decode sRGB → linear scRGB, encode) rather than disable the button for SDR-only monitors, since
   DESIGN.md doesn't say to hide the button conditionally and disabling it would need extra state
   plumbing. Low-stakes; flagged for awareness, not because it needs a decision before coding.

---
