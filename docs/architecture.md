# RoeSnip architecture

How RoeSnip captures, tone-maps and presents a screenshot, and how the code is split. Behavior
that is checked per feature and per OS lives in [parity.md](parity.md); how to drive the app from a
script lives in [testing.md](testing.md).

## Why it exists

With HDR (or Windows 11 Auto Color Management) on, Windows composites the desktop in linear
scRGB: FP16, 1.0 = 80 nits, sRGB primaries, values above 1.0 for highlights and negative for wide
gamut. Legacy capture paths hard-clip that buffer or divide it by the wrong constant, which gives
the washed-out gray look. Two things must be handled:

1. **SDR white level.** SDR windows are composited at `sdrWhiteNits / 80` (slider at 240 nits ->
   SDR white is 3.0).
2. **HDR highlights** need a smooth rolloff into 8-bit sRGB, not a clip and not a linear squash.

RoeSnip captures the real FP16 frame and does both. A shot of SDR-only content is
pixel-identical to a shot on an SDR monitor, HDR content gets a hue-preserving rolloff, and the
untouched HDR original can be saved as JPEG XR (Windows through WIC, Linux and macOS through jxrlib).

## Projects

```
src/RoeSnip/                    WPF app, Windows only (net8.0-windows10.0.22621.0): tray, overlay,
                                annotations, recording, sharing, self-update. Ships as RoeSnip.exe.
src/RoeSnip.App/                Avalonia 12 app (net8.0 and net8.0-windows...): Linux, macOS and Windows.
src/RoeSnip.Core/               net8.0, no UI dependency: capture contracts, tone mapping, imaging
                                (SkiaSharp), settings, updates, overlay math, recording, sharing.
src/RoeSnip.Platform.Windows/   Desktop Duplication + WGC capture, SDR white level, JPEG XR, MP4.
src/RoeSnip.Platform.Linux/     Portal / ScreenCast (PipeWire) / X11 capture, GStreamer MP4.
src/RoeSnip.Platform.MacOS/     .NET side of the scksnap helper.
helpers/scksnap/                Swift helper for macOS capture and recording (macOS runners only).
tests/                          RoeSnip.Tests (WPF), Core.Tests, App.Tests, Platform.Windows.Tests.
```

The WPF app keeps its own copies of the color, capture and imaging code; `RoeSnip.Core` is an
extraction of that code for the Avalonia app, not a refactor of the WPF app. A change to the WPF app is
ported to the Avalonia app (see [parity.md](parity.md)); shared behavior belongs in Core.

`RoeSnip.App` picks its capture backend at startup through `CaptureBackendRegistry`. It references
`RoeSnip.Platform.Linux` only for RID `''` or `linux-*`, so use of that assembly in App code sits
inside `#if LINUX_BACKEND`. `RoeSnip.Platform.Windows` and `.MacOS` are referenced the same way per OS.

Config lives in a per-OS directory (`Core/Settings/ConfigPaths.cs`): Windows `%APPDATA%\RoeSnip.App`,
macOS `~/Library/Application Support/RoeSnip.App`, Linux `$XDG_CONFIG_HOME/roesnip-app` or
`~/.config/roesnip-app`. The WPF app uses `%APPDATA%\RoeSnip`. The two apps use separate settings
files, single-instance names and Run keys so both can be resident without one answering the other's
hotkey. An unreadable `settings.json` falls back to defaults in memory and is not overwritten.

## Capture

On trigger every monitor is captured first, then the overlays appear (freeze semantics). The dim
shows before capture and never takes foreground, so tooltips and hover UI present at hotkey time
survive into the frame.

### Windows

Per monitor:

1. Enumerate with DXGI (`IDXGIOutput6`) and create the D3D11 device on the adapter that owns the
   output (hybrid-GPU laptops). SDR white level comes from `DisplayConfigGetDeviceInfo`
   (`GET_SDR_WHITE_LEVEL`, nits = `SDRWhiteLevel / 1000 * 80`); a failed query uses 240 nits.
   `MaxLuminance` of 0 or an absurd value uses 1000 nits.
2. **Desktop Duplication** is primary: `DuplicateOutput1` offering FP16 and BGRA8. The pipeline
   branches on the format actually delivered, not on the Advanced Color flag (ACM composes SDR
   displays in FP16 too). FP16 is scRGB, BGRA8 is passthrough. The frame is copied to a staging
   texture before `ReleaseFrame`. `DXGI_ERROR_ACCESS_LOST` re-enumerates and retries once, then
   falls back.
3. **Windows.Graphics.Capture** is the fallback (RDP and other duplication-denied contexts), via
   `CreateFreeThreaded` so it works without a dispatcher. The yellow capture border may show on this
   path; unpackaged apps cannot suppress it.
4. `CapturedFrame` holds the raw pixels and the monitor metadata. FP16 frames stay in memory for
   the session: the loupe's nits readout and the JPEG XR export read them.

Screenshots never include the mouse cursor (`IsCursorCaptureEnabled = false`; Desktop Duplication
never reads the pointer shape). Recordings do include it. DRM-protected video and windows that
exclude themselves from capture (`WDA_EXCLUDEFROMCAPTURE`) are black on both backends; no capture
API can read them, and RoeSnip uses the same flag on its own overlay windows.

### Linux and macOS

