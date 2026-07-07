# TESTING.md — Phase B cross-platform port verification record

Living, dated record of what has actually been verified per OS, how, and on what hardware —
per PLAN-XPLAT.md §4 step 7 and DESIGN-XPLAT.md "Verification reality". "Compiles" and "ran on
real hardware" are different claims; this file never conflates them.

Machine used throughout: Windows 11 Pro, 3 monitors (`\\.\DISPLAY1` 2560x1440, `\\.\DISPLAY2`
1440x2560 portrait, `\\.\DISPLAY3` 2560x1440 primary), all advanced-color (HDR) active,
SDR white 240 nits, max luminance 400 nits, all at 96 DPI (scale 1.0). Integration pass:
2026-07-07.

## Build/test matrix (integration, 2026-07-07)

- `dotnet build RoeSnip.sln -c Debug -t:Rebuild` — 0 warnings, 0 errors. All 9 projects,
  both RoeSnip.App TFMs (`net8.0` + `net8.0-windows10.0.22621.0`). This no-RID build compiles
  Platform.Windows, Platform.Linux, and Platform.MacOS simultaneously (PLAN-XPLAT §1.7's
  design-time compile gate).
- `dotnet test RoeSnip.sln` — all green: RoeSnip.Tests (frozen WPF) 68/68,
  RoeSnip.Core.Tests 70/70, RoeSnip.Platform.Windows.Tests 5/5 (includes the JXR round-trip
  acceptance gate and the backend-registry selection check: `CreateForCurrentPlatform()`
  returns `WindowsCaptureBackend` specifically on this machine, PLAN-XPLAT §4 risk item).
- Publish gates, all clean (note: `-f` is required — RoeSnip.App multi-targets, plain
  `dotnet publish -r <rid>` fails NETSDK1129):
  - `dotnet publish src/RoeSnip.App -c Release -f net8.0-windows10.0.22621.0 -r win-x64 --self-contained`
  - `dotnet publish src/RoeSnip.App -c Release -f net8.0 -r linux-x64 --self-contained`
  - `dotnet publish src/RoeSnip.App -c Release -f net8.0 -r osx-arm64 --self-contained`
  - `dotnet publish src/RoeSnip.App -c Release -f net8.0 -r osx-x64 --self-contained`
  - RID isolation verified on every output: each publish contains only its own
    `RoeSnip.Platform.*` assembly; win/osx outputs contain no `Tmds.DBus.dll`.

## Windows

### Verified 2026-07-07 (headless, this machine)

- `RoeSnip.exe --diag` (Avalonia app): backend line
  `Windows (Desktop Duplication/WGC)` + all 3 real monitors with sane HDR metadata, exit 0.
  Output is line-for-line identical to the frozen WPF app's `--diag` on the same machine
  (direct A/B run, both exes).
