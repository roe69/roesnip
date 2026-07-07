# RoeSnip — HDR-correct screenshot tool for Windows

*(v1.1 — incorporates adversarial design review; API claims below were verified against
Microsoft docs where noted.)*

## Why this exists

On HDR/OLED monitors, Snipping Tool, Lightshot, ShareX-default, etc. produce washed-out,
gray screenshots. Root cause: with Advanced Color (HDR, or Win11 ACM) enabled, Windows
composites the desktop in **linear scRGB** (FP16, where `1.0 = 80 nits`, sRGB primaries,
values may exceed 1.0 and go negative for wide gamut). Legacy capture paths grab that
buffer and either hard-clip it or naively divide it, ignoring two things:

1. **SDR white level** — the "SDR content brightness" slider. SDR windows are composited at
   `sdrWhiteNits / 80` (e.g. slider at 240 nits → SDR white = scRGB `3.0`). Dividing by the
   wrong constant (or not at all, then clipping) is exactly the washed-out gray look.
2. **HDR highlights** — pixels brighter than SDR white need a smooth rolloff to fit in
   8-bit sRGB, not a hard clip (blown highlights) and not linear compression (dim, flat).

RoeSnip captures the true FP16 scRGB frame and does this correctly:

- Screenshots containing only SDR content come out **pixel-identical** to a screenshot on
  an SDR monitor (exact pass-through; see tone-map spec).
- HDR content gets a proper hue-preserving highlight rolloff.
- Optionally saves the untouched HDR original alongside (JPEG XR, like Xbox Game Bar).

## Product scope (v1)

Lightshot-style flow:

1. Tray app, starts with Windows (optional), single instance.
2. Global hotkey (default **PrintScreen**, configurable) freezes the screen.
   - Win11 intercepts PrtScr for Snipping Tool when
     `HKCU\Control Panel\Keyboard\PrintScreenKeyForSnippingEnabled` ≠ 0. On first run, if
     set, ask the user (dialog, one time) whether to disable it; otherwise register
     Ctrl+PrintScreen instead. `RegisterHotKey` succeeding is NOT proof of delivery —
     verify end-to-end.
3. Full-screen overlay per monitor shows the frozen frame; drag to select a region.
   - Dimmed outside selection; size badge; crosshair + magnifier with **RGB/hex and nits
     readout** (killer HDR feature — reads the FP16 source); click-to-copy color.
   - Selection adjustable (drag handles) after initial drag.
   - `Esc` cancels. `Enter` / double-click confirms. `Ctrl+C` copies. `Ctrl+S` saves.
4. Toolbar attached to selection: annotation tools (rectangle, ellipse, arrow, line,
   freehand, text; color + stroke width; Ctrl+Z undo), then **Copy**, **Save**, **Save HDR**.
   - Text tool is implemented LAST and is cuttable if it endangers the milestone (inline
     editing/IME is the known UX time sink).
5. Output:
   - Clipboard: PNG + CF_DIBV5 (annotations burned in). Brief shutter-flash cue on copy.
   - Save: PNG via dialog, default filename `roesnip_yyyyMMdd_HHmmss.png`; tray balloon
     with "open folder" after save.
   - Save HDR: `.jxr` (JPEG XR, FP16 scRGB, no annotations — raw crop). Off the main path;
     button + optional "always save HDR copy" setting.
6. Settings (JSON at `%APPDATA%\RoeSnip\settings.json`): hotkey, save directory,
   auto-save-HDR-copy toggle, tone-map knee/peak overrides (advanced), run-at-startup
   (HKCU Run key), copy-on-select toggle.
7. **CLI test mode** (essential for automated testing):
   `RoeSnip.exe --capture [--monitor N] [--out path] [--jxr]` — captures without any UI,
   writes PNG (+ JXR), prints per-monitor diagnostics to stdout: device name, resolution,
   advanced color on/off, capture format, SDR white nits, min/max/avg captured pixel value.
   Exit code 0/1. Also `--diag` prints monitor diagnostics only.
   Note: exercising Desktop Duplication requires an interactive desktop session (fails
   with E_ACCESSDENIED from service-like contexts).

Non-goals for v1: upload/share server, video capture, scrolling capture, window snapping,
HDR-true (FP16 swapchain) overlay preview, HDR clipboard, AVIF/JXL encoding, pinned
screenshots, cross-monitor selections. Keep seams for them.

## Stack