- **Linux:** on Wayland, captures come from a kept-open xdg-desktop-portal ScreenCast session (one
  share dialog, remembered by a token); the cursor is drawn in from the stream's cursor metadata.
  GNOME's screenshot portal (shutter sound and flash) is not used. X11 uses `XGetImage`. Frames are
  SDR (`Bgra8Srgb`) except on a monitor in HDR mode on GNOME 50 or newer: mutter then offers
  half-float PQ BT.2020 frames, DMA-BUF only (the 8-bit stream is mutter's own tone-mapped copy and
  loses the HDR values). `PipeWireHdrReader` (own libpipewire consumer on its own remote of the same
  session) takes them, `DmaBufGl` imports each frame through EGL into an RGBA16F texture, and a
  capture reads it back and `PqBt2020` converts it to linear scRGB with SDR white 203 nits
  (`SdrWhiteInBufferUnits` 203/80), so everything after capture is the Windows path. Any failure
  leaves that monitor on the 8-bit stream. See docs/parity.md item 35.
- The .jxr on Linux and macOS is `JxrExport`: a float TIFF run through the bundled jxrlib
  `JxrEncApp -c 28`, the same 128bppRGBAFloat scRGB file Windows writes through WIC.
- **macOS:** the Swift helper `scksnap` (ScreenCaptureKit) captures and records. macOS 15 on Apple
  silicon asks for an HDR screenshot; older or Intel Macs get SDR. A TCC "screen recording denied"
  exit code is surfaced as an error in the UI.

Buffers stay raw and the metadata carries the convention: `CapturedFrame.SdrWhiteInBufferUnits` is
sdrWhiteNits/80 for Windows scRGB, 1.0 for macOS EDR, and unused for SDR frames. Tone mapping divides
by it, the nits readout multiplies back, and the JPEG XR export writes original values.

## Tone-map pipeline (`ToneMapper`)

Input FP16 scRGB, output BGRA8 sRGB, computed once per captured frame (not per crop), so the preview
equals the export:

1. `c_lin = c_scRGB / SdrWhiteInBufferUnits` (SDR white becomes 1.0). Negatives clamp to 0.
2. `M` = the maximum channel over the frame.
   - `M <= 1 + epsilon`: exact pass-through, inverse sRGB and round-to-nearest, no shoulder, no
     dither. This keeps SDR-only shots bit-identical to an SDR monitor.
   - Otherwise a hue-preserving Hermite soft knee: `knee = 0.90`, `peak = max(2.0, min(M,
     maxLuminance / sdrWhiteNits))`. Pixels with `m = max(r,g,b)` above the knee map through a C1
     shoulder `[knee, peak] -> [knee, 1.0]` (slope 1 at the knee, 0 at the peak) and all three
     channels scale by `f(m)/m`. Settings may override knee and peak.
3. Proper piecewise inverse sRGB EOTF (not gamma 2.2), then quantize. Ordered dither (+-0.5 LSB) is
   applied only to shoulder-mapped pixels.

`BGRA8` frames bypass the tone mapper. The overlay preview is exactly the tone-mapped SDR image.

## Overlay

- One borderless, topmost overlay window per monitor. All selection, loupe and annotation-export
  math is in physical pixels; DIPs or Avalonia logical units never leave the view layer.
- **Mixed DPI (Windows):** a window parked off-screen keeps its creation monitor's DPI, so moving it
  on screen raises `WM_DPICHANGED` with a scaled rect. Each window is positioned with `SetWindowPos` in
  physical pixels before first render and pins that rect (`DpiRectPin`).
- Only one window holds keyboard focus: the overlay controller forwards key commands to all windows
  (Esc, Enter, Ctrl+C, Ctrl+S work from any monitor) and mouse-enter activates another window.
  All overlays close together.
- Annotations are vector shapes over the preview and are rasterized 1:1 on export.
- The loupe shows a nits readout from the FP16 source and previews the selection borders while a
  drag is in progress.

### Cross-monitor selection

A fresh drag can span monitors. The window that owns the drag captures the mouse, so it keeps
receiving moves past its edge and computes a virtual-desktop rectangle. The session
(`OverlaySession`) clamps it to the union of monitor bounds, intersects it with each monitor
(`Core/Overlay/SpanningSelectionMath.cs`), and pushes each monitor's local slice into that window's
ordinary selection pipeline. The selection is "spanning" only while two or more monitors are touched;
single-monitor selections take the original path unchanged.

- Handles and body drags work on a spanning selection's real edges (`SpanningResize` and
  `SpanningMove`); an edge that is only a monitor-boundary clip is not a handle.
- Only the window that owns the drag shows the toolbar and size badge (true composite size); the
  others draw a dashed border.
- Annotations are off for a spanning selection (a shape would need a cross-window coordinate
  space). Copy, Save, Save HDR and Record are available.
- Copy and Save composite each monitor's already tone-mapped crop; gaps with no monitor are opaque
  black. Save HDR stitches the raw FP16 crops into one JPEG XR (raw scRGB is one absolute space, so
  stitching needs no per-monitor conversion). Recording composites the live captures of each
  intersected monitor.
- The `select` automation command takes virtual-desktop pixels and runs the same code path as a drag.

## Failure modes

- **Desktop Duplication black frame (NVIDIA RTX with HDR):** duplication can succeed structurally
  and deliver an all-zero buffer, alpha included, which a real frame never has. `FrameSanity` treats
  it as a capture failure and falls back to WGC. The per-monitor "duplication is broken here" memo
  persists in `capture-cache.json` (keyed by device name), so later launches skip the doomed retry.
- No HDR monitor, mixed HDR and SDR monitors, and ACM-on-SDR (an FP16 frame from an "SDR"
  display) all go through the delivered-format branch above.
- HDR toggled mid-capture: access lost, re-enumerate once, then WGC.
- Hotkey unavailable: PrintScreen may belong to Snipping Tool (`PrintScreenKeyForSnippingEnabled`);
  the app asks once, or falls back to another binding. `RegisterHotKey` succeeding is not proof of
  delivery.
- Second instance: signals the first over the single-instance pipe and exits.
