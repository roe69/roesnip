# RoeSnip cross-platform architecture (DESIGN-XPLAT)

*(v2 — incorporates adversarial review; platform claims verified against vendor docs.)*

## Strategy

One new **Avalonia 12** app (`RoeSnip.App`) targeting win-x64, osx-arm64/osx-x64, linux-x64,
sharing a portable core with per-OS capture backends. The existing WPF app (`src/RoeSnip`)
stays frozen and shipping as the polished Windows deliverable this cycle; the Avalonia app is
the cross-platform build (on Windows it doubles as the testbed proving the shared code).
Convergence to a single app is a later cleanup. The WPF app keeps its own copies of the
color/capture code this cycle (frozen + verified beats churn); Core is an extraction, not a
refactor of the WPF app.

## Solution layout (new projects; existing src/RoeSnip untouched)

```
src/RoeSnip.Core/              net8.0, zero UI deps
  Color/    ColorMath, ToneMapper, Dither          (ported, namespace RoeSnip.Core.*)
  Capture/  MonitorInfo, CapturedFrame, FrameFormat, IScreenCapturer,
            ICaptureBackend (new: monitor enumeration + capture-all), CaptureService
  Imaging/  SdrImage (no WPF), PngWriter via SkiaSharp (pin the version Avalonia 12 ships)
  Settings/ RoeSnipSettings + SettingsStore (portable config dirs:
            Windows %APPDATA%\RoeSnip, macOS ~/Library/Application Support/RoeSnip,
            Linux $XDG_CONFIG_HOME/roesnip or ~/.config/roesnip)
src/RoeSnip.Platform.Windows/  net8.0-windows10.0.22621.0
  Existing DD + WGC capturers, monitor enum w/ SDR white level, WIC JxrWriter — ported to
  Core contracts (mechanical namespace/type mapping; behavior identical to the WPF app).
src/RoeSnip.Platform.MacOS/    net8.0 — thin .NET side only
  Capture is delegated to a SMALL SWIFT HELPER BINARY ("scksnap"): SCK is async-only
  (Obj-C blocks + dispatch queues) and not sanely drivable via portable P/Invoke (Microsoft's
  own bindings hang on GetShareableContentAsync; net8.0-macos workload can't build on this
  Windows machine). Helper: enumerate displays; capture full display; write FP16 pixels +
  metadata (w/h/stride/EDR headroom/display bounds/scale) to a temp file; .NET side shells
  out, parses, crops in Core. Matrix: macOS 15+ on Apple Silicon → HDR screenshot presets
  (SCStreamConfiguration captureDynamicRange hdrLocalDisplay); macOS 14 / Intel → SDR
  (SCScreenshotManager plain, or CGDisplayCreateImage pre-14). Do NOT use captureImageInRect
  (15.2+ only) — capture full display, crop in Core. Helper is ad-hoc signed with a stable
  identifier (TCC Screen Recording attribution); TCC-denied exit code is a first-class,
  UI-surfaced error. Helper source lives in-repo (helpers/scksnap/), built by GitHub Actions
  macOS runner (jxa-helper precedent) — NOT buildable locally on Windows.
src/RoeSnip.Platform.Linux/    net8.0
  Primary: xdg-desktop-portal org.freedesktop.portal.Screenshot via Tmds.DBus — correct on
  Wayland and X11-with-portals; returns one full-virtual-desktop PNG URI (SDR); decode via
  SkiaSharp; slice per monitor from UI Screens geometry, VERIFYING pixel scale at runtime by
  comparing PNG dimensions to summed screen bounds (HiDPI portals return physical px).
  GNOME may show a permission dialog (one-time grant on new portals, per-shot on old ones) —
  documented, not fought. Fallback (real, tested path): raw X11 XGetImage via libX11
  P/Invoke for portal-less X sessions. HDR on Linux: not attempted in v1 (compositor HDR
  experimental) — frames are Bgra8Srgb → pass-through.
src/RoeSnip.App/               Avalonia 12 cross-platform UI
  Overlay windows: one per screen, SystemDecorations=None, Topmost; set Position (physical
  PixelPoint from Screen.Bounds) BEFORE Show(), size from Screen.Bounds / DesktopScaling,
  never move a shown window across monitors (known Avalonia mixed-DPI resize bugs #13917 /
  #17834 — same discipline as the WPF app; this machine is all-96-DPI so the math must be
  REVIEWED, not just run). Selection + handles + size badge, magnifier w/ nits readout,
  click color inspector, pictogram toolbar, annotations — same UX spec as the Phase A WPF
  fixes. Clipboard adapters: Windows reuse P/Invoke PNG+DIBV5; macOS NSPasteboard PNG via
  helper or objc; Linux Avalonia clipboard image/png (Avalonia 12 fixed X11 INCR). Tray via
  Avalonia TrayIcon but STRICTLY optional (Linux StatusNotifier needs a GNOME extension):
  the app is fully operable via CLI verbs (`RoeSnip capture`, `RoeSnip settings`) that
  signal the running instance over the single-instance pipe (Unix domain socket on
  mac/linux). Global hotkey via SharpHook (pin current version) — X11 and Windows/macOS
  only; on XDG_SESSION_TYPE=wayland do NOT start the hook (libuiohook is X11-only and one
  IGlobalHook per process); the documented PRIMARY Linux activation is a DE keyboard
  shortcut bound to `RoeSnip capture`. macOS hook needs Accessibility permission — surfaced
  in UI, not assumed.
tests/RoeSnip.Core.Tests/      ports of ColorMath/ToneMapper/Settings golden tests
helpers/scksnap/               Swift helper source + GitHub Actions workflow (macOS runner)
```

## Key semantics decisions

- **Buffers stay RAW; metadata carries the convention.** `CapturedFrame` gains
  `double SdrWhiteInBufferUnits` (Windows scRGB: sdrWhiteNits/80; macOS EDR: 1.0; SDR
  frames: n/a). ToneMapper's existing scale step generalizes to divide by this value; the
  nits readout converts via `value / SdrWhiteInBufferUnits * sdrWhiteNits`. JXR export keeps
  writing untouched original values. Existing golden tests preserved via the metadata.
- Save HDR is Windows-only v1 (backend capability flag hides the button elsewhere).
- CLI `--diag`/`--capture` preserved in RoeSnip.App on all OSes (the only headless smoke on
  mac/linux; also the CI hook later).

## Verification reality

- Windows: full runtime verification of RoeSnip.App (same interactive automation rig).
- Core: unit tests everywhere.
- Linux: X11 path runtime-tested in WSLg on this machine if available (Avalonia X11 +
  XGetImage + portal-if-present); else compile + publish gate.
- macOS: helper scaffold + Actions workflow compile only — shipped explicitly as "built,
  not hardware-validated", with TESTING.md checklists per OS.

## Milestones

- **X1**: solution restructure + Core extraction + Core tests green.
- **X2**: Avalonia app on Windows at overlay feature parity minus JXR button (uses
  Platform.Windows backend); interactive verification passes on this machine.
- **X3a**: Linux backend (portal + X11 fallback) + linux-x64 publish; WSLg runtime smoke if
  available. Not blocked by X3b.
- **X3b**: macOS Swift helper + .NET shell integration + osx publishes + GitHub Actions
  build; explicitly "needs hardware validation".
