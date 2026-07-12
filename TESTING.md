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

## Driving RoeSnip programmatically (agents/E2E)

Applies to the frozen WPF app (`src/RoeSnip`), NOT `RoeSnip.App` above. Automated agents/E2E
harnesses used to drive this app with synthetic mouse input and UIA — slow, and every drag/click
timing-dependent. The dev-gated automation channel (`src/RoeSnip/App/AutomationServer.cs`)
replaces that with a deterministic, line-based JSON control pipe that calls straight into the same
production code a real click/drag runs (button `Click` events, `RegionOutline`'s own drag-finish
bookkeeping, `OverlayWindow`'s own `SetSelection`/`OnCommand`) — never a re-implementation, and
never simulated input.

**Zero behavior change when off.** With neither the flag nor the env var below set, this channel
does not exist: no pipe, no listener thread, nothing. It must never be enabled for a normal user
install.

### Starting the resident with automation enabled

```
set ROESNIP_AUTOMATION=1
RoeSnip.exe
```

or equivalently:

```
RoeSnip.exe --automation
```

Either starts the normal tray app (icon, hotkey, single-instance takeover — all unchanged) plus a
second named pipe, `RoeSnip-Automation`. A launch without the flag/env var behaves exactly as
before this feature existed.

### The `--auto` CLI client

`RoeSnip.exe --auto '<json>'` connects to the running resident's automation pipe, sends one JSON
request line, prints the response line to STDOUT, and exits 0 (response `"ok":true`) or 1
(`"ok":false`, or any failure to connect). It never touches the single-instance mutex/pipe — it
can never trigger the "normal launch replaces the running instance" takeover a bare `RoeSnip.exe`
does, so it is always safe to run alongside a live resident.

Zero-arg commands (`state`, `trigger`, `escape`) accept a shorthand — the bare command name instead
of full JSON:

```
RoeSnip.exe --auto state
RoeSnip.exe --auto '{"cmd":"state"}'      # equivalent
```

If the resident isn't running with automation enabled, the client prints a clear error ("resident
not started with ROESNIP_AUTOMATION=1 (or --automation)") and exits 1.

### Commands

Every response is one JSON line: `{"ok":true, ...}` or `{"ok":false,"error":"..."}`. All
coordinates are **virtual-desktop physical pixels** (same convention as `Capture.RectPhysical`
everywhere else in this codebase) — not DIPs, not monitor-relative, unless noted. Every command
except `screenshot` responds with the full `state` shape (below) so a caller never has to issue a
separate `state` call just to see what its own command did.

#### `state`

Current mode and everything else a caller needs to decide what to do next.

```
--auto state
```
```json
{"ok":true,"mode":"idle","selection":null,"recordingFormat":null,"preset":null,"estimateText":null,
 "fps":null,"fpsRange":null,
 "monitors":[{"deviceName":"\\.\\DISPLAY1","left":0,"top":0,"right":2560,"bottom":1440,"isPrimary":true}]}
```

`mode` is one of `idle | overlay | setup | capturing | reviewing` — `overlay` is a capture overlay
with no recording session yet (selection may or may not exist); `setup`/`capturing`/`reviewing`
are `RecordingSession`'s own three phases once Record was chosen. `selection` is `{x,y,w,h}` or
`null`. `recordingFormat`/`preset`/`estimateText`/`fps`/`fpsRange` are only non-null in
`setup`/`capturing`/`reviewing` — `estimateText` is the recording chrome's live size-estimate
readout, verbatim (e.g. `"~700 KB/s * 41 MB/min (varies with motion)"`), `preset` mirrors whichever
size chip is currently checked (`max`/`quality`/`balanced`/`compact`/`minimal` — display labels in
the chrome itself read Max/High/Medium/Low/Min, but the wire protocol keeps the original tier
names), `fps` mirrors the FPS slider's current value, and `fpsRange` is
`{"min":M,"max":N}` — the CURRENT recording format's own slider bounds (`{"min":5,"max":50}` for
GIF, `{"min":5,"max":60}` for MP4 — quality/fps expansion workstream: fps is a free integer slider
now, not four fixed chips, so `fpsRange` replaces the old `allowedFps` array) — the exact range the
`fps` command below will accept right now.

#### `trigger`

Opens the capture overlay — the same action as `--signal-capture` / the hotkey / the tray icon's
Capture item. Blocks (up to ~5s) until the overlay is actually up before responding, so the
returned `state` reflects it rather than a stale `idle`.

```
--auto trigger
```
```json
{"ok":true,"mode":"overlay","selection":null, ...}
```

#### `select`

`{"cmd":"select","x":100,"y":80,"w":800,"h":600}`