- `RoeSnip.exe --capture --out t.png`: wrote `t_monitor{0,1,2}.png` (multi-monitor `--out`
  suffix behavior, matching WPF), all Fp16ScRgb, all verified non-black (sampled luma avg
  27–49, max up to 255) with correct per-monitor dimensions. The DD black-frame quirk fired
  live on DISPLAY1 during WP-X2 and fell back to WGC cleanly; the persisted memo in
  `%APPDATA%\RoeSnip.App\capture-cache.json` now skips DD there (P5 audit fix: RoeSnip.App uses
  its own config directory, distinct from the frozen WPF app's `%APPDATA%\RoeSnip`, so the two
  resident apps can never fire each other's hotkey handler off one settings.json).
- `--capture --monitor 0 --out x.jxr --jxr`: real JXR written (WP-X2 pass), proving the
  `AppComposition.WriteHdrExport` wiring to Platform.Windows's JxrWriter.
- CLI output caveat: the app is WinExe + AttachConsole. `dotnet run` output cannot be piped
  or redirected (prints to the invoking interactive console only); invoke the built exe
  directly (or via `Start-Process -RedirectStandardOutput`) when capturing output in scripts/CI.
  `dotnet run` also requires `-f net8.0-windows10.0.22621.0`.

### NOT verified — needs an interactive session (prioritized)

1. Overlay parity A/B against the frozen WPF app (highest value — both apps run side by side
   on this machine): drag-select + 4px click-vs-drag threshold, two-stage Esc, click color
   inspector (hex auto-copy, >250-nits amber), 8-handle resize, magnifier nits readout,
   all six annotation tools + undo, Enter/double-click confirm, Ctrl+C paste into Paint AND
   a browser (PNG + CF_DIBV5), Ctrl+S (cancel keeps overlay open), toolbar X closes outright.
2. Mixed content on the real 3-monitor setup: mouse-enter activation, selection exclusivity
   on one monitor, keyboard reaching the overlay from any monitor, all windows closing
   together. MonitorInfo↔Avalonia-Screen correlation is by exact bounds equality — exercised
   here but only at uniform scale 1.0.
3. Mixed-DPI (scale ≠ 1.0) overlay math — this machine is all-96-DPI, so the position/size
   math (physical PixelPoint before Show(), size = Bounds/Scaling, never reposition post-show)
   was REVIEWED against the WPF version, never exercised. Needs a mixed-DPI setup.
4. Tray mode: icon + menu, hotkey → overlay, saved-balloon toast (custom toast window —
   Avalonia has no balloon API), PrintScreen/Snipping-Tool consent dialog (Avalonia Yes/No;
   dismissing without answering persists nothing and re-asks).
5. Resident-instance signalling: `RoeSnip capture` while an instance is resident must signal
   it (no second tray icon/hotkey); needs a live resident process. Note: the hotkey is a
   SharpHook low-level hook, NOT RegisterHotKey — the keystroke is observed, not consumed,
   so with the Snipping Tool PrtScr intercept ON both would trigger (this is exactly what
   the ported consent flow mitigates).
6. RenderTargetBitmap channel order: verify saved/copied PNGs are not R/B-swapped and
   annotations land pixel-exact at selection edges (code branches on `rtb.Format`).
7. Published single-file exe repeating the above standalone.

## Linux

### Verified 2026-07-07 (on Windows — compile/publish only)

- Platform.Linux compiles in the solution build; linux-x64 self-contained publish is clean and
  contains `RoeSnip.Platform.Linux.dll`, `Tmds.DBus.dll`, `libSkiaSharp.so` (via Avalonia.Skia),
  and no Windows/macOS platform assemblies.
- Zero Linux runtime verification has happened anywhere yet.

### NOT verified — needs WSLg first, then a real distro

WSLg smoke (copy the linux-x64 publish into WSL, check `echo $XDG_SESSION_TYPE` first):

- `./RoeSnip --diag` — exercises RandR monitor enumeration end-to-end (struct marshalling of
  XRRScreenResources/XRROutputInfo/XRRCrtcInfo was hand-checked but never executed), output
  names, bounds, primary detection.
- `./RoeSnip --capture` — portal is usually absent in WSLg: expect one logged portal failure, then
  X11 fallback attempted per capture (no permanent memo of the portal as broken unless X11 has
  ALSO genuinely succeeded at least once — see the next point). Verify XGetImage returns real
  pixels: WSLg's `XDG_SESSION_TYPE`/`WAYLAND_DISPLAY` mark the session as Wayland, so as of the P3
  audit fix the X11 capturer now REFUSES to run there by default (throws immediately, both
  monitors omitted) rather than risking the known XWayland hazard of "successful" all-black
  frames; set `ROESNIP_FORCE_X11=1` to force it anyway for this smoke test specifically. Also
  verify a real all-zero XGetImage result (forced path) is now caught by `FrameSanity.IsAllZero`
  and thrown as a `CaptureException`, mirroring `DesktopDuplicationCapturer`'s black-frame check.
- Known WSLg-unreachable: real portal behavior (prompt, `uri` result, temp-file deletion),
  HiDPI discovered-scale slicing (the scale≠1 branch has never executed anywhere),
  Tmds.DBus `Response` signal deserialization against a real portal.

Real GNOME/KDE box checklist:

- Portal permission dialog: GNOME prompts even for non-interactive shots (one-time grant on
  new portals, per-shot on old); KDE historically skips it. Documented behavior, not a bug.
- **Known UX edge (plan-mandated cache interacting with the portal):** a single user DENIAL of
  the portal dialog throws CaptureException → the portal capturer is memoized broken forever
  for that monitor (persisted `capture-cache.json` under `~/.config/roesnip-app/`); deleting the
  cache file is the only retry path today.
- **Wayland activation:** SharpHook/libuiohook is X11-only. On `XDG_SESSION_TYPE=wayland` the
  hotkey hook is never started (by design). The PRIMARY activation path is a DE keyboard
  shortcut bound to the CLI verb: GNOME Settings → Keyboard → Custom Shortcuts → command
  `/path/to/RoeSnip capture` (KDE: System Settings → Shortcuts). First invocation becomes the
  resident instance; subsequent ones signal it over the single-instance pipe (UDS).
- Pure-Wayland-without-XWayland limitation: monitor enumeration is RandR-only, so no XWayland
  ⇒ empty monitor list ⇒ no capture even if the portal works. Plan-accepted v1 limitation.
- Tray icon is strictly optional (StatusNotifier may need a GNOME extension); its absence is
  logged and must not break CLI operability.
- Clipboard: Avalonia 12 DataTransfer `image/png` path is compile-only so far; verify paste
  into GIMP/Firefox (X11 INCR fix is in Avalonia 12.0.0).

## macOS

### Verified 2026-07-07 (on Windows — compile/publish only)

- Platform.MacOS compiles; osx-arm64 and osx-x64 publishes clean, contain
  `RoeSnip.Platform.MacOS.dll` and no Windows/Linux/Tmds.DBus assemblies.
- `helpers/scksnap` Swift source + `.github/workflows/build-scksnap.yml` exist; the workflow
  YAML parses. The Swift code has NEVER been compiled (no macOS host here) — the workflow's
  swiftc steps are the compile gate; its `./scksnap list` step is the only runtime smoke
  (real WindowServer on the runner). Trigger by pushing, check `gh run list --workflow
  build-scksnap` / `gh run view`.

Status label: **built, not hardware-validated.** Do not claim more.

### NOT verified — needs a real Mac

- `scksnap capture` end-to-end: wire-format (96-byte LE header + raw rows) round-trip into
  `CapturedFrame`; FP16 extended-linear-sRGB EDR values; the HDR path specifically needs
  macOS 15 + Apple Silicon + an EDR display; SDR path on macOS 14/Intel;
  `CGDisplayCreateImage` "pre-14" path. P9 audit correction: the helper's REAL floor is macOS 13
  (`swiftc -target ...-macos13.0`, helpers/scksnap/README.md's Build section) — the binary cannot
  run at all below that, so "pre-14" in practice only ever means macOS 13.x, not "any older
  macOS" as the phrase might otherwise suggest.
- **TCC (Screen Recording) flow:** first capture must trigger the system prompt
  (`CGRequestScreenCaptureAccess`); grant lives under System Settings → Privacy & Security →
  Screen & System Audio Recording, attributed to the helper's stable ad-hoc identifier
  `net.roelite.roesnip.scksnap`. Denial = helper exit code 82 →
  `ScreenRecordingPermissionDeniedException` from `MacCaptureBackend.CaptureAll` (deliberate
  deviation from §2.3 "never throw" — TCC-denied is first-class per DESIGN-XPLAT; the app
  shell should catch it and show System-Settings guidance — currently surfaced as an error).
  To re-test the prompt: `tccutil reset ScreenCapture` (or remove the entry manually).
- **Signing caveat:** ad-hoc signatures have a per-build designated requirement — a TCC grant
  is NOT guaranteed to survive helper rebuilds; the stable identifier only keeps the System
  Settings entry coherent. A Developer ID signature is the real fix before distribution.
- Deployment: extract CI's `scksnap.tar.gz` (tar preserves the exec bit/signature) next to the
  published RoeSnip executable (flat sibling; `ROESNIP_SCKSNAP_PATH` env var overrides).
  Not automated yet.
- NSPasteboard clipboard path (objc_msgSend), SharpHook's Accessibility-permission prompt,
  `SdrWhiteNits=240` placeholder sanity (no OS-reported absolute value exists — PLAN-XPLAT
  §6 flag 3, open product question), mixed-scale multi-monitor global-bounds approximation.

## Known cross-cutting notes

- SkiaSharp 3.119+ needs DX12 on Windows; irrelevant for this app's HDR-capable audience but
  a possible launch-failure cause on very old GPUs.
- `RoeSnip.App` on Windows uses distinct single-instance names (`Global\RoeSnip.App-SingleInstance`,
  pipe `RoeSnip.App-SingleInstance`) and HKCU Run value (`RoeSnip.App`) so it can run side by
  side with the frozen WPF app without cross-signalling. Per the P5 audit fix, `RoeSnip.App` also
  now uses its own config directory (`%APPDATA%\RoeSnip.App` / `~/Library/Application Support/
  RoeSnip.App` / `~/.config/roesnip-app`) rather than sharing the WPF app's `%APPDATA%\RoeSnip` —
  the two apps no longer share settings.json OR capture-cache.json at all. Sharing one directory
  meant both apps loaded the same HotkeyVirtualKey and each armed their own global-hotkey
  mechanism (SharpHook here, RegisterHotKey in the WPF app) for the identical key, so one
  PrintScreen press fired both — two overlay stacks plus concurrent capture-cache rewrites.
  Convergence to one shared config directory is a later cleanup, once the WPF app retires.
- Run-at-startup is Windows-only (HKCU Run); a logged no-op on macOS/Linux (PLAN-XPLAT §6
  flag 5).