- **C# / .NET 8**, TFM **`net8.0-windows10.0.22621.0`** (needed for newer WGC APIs like
  `IsBorderRequired`; still runs on older Win10/11 — guard newer APIs with
  `ApiInformation.IsPropertyPresent`). WPF (`UseWPF`) + WinForms only for `NotifyIcon`
  (`UseWindowsForms`).
- **Vortice.Windows** (Vortice.Direct3D11, Vortice.DXGI; Vortice.WIC if the JXR fallback
  is needed) — maintained, .NET 8-compatible (verified).
- Windows.Graphics.Capture types from the TFM's built-in WinRT projection (CsWinRT).
- WPF imaging for encode: `PngBitmapEncoder`; `WmpBitmapEncoder` (JXR) **gated on an
  acceptance test** — encode a buffer containing 3.0, decode, assert > 1.0 survives; if
  WPF flattens to 8-bit, use WIC directly via Vortice.WIC (128bppRGBAFloat scRGB).
- P/Invoke: `RegisterHotKey`, `QueryDisplayConfig`/`DisplayConfigGetDeviceInfo`,
  `SetWindowPos`, clipboard, `GetDpiForMonitor`.
- Publish: `dotnet publish -c Release -r win-x64 --self-contained -p:PublishSingleFile=true
  -p:IncludeNativeLibrariesForSelfExtract=true` (the extract flag is required for WPF
  single-file).

## Architecture

```
roesnip/
  RoeSnip.sln
  src/RoeSnip/
    RoeSnip.csproj
    Program.cs               # entry: single-instance mutex, CLI parse (--capture/--diag), else tray
    App/
      TrayApp.cs             # NotifyIcon, context menu (Capture, Settings, About, Exit)
      HotkeyManager.cs       # RegisterHotKey on a message-only window; PrtScr registry handling
      Settings.cs            # JSON load/save, defaults, fail-closed on unreadable file
      SettingsWindow.xaml    # minimal WPF settings UI
      StartupManager.cs      # HKCU\...\Run
    Capture/
      MonitorInfo.cs         # enumerate outputs: bounds, DPI, advanced color?, SDR white nits, max nits
      IScreenCapturer.cs     # per-monitor single-frame grab -> CapturedFrame
      DesktopDuplicationCapturer.cs   # primary path (borderless by nature)
      WgcCapturer.cs         # fallback path (may show yellow border; acceptable)
      CaptureService.cs      # orchestrates: freeze all monitors, pick capturer, fallback logic
      CapturedFrame.cs       # owns pixels: FP16 scRGB or BGRA8 sRGB + metadata (monitor, format)
    Color/
      ColorMath.cs           # sRGB EOTF/inverse, Rec.709 luminance, scRGB<->nits
      ToneMapper.cs          # scRGB FP16 -> BGRA8 sRGB (the crown jewel; see below)
      Dither.cs              # ordered dither, applied ONLY to shoulder-mapped pixels
    Imaging/
      SdrImage.cs            # BGRA8 buffer + W/H, crop
      PngWriter.cs
      JxrWriter.cs           # FP16 crop -> .jxr (WPF encoder or WIC fallback)
      ClipboardService.cs    # PNG + CF_DIBV5
    Overlay/
      OverlayController.cs   # one OverlayWindow per monitor; focus & keyboard broadcast; result
      OverlayWindow.xaml/.cs # frozen SDR preview, dimming, selection, keyboard
      SelectionAdorner.cs    # handles, size badge
      Magnifier.cs           # zoom loupe + RGB/hex/nits readout (reads FP16 source!)
      AnnotationLayer.cs     # shapes model + rendering + hit-test + undo stack
      ToolbarControl.xaml    # tools, colors, copy/save/save-HDR
  tests/RoeSnip.Tests/
    ColorMathTests.cs        # golden values (see Test plan)
    ToneMapperTests.cs
    JxrRoundTripTests.cs     # HDR float survival through the encoder
    SettingsTests.cs
  README.md  DESIGN.md  PLAN.md
```

### Capture path

On trigger: capture **all monitors first**, then show overlays (freeze semantics).

Per monitor:

1. Enumerate via DXGI (`IDXGIFactory1` → adapters → `IDXGIOutput6`).
   - **Create the D3D11 device on the adapter that owns the output** (hybrid-GPU laptops);
     never assume the default adapter.
   - Advanced color detection: `GetDesc1().ColorSpace ==
     DXGI_COLOR_SPACE_RGB_FULL_G2084_NONE_P2020` indicates HDR — but do NOT branch the
     pipeline on this alone (Win11 ACM composes SDR displays in FP16 too). The pipeline
     branches on the **actual duplication format** (step 2).
   - `MaxLuminance` from `GetDesc1()`; if 0 or absurd, default 1000 nits.
   - SDR white level: `DisplayConfigGetDeviceInfo` with
     `DISPLAYCONFIG_DEVICE_INFO_GET_SDR_WHITE_LEVEL` (enum 11, struct `{header, ULONG
     SDRWhiteLevel}`); `nits = SDRWhiteLevel / 1000.0 * 80.0` (verified). Match paths to
     DXGI outputs by GDI device name (`QueryDisplayConfig` with `QDC_ONLY_ACTIVE_PATHS` +
     `DISPLAYCONFIG_DEVICE_INFO_GET_SOURCE_NAME` vs `DXGI_OUTPUT_DESC.DeviceName`).
     Query failure → default 240 nits, log, never crash.
2. **Primary: Desktop Duplication.** `IDXGIOutput5::DuplicateOutput1` passing BOTH
   `R16G16B16A16_FLOAT` and `B8G8R8A8_UNORM` as supported formats; read the actual
   delivered format from `DXGI_OUTDUPL_DESC.ModeDesc.Format` and branch on that
   (FP16 → scRGB tone-map path, BGRA8 → passthrough). FP16 frames are linear scRGB with
   1.0 = 80 nits (verified via MS docs/Q&A). `AcquireNextFrame` in a short retry loop
   (up to 5 × 100 ms; first acquire normally returns the current desktop immediately —
   the loop handles `DXGI_ERROR_WAIT_TIMEOUT` on static screens). Copy to staging texture
   **before** `ReleaseFrame`, `Map`, copy rows respecting `RowPitch`.
   `DXGI_ERROR_ACCESS_LOST` (mode/HDR toggle mid-grab) → re-enumerate and retry once,
   then fall back to WGC.
3. **Fallback: Windows.Graphics.Capture** (`GraphicsCaptureItem` from HMONITOR via
   `IGraphicsCaptureItemInterop`). MUST use **`Direct3D11CaptureFramePool.CreateFreeThreaded`**
   (plain `Create` needs a DispatcherQueue and throws from CLI/console contexts) with an
   event-based single-frame wait. Pixel format FP16 or BGRA8 to match the display.
   `IsCursorCaptureEnabled = false`. `IsBorderRequired = false` is best-effort only —
   guard with `ApiInformation.IsPropertyPresent` + try/catch; unpackaged apps lack the
   `graphicsCaptureWithoutBorder` capability, so the **yellow border may appear on the
   fallback path and that is accepted** (DD is the primary and is borderless). Covers RDP
   and other DD-denied contexts.
4. `CapturedFrame` holds the raw pixels + `MonitorInfo`. FP16 frames stay in memory for
   the session (magnifier nits readout + JXR export read from it).

### Tone-map pipeline (`ToneMapper`) — the whole point of the app

Input: FP16 scRGB (linear, 1.0 = 80 nits). Output: BGRA8 sRGB. **Adaptive per capture**
(computed once per frame, not per crop, so preview == export):

1. `scale = 80.0 / sdrWhiteNits`; per pixel `c_lin = c_scRGB * scale`. Now SDR white = 1.0.
2. Clamp negatives (out-of-gamut) to 0 per channel (v1 gamut handling: clip; documented
   seam for real gamut mapping later).
3. Scan the frame for `M = max over pixels of max(r,g,b)`.
   - **`M ≤ 1.0 + ε` (pure SDR content — the common case): exact pass-through.** No
     shoulder, no dither; straight inverse-sRGB + round-to-nearest. This guarantees
     screenshots of normal apps are bit-identical to an SDR-monitor screenshot.
   - **`M > 1.0 + ε` (HDR highlights present):** hue-preserving soft knee. Let
     `knee = 0.90`, `peak = clamp(min(M, maxLuminance / sdrWhiteNits), 2.0, ∞)`.
     For each pixel with `m = max(r,g,b)`:
     - `m ≤ knee`: pass through.
     - `m > knee`: clamp `m` to `peak`, then map through a **C1-continuous Hermite
       shoulder** `[knee, peak] → [knee, 1.0]` (slope 1 at the knee, slope 0 at the peak);
       scale all three channels by `f(m)/m` (hue-preserving). Do NOT call this BT.2390 —
       that EETF is PQ-domain; this is a Hermite soft-knee in normalized linear domain.
     - Settings may override knee/peak (advanced).