In `overlay` mode: sets the selection on whichever monitor contains the rect's top-left corner,
through the same code path Ctrl+A (select-all) uses — selection becomes visible and the annotation
toolbar shows, exactly as a completed drag would leave it. In `setup` mode (a recording session's
Setup phase): applies the rect to `RegionOutline` through the exact band-drag code path, so
`RegionChanged` → `RecordingSession.OnRegionMoved` → the chrome's `UpdateSelection` all fire, same
as a real drag. Errors if idle, or if a recording is in `capturing`/`reviewing` (the region is
locked once a take exists).

```json
{"ok":true,"mode":"overlay","selection":{"x":100,"y":80,"w":800,"h":600}, ...}
```

#### `record`

`{"cmd":"record","format":"gif"}` (or `"mp4"`)

From `overlay` mode with a selection: invokes the same `OverlayCommand.RecordMp4`/`RecordGif` the
toolbar's Record menu choices raise. Closes the overlay and hands off to `RecordingController`,
landing in `setup`. Blocks (up to ~5s) for that hand-off before responding. Errors if there is no
active overlay session or no selection yet.

```json
{"ok":true,"mode":"setup","recordingFormat":"gif","preset":"quality","estimateText":"~..." , ...}
```

#### `preset`

`{"cmd":"preset","tier":"balanced"}` — one of `max`/`quality`/`balanced`/`compact`/`minimal`.

Only valid in `setup` (the size row is locked once a take exists, same as the real chrome). Clicks
the matching size chip (`RecordingChrome.InvokeSizePreset`, which raises the chip's real `Click`
event) and persists it, same as a mouse click. Errors with the current phase if not in `setup`.
`minimal` is the quality/fps expansion workstream's fifth, most aggressive tier (GIF: a 16-color
palette, a large lossy run-extension threshold, and a half-resolution render; MP4: the lowest
bitrate tier) — see GifSizePresets.ForPreset's own doc comment for the exact calibrated numbers.

#### `fps`

`{"cmd":"fps","value":20}` — any integer from 5 up to the CURRENT recording format's own ceiling
(GIF: 5-50; MP4: 5-60 — see `state`'s own `fpsRange` doc above). Quality/fps expansion workstream:
fps is a free integer slider now, not four fixed chips per format — GIF's ceiling is still a HARD
format limit (GIF89a's delay field is whole centiseconds and every real decoder clamps a 0-1cs
delay to 10cs, so 2cs/50fps is the fastest legal frame time), but every integer up to that ceiling
is legal now, not just divisors of 100: the encoder's patch-behind delay scheme
(`GifEncoder.PatchLastDelay`) carries the sub-centisecond rounding remainder forward frame to
frame, so a non-divisor rate (e.g. 37fps) still averages out to EXACTLY the requested speed over a
long take instead of silently drifting.

Only valid in `setup`, same as `preset` above. Sets the FPS slider to this value
(`RecordingChrome.InvokeFps`, which moves the same slider a real drag would and persists
immediately rather than waiting out the slider's drag-debounce window — see that method's own doc
comment) and persists it, same as a completed drag. A value outside the pure 5-60 union range is
rejected immediately (no live session needed to catch it); a value inside that union but outside
the CURRENT format's own narrower range (e.g. `55` while recording GIF, whose own ceiling is `50`)
is rejected once a session exists, with an error naming the current format's own range. Errors with
the current phase if not in `setup`.

```
--auto '{"cmd":"fps","value":20}'
```
```json
{"ok":true,"mode":"setup","recordingFormat":"gif","preset":"quality","fps":20,"fpsRange":{"min":5,"max":50}, ...}
```

#### `chrome`

`{"cmd":"chrome","action":"start"}` — one of `start`/`stop`/`save`/`cancel`/`pause`/`resume`.

Raises the same button `Click` event a real mouse click on that chrome button would — `start`/
`stop` share one button exactly like the real UI (`start` valid only in `setup`, `stop` only in
`capturing`). `pause` requires `capturing` and not already paused; `resume` requires already
paused (valid from `capturing` or `reviewing` — a soft-stopped take is a paused take, see
`RecordingSession.Resume`'s own doc comment); `save` requires `reviewing`; `cancel` is valid in any
phase. An action invalid for the current phase errors with that phase instead of silently no-op'ing
the way a disabled button would.

```
--auto '{"cmd":"chrome","action":"start"}'
```
```json
{"ok":true,"mode":"capturing","recordingFormat":"gif","preset":"quality", ...}
```

#### `escape`

Closes/cancels whatever is open (a recording session's Cancel, or the overlay's Cancel) and returns
to `idle`. A no-op (still `"ok":true`) if already idle.

```
--auto escape
```
```json
{"ok":true,"mode":"idle", ...}
```

#### `screenshot`

`{"cmd":"screenshot","path":"C:\\temp\\shot.png","rect":{"x":0,"y":0,"w":1920,"h":1080}}`

Captures the given rect (or the primary monitor, if `rect` is omitted) via
`System.Drawing.Graphics.CopyFromScreen`, from inside the RoeSnip process, and writes a PNG.
`RegionOutline` is NOT capture-excluded, so it appears in a plain screenshot like any other
on-screen window — useful for verifying the "this area will be recorded" frame is where it should
be. The overlay/chrome/flash windows ARE `WDA_EXCLUDEFROMCAPTURE` and so are invisible to a plain
screenshot; pass `"includeExcluded":true` to temporarily clear that affinity on every one of this
process's own currently-excluded windows, capture, then restore it. **Caveat:** the affinity
change and the capture are not atomic with the compositor — a window mid-repaint at the exact
capture instant could show a stale or partially composited frame for that one screenshot. Accepted
for an automation/E2E screenshot; do not rely on `includeExcluded` for pixel-perfect verification of
transient animation frames.

```json
{"ok":true,"path":"C:\\temp\\shot.png","width":1920,"height":1080}
```

Only the path/width/height are returned — never the image bytes (hard 64k response cap; one file
write per response).

### Worked example: select, record, read the estimate, resize, set preset and fps, screenshot, cancel

```
set ROESNIP_AUTOMATION=1
RoeSnip.exe &

RoeSnip.exe --auto trigger
:: {"ok":true,"mode":"overlay","selection":null,...}

RoeSnip.exe --auto "{\"cmd\":\"select\",\"x\":100,\"y\":100,\"w\":800,\"h\":600}"
:: {"ok":true,"mode":"overlay","selection":{"x":100,"y":100,"w":800,"h":600},...}

RoeSnip.exe --auto "{\"cmd\":\"record\",\"format\":\"gif\"}"
:: {"ok":true,"mode":"setup","recordingFormat":"gif","preset":"quality","fps":25,
::  "fpsRange":{"min":5,"max":50},
::  "estimateText":"~475.8 KB/s * 27.9 MB/min (varies with motion)",...}

RoeSnip.exe --auto state
:: read estimateText/fps/preset again any time - they're always the chrome's live readout, verbatim

RoeSnip.exe --auto "{\"cmd\":\"select\",\"x\":100,\"y\":100,\"w\":1200,\"h\":800}"
:: still setup - resize is allowed pre-Start; estimateText now reflects the bigger canvas
:: {"ok":true,"mode":"setup","selection":{"x":100,"y":100,"w":1200,"h":800},
::  "estimateText":"~951.6 KB/s * 55.8 MB/min (varies with motion)",...}

RoeSnip.exe --auto "{\"cmd\":\"preset\",\"tier\":\"compact\"}"
:: quality and framerate are independent axes - changing the tier alone leaves fps at 25. Compact's
:: estimate uses the quality/fps expansion workstream's CALIBRATED blended factor (0.498, not a
:: hand-picked guess - see GifSizePresets.ForPreset's own doc comment for the measured table).
:: That factor folds in Compact's 0.75 render scale (the 2026-07-13 tier-spread pass): the same
:: day's visual retune constrained the lossy-run threshold to what smooth gradient/photo content
:: tolerates, leaving Compact's byte ratio (0.865) nearly indistinguishable from Balanced's, so
:: the Low tier now gets its real size step from a 0.75-resolution render instead - text stays
:: legible, just softer, and 0.498 is the re-measured on-disk ratio with that scale included.
:: {"ok":true,"mode":"setup","preset":"compact","fps":25,
::  "estimateText":"~473.9 KB/s * 27.8 MB/min (varies with motion)",...}

RoeSnip.exe --auto "{\"cmd\":\"fps\",\"value\":10}"
:: and fps alone leaves the tier at compact - the slider and the size row move independently, same
:: as the UI. 10 is a perfectly legal GIF fps now (not one of the old four fixed chip values) -
:: see the `fps` command's own doc above for why any integer 5-50 works.
:: {"ok":true,"mode":"setup","preset":"compact","fps":10,
::  "estimateText":"~189.6 KB/s * 11.1 MB/min (varies with motion)",...}

RoeSnip.exe --auto "{\"cmd\":\"screenshot\",\"path\":\"C:\\temp\\region-outline.png\",\"includeExcluded\":true}"
:: {"ok":true,"path":"C:\\temp\\region-outline.png","width":1920,"height":1080}

RoeSnip.exe --auto escape
:: {"ok":true,"mode":"idle",...}   -- Setup's Cancel discards the take (nothing was ever recorded)
```