4. Clamp to [0,1], apply inverse sRGB EOTF (proper piecewise sRGB, not pow(1/2.2)).
5. Quantize to 8-bit: round-to-nearest for pass-through pixels; small ordered dither
   (±0.5 LSB) **only for shoulder-mapped pixels** — dithering everything would break the
   bit-identical-SDR promise.

BGRA8-source frames bypass the tone-mapper entirely.

Performance: C# `Parallel.For` over rows; 4K ≈ 8.3 MP; target < 150 ms. `System.Half` →
float. Keep the inner loop in one method (vectorizable later; no SIMD in v1).

The overlay preview shows **exactly the tone-mapped SDR image** — WYSIWYG with the saved
PNG. (HDR-true preview via FP16 swapchain is an explicit v2 seam in `OverlayWindow`.)

### Overlay & multi-monitor

- One WPF window per monitor, borderless, topmost, `ShowInTaskbar=false`. Per-Monitor V2
  DPI awareness (app manifest). **Known-good mixed-DPI pattern (required):**
  `WindowStartupLocation=Manual`; position/size via Win32 `SetWindowPos` in physical
  pixels during `SourceInitialized` (before first render, avoiding the WM_DPICHANGED
  size-bounce); preview `Image` with `Stretch=Fill` filling the window; **all selection,
  magnifier, and annotation-export math in physical pixels** — WPF DIPs never leave the
  view layer.
- Selection lives on one monitor (cross-monitor is a v2 seam); starting a drag on monitor
  A clears any selection on B.
- Keyboard: only one window has focus — `OverlayController` broadcasts key commands to all
  overlays; mouse-enter on another overlay `Activate()`s it. Esc/Enter/Ctrl+C/Ctrl+S work
  from any monitor. All overlays close together.
- Annotations are vector shapes over the preview; export rasterizes at 1:1 physical pixel
  scale (`DrawingVisual` + `RenderTargetBitmap` at 96 DPI so pixels map 1:1).

### Failure modes to handle

- No HDR monitors at all → pure SDR path; must be tested explicitly.
- Mixed HDR + SDR monitors; ACM-on-SDR (FP16 duplication of an "SDR" display — handled by
  branching on delivered format, with SDR white level applied the same way).
- HDR toggled / mode switch mid-capture → `DXGI_ERROR_ACCESS_LOST` → re-enumerate, retry
  once, then WGC.
- SDR white level query fails → default 240 nits, log.
- Hotkey unavailable (OneDrive/Snipping Tool own PrtScr) → balloon explaining, fall back
  to Ctrl+PrintScreen, reflect in settings UI.
- Settings file unreadable → defaults in memory, do NOT overwrite the file (fail closed).
- Second instance → signal first (named pipe or WM_COPYDATA) to open capture, exit.

## Test plan

- **Unit (must exist, must pass):**
  - Golden tone-map values, e.g. scRGB 3.0 @ SDR white 240 → byte 255; scRGB 1.5 @ 240 →
    linear 0.5 → byte 188; 0 → 0; negatives → 0.
  - SDR-parity: a frame with all values ≤ 1.0 after normalization maps EXACTLY to
    round(inverse-sRGB) — no dither, no shoulder.
  - Shoulder continuity: values just below/above the knee produce adjacent outputs (C1 at
    the knee); `f(peak) == 1.0`; `m > peak` clamps.
  - sRGB transfer golden pairs (0.0031308 boundary, 0.5, 1.0).
  - BGRA8 passthrough path leaves bytes untouched.
  - JXR round-trip: encode FP16 buffer containing 3.0 → decode → assert > 1.0 survives.
- **Integration:** `--diag` and `--capture` on the dev machine (real monitors, interactive
  session); verify PNG opens, dimensions match, stdout diagnostics sane.
- **Manual/user:** overlay interaction + real HDR visual check (user has the HDR monitor).

## Milestones

- **M1 — skeleton + capture core:** csproj/sln, Program/CLI, MonitorInfo, DD capturer,
  ToneMapper + ColorMath + tests, PngWriter, `--capture`/`--diag` working end-to-end.
- **M2 — overlay UX:** overlay windows, selection, magnifier w/ nits, toolbar, copy/save,
  annotations (text tool last, cuttable), WGC fallback.
- **M3 — app shell:** tray, hotkey (incl. PrtScr registry consent flow), settings + window,
  startup, JXR export, single-instance, shutter cue + save balloon, publish profile, README.
