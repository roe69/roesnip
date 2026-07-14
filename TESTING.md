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
toolbar shows, exactly as a completed drag would leave it. In `setup`/`capturing`/`reviewing` mode
(any phase of a recording session): applies the rect to `RegionOutline` through the exact band-drag
code path, so `RegionChanged` → `RecordingSession.OnRegionMoved` → the chrome's `UpdateSelection`
all fire, same as a real drag — including, once a take exists (`capturing`/`reviewing`), the
size-locked MOVE-ONLY semantics a real drag has then (`w`/`h` in the request are ignored; only
`x`/`y` — the desired top-left — are honored, and the recorder's own crop follows, same as dragging
the band with the mouse). Multi-monitor recording (drag handoff): a `select` that moves the region
so it's no longer fully contained in its current monitor's bounds during `capturing`/`reviewing`
triggers the same cross-monitor handoff a real drag across that boundary would — see "Cross-monitor
recording" below. Errors only if idle (nothing to select).

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

`{"cmd":"chrome","action":"start"}` — one of `start`/`stop`/`save`/`share`/`cancel`/`pause`/`resume`.

Raises the same button `Click` event a real mouse click on that chrome button would — `start`/
`stop` share one button exactly like the real UI (`start` valid only in `setup`, `stop` only in
`capturing`). `pause` requires `capturing` and not already paused; `resume` requires already
paused (valid from `capturing` or `reviewing` — a soft-stopped take is a paused take, see
`RecordingSession.Resume`'s own doc comment); `save` requires `reviewing`; `cancel` is valid in any
phase. An action invalid for the current phase errors with that phase instead of silently no-op'ing
the way a disabled button would.

`share` (Sharing/* subsystem, added alongside the toolbar's own Share wiring) requires `reviewing`
and errors if a save or share is already in progress. It hard-stops the still-alive pipeline (same
finalize Save uses), uploads the temp file to the configured default share provider WITHOUT moving
it, then re-arms into `setup` immediately — the upload itself is handed to `ShareFlowPresenter`,
which shows its own small `ShareResultWindow` toast (Uploading → Success/Failure) independent of
whatever the chrome/session is doing by then; the URL is copied to the clipboard automatically on
success, and a failed/cancelled upload leaves the recording's temp file in place and names its path
in the toast. See `RecordingSession.RequestShare`'s own doc comment in `Recording/RecordingController.cs`
for the full design.

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

#### `confirm`

`{"cmd":"confirm","action":"copy"}` or `{"cmd":"confirm","action":"save","path":"C:\\temp\\out.png"}`

Multimon-selection addition: the overlay's Copy/Save had no automation entry point at all before
this — the toolbar's Copy button raises the same `OverlayCommand.Copy` this does, but Save's real
path (`TryShowSaveDialog`) pops an interactive `SaveFileDialog`, which must never happen from a
headless automation call (it would hang the pipe waiting for a human). `action: "save"` therefore
**requires an explicit `path`** and writes directly via `PngWriter`, skipping the dialog entirely —
the same production render (`RenderSelectionWithAnnotations`, or `RenderSpanningSelection` for a
selection spanning multiple monitors) either path already uses. Requires an active overlay session
with a selection; errors otherwise. HDR export (`SaveHdrRequested`) is not reachable through this
command — use the toolbar's Save right-click menu for that on a single-monitor selection (never
available for a spanning one; see the cross-monitor selection section below).

```
--auto '{"cmd":"confirm","action":"copy"}'
```
```json
{"ok":true,"mode":"idle", ...}
```

Closes the overlay on success, same as a real Copy/Save click — the trailing `state` snapshot
reflects the post-close (`idle`) state, not the selection that was just confirmed.

`action: "share"` (Sharing/* subsystem, wired in the same integration pass as the chrome `share`
action above) raises the same `OverlayCommand.Share` the toolbar's Share button raises: renders the
current selection through the exact path Copy uses (including a spanning stitch), hands it to
`ShareFlowPresenter`, and closes the overlay IMMEDIATELY, same as `copy`/`save` (the trailing `state`
snapshot reflects the post-close state, not `overlay`). The upload itself runs detached; its
progress/result is reported via the presenter's own `ShareResultWindow` toast — a successful URL is
copied to the clipboard automatically, a failed/cancelled upload names the kept file/error in the
toast. Requires a selection and at least one enabled share provider (Settings > Sharing).

### Cross-monitor selection (`select` spanning multiple monitors)

`select`'s rect can cross a monitor boundary — e.g. on a 3-monitor layout with `DISPLAY3` at
`(0,0)-(2560,1440)` and `DISPLAY1` at `(0,-1440)-(2560,0)` directly above it,
`{"cmd":"select","x":600,"y":-200,"w":800,"h":500}` selects a rect that's part `DISPLAY1`, part
`DISPLAY3`. This routes through the exact same distribute mechanism a real mouse drag crossing that
same boundary uses (`OverlaySession.OnSpanningCandidate`) — never a separate implementation. `state`'s
`selection` reports the union rect in the same virtual-desktop physical pixels as always; nothing
about the wire shape changes for a spanning rect.

Annotations are still a v1 cut for a spanning selection — see `docs/DESIGN-MULTIMON-SELECTION.md`
for why. `confirm`'s `copy`/`save` actions both work (rendering a byte composite stitched from each
intersected monitor's own already-tone-mapped crop; gaps where no monitor covers part of the rect are
opaque black). **Record and HDR save are no longer refused while spanning** (integration pass,
2026-07-13, `spanning-recording`/`spanning-selection-complete` merge): `record` hands the spanning
virtual rect off to `RecordingSession`, whose own `BeginCapture` re-derives the intersected monitor
set and takes the spanning capture path (`SpanningCanvasCompositor`/`EncoderLoopSpanning`)
automatically once that set has 2+ monitors — see `OverlaySession.RecordSpanning`'s doc comment in
`Overlay/OverlayController.cs`. HDR save was already resolved by the `spanning-selection-complete`
merge (`JxrWriter.WriteSpanning`); this pass only touched Record.

**Verified 2026-07-13** (this machine, real 3-monitor HDR layout): a real synthetic mouse drag from
DISPLAY3 into DISPLAY1 produced exactly the requested `{x:600,y:-200,w:800,h:500}` selection, and a
real Ctrl+C produced an 800×500 clipboard image containing correctly stitched content from both
monitors. The `select`/`confirm` **automation commands** themselves proved unreliable on this specific
dev machine — `OverlayInputInterop`'s low-level keyboard hook doesn't filter injected input
(`LLKHF_INJECTED`), and something on this machine intermittently/frequently injects a synthetic
Escape that reaches it once an `InvokeOnUi`-driven automation call does real UI work (observed via a
temporary diagnostic stack trace: `SessionKeyboardHook` → `ProcessKeyCommand` → `CancelStage` →
`Finish(null)`, landing within milliseconds of `select`/`confirm`, while passive `state` polls and
real mouse/keyboard-driven interaction were unaffected) — reproduced identically against a pristine,
unmodified build, so this is a pre-existing environmental characteristic of automation on a live,
actively-used desktop, not a defect in the spanning-selection feature itself. Not something this
branch attempts to fix (touching the keyboard hook is explicitly out of scope — see DESIGN.md's own
"hard-won fixes" warning); flagged here for whoever next drives automation on a similarly busy
machine.

### Cross-monitor recording (drag a live take across a monitor boundary)

Multi-monitor recording phase 1 (`multimon-recording-p1` branch, PLAN-MULTIMON-RECORDING.md):
distinct from cross-monitor SELECTION above, which is about the one-shot capture/annotate flow. This
is about a REGION THAT'S ALREADY RECORDING (`capturing` or `reviewing`) being dragged (or `select`ed
— see the `select` command's own doc above) across a monitor boundary mid-take.

Phase 1 is "snap, not spanning": the recorded content is never a stitched composite of two
monitors at once (that's phase 2, not built here). The instant the region's bounding rect would stop
being fully contained in its current monitor, RoeSnip tears down the `RegionRecorder` on the monitor
it's leaving and builds a fresh one on the monitor now holding the majority of the region — a brief
"pop" in the recording (a few dropped frames while the new WGC session spins up; VFR timestamps and
GIF's own patch-behind carry absorb the gap without any visible time distortion) followed by content
from the NEW monitor. The encoder's canvas dimensions never change (only the position moves), and the
tone-map exposure is RECOMPUTED fresh for the destination monitor rather than carried over or
blended — a visible brightness/color shift across the handoff on two differently-calibrated monitors
is the expected, photometrically-correct behavior, not a bug.

`select` during `capturing`/`reviewing` is a size-locked MOVE (see that command's own doc above) —
this is the production path automation drives a handoff through, exactly mirroring what dragging the
region's band with the mouse does. A single `select` call that jumps straight onto a different
monitor (rather than a smooth incremental drag) triggers exactly one handoff, landing on whichever
monitor the FINAL rect mostly overlaps — intermediate monitors the jump would have crossed are never
visited.

```
RoeSnip.exe --auto "{\"cmd\":\"select\",\"x\":100,\"y\":100,\"w\":800,\"h\":600}"
RoeSnip.exe --auto "{\"cmd\":\"record\",\"format\":\"gif\"}"
RoeSnip.exe --auto "{\"cmd\":\"chrome\",\"action\":\"start\"}"
:: {"ok":true,"mode":"capturing", ...}

:: Drag the SAME size region onto DISPLAY1 (negative-Y monitor in this rig's layout) mid-take:
RoeSnip.exe --auto "{\"cmd\":\"select\",\"x\":100,\"y\":-1300,\"w\":800,\"h\":600}"
:: {"ok":true,"mode":"capturing","selection":{"x":100,"y":-1300,"w":800,"h":600}, ...}
:: w/h echoed back unchanged (size-locked); stderr logs "recording handed off DISPLAY3 -> DISPLAY1 ..."
```

Given the same-machine automation reliability caveat documented in the cross-monitor SELECTION
section above (a synthetic Escape occasionally reaching `SessionKeyboardHook` mid-`InvokeOnUi`), a
live E2E run on a busy dev box should retry a `select`/`chrome` call that comes back `ok:false`
unexpectedly rather than treating one failure as conclusive.

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

## Driving RoeSnip.App programmatically (agents/E2E)

Applies to the Avalonia port (`src/RoeSnip.App`, item 03-automation-pipe), NOT the frozen WPF app
above. Same idea as the WPF app's own automation channel — a deterministic, line-based JSON
control pipe that calls straight into the same production code a real click/drag runs
(`OverlayWindow`'s own `SetSelection`/`OnCommand` via the `*ForAutomation` hooks on
`OverlayController`/`OverlayWindow`), never a re-implementation, never simulated input — ported
into `src/RoeSnip.App/AppShell/AutomationServer.cs`, ONLY covering what this port currently has:
the overlay (capture/select/copy/save), not recording. `NamedPipeServerStream`/
`NamedPipeClientStream` map to Unix domain sockets on macOS/Linux, so the whole channel is
portable — nothing here is Windows-only.

**Distinct pipe name from the WPF app on purpose**: `RoeSnip.App-Automation`, not
`RoeSnip-Automation` — both apps' residents run side by side on this machine (same rationale as
`AppShell/SingleInstance.cs`'s own mutex/pipe name split), and sharing a name would let whichever
app's `--auto` client connects second silently drive the OTHER app's resident.

**Zero behavior change when off.** With neither the flag nor the env var below set, this channel
does not exist: no pipe, no listener thread, nothing.

### Starting the resident with automation enabled

```
set ROESNIP_AUTOMATION=1
RoeSnip.exe
```

or equivalently:

```
RoeSnip.exe --automation
```

(`RoeSnip.exe` here is the Avalonia app's own binary — same `AssemblyName` as the WPF app, so
make sure you're launching the one you mean; `src/RoeSnip.App/bin/<config>/<tfm>/RoeSnip.exe`.)
`AppComposition.RunTray`'s hidden-flag allowlist accepts `--automation` specifically (mirroring
the WPF app's `TrayApp.Run`); any other unrecognized argument still prints usage and exits 1.

### The `--auto` CLI client

`RoeSnip.exe --auto '<json>'` connects to the running resident's automation pipe, sends one JSON
request line, prints the response line to STDOUT, and exits 0 (`"ok":true`) or 1 (`"ok":false`, or
any failure to connect). Intercepted in `Program.Main` before any single-instance machinery, so it
never touches `AppShell/SingleInstance.cs`'s mutex/pipe — always safe to run alongside a live
resident.

Zero-arg commands (`state`, `trigger`, `escape`) accept the same shorthand as the WPF app:

```
RoeSnip.exe --auto state
RoeSnip.exe --auto '{"cmd":"state"}'      # equivalent
```

### Commands implemented in this port

Same wire shape as the WPF app's `state` response (see that section above for the full field
reference) — `mode` is `idle`/`overlay` while no recording is active, and `setup`/`capturing`/
`reviewing` (with `recordingFormat`/`preset`/`estimateText`/`fps`/`fpsRange` populated) while one is
— see the recording section below.

- **`state`** — current mode/selection/monitor list.
- **`trigger`** — opens the capture overlay (same action as the tray icon / hotkey / the bare
  `capture` CLI verb / a second-instance signal). Blocks (up to ~5s) until the overlay is
  actually up.
- **`select`** — `{"cmd":"select","x":100,"y":100,"w":400,"h":300}`. Sets the selection on
  whichever monitor contains the rect's top-left corner, through the exact same
  `SetSelection`/clamp/too-small-cancel path a real mouse-up finishing a drag takes. Errors if
  idle (nothing to select — use `trigger` first).
- **`escape`** — cancels the open overlay (if any) and returns to `idle`. A no-op
  (`"ok":true`) if already idle.
- **`screenshot`** — `{"cmd":"screenshot","path":"C:\\temp\\shot.png","rect":{...}}`. Captures via
  this app's OWN `CaptureService`/`SdrImage`/`PngWriter` pipeline (the same one `--capture` and
  the interactive overlay use), not a raw desktop grab — fully portable, but means an explicit
  `rect` must fall within a SINGLE monitor's bounds (its top-left corner picks the monitor; the
  rect is then clamped to that monitor's captured frame). Omit `rect` to capture the primary
  monitor's full bounds. `"includeExcluded":true` temporarily clears
  `WDA_EXCLUDEFROMCAPTURE` on this process's own windows (item 02's
  `WindowCaptureExclusion.ClearOnOwnWindows`/`Restore`) for the duration of the capture, same
  caveat as the WPF app's version (the affinity change and the capture aren't atomic with the
  compositor). Only path/width/height are returned, never image bytes.
- **`confirm`** — `{"cmd":"confirm","action":"copy"}` or
  `{"cmd":"confirm","action":"save","path":"C:\\temp\\out.png"}` or
  `{"cmd":"confirm","action":"share"}`. `"save"` **requires an explicit `path`** and writes
  directly via `PngWriter`, never popping the interactive save picker (which would hang the pipe
  waiting for a human) — same production render (`RenderSelectionWithAnnotations`) either path
  already uses. `"share"` (Sharing/* subsystem, item 12) uploads to the configured default
  provider through the exact same `OverlayCommand.Share` path the toolbar's Share button raises —
  same as `copy`/`save` it closes the overlay immediately (the response's `mode` reflects the
  post-close state, not `"overlay"`); the upload's own progress/result — success or failure —
  is reported via a `ShareResultWindow` toast, same as a real click. Requires an active overlay
  session with a selection; errors otherwise.

### `record` / `preset` / `fps` / `chrome` (item 21 — recording)

Live, same wire shape as the WPF app's own protocol section above (`mode` becomes `setup`/
`capturing`/`reviewing` while a recording is active; `recordingFormat`/`preset`/`estimateText`/
`fps`/`fpsRange` populate accordingly):

- **`record`** — `{"cmd":"record","format":"gif"}` or `"mp4"`. Closes the overlay (same as a
  toolbar Record menu pick) and opens a recording session in `setup`. Requires an active overlay
  session with a selection; a multi-monitor (spanning) selection errors instead of silently
  recording one monitor (spanning recording is not ported — single-monitor takes only).
- **`preset`** / **`fps`** — Setup-only, same tier/range validation as the WPF wire contract.
- **`chrome`** — `{"cmd":"chrome","action":"start"}` (also `stop`/`pause`/`resume`/`save`/`share`/
  `cancel`). Drives the exact same button handler a real chrome click would; an action invalid for
  the current phase errors instead of silently no-opping. `save` with no explicit path (this port
  has no save-file dialog) only succeeds when `ROESNIP_RECORD_AUTOSAVE` is set on the resident's own
  environment — otherwise the take stays in `reviewing` and the failure is logged to stderr, never
  silently swallowed. `share` with no configured provider is a fail-closed no-op (the take stays
  exactly as it was, still soft-stopped, still Resumable) — see RecordingOrchestrator.RequestShare's
  own doc comment for the full contract (ported verbatim from the WPF reference's 422e87a fix).
- **`select`** while a recording is active moves/resizes the (Windows-only) RegionOutline instead of
  the overlay selection — size-locked (move-only) once Capturing/Reviewing, same as a real band drag.
- **PrtScr / `trigger` while a recording is active**: TrayApp.TriggerCapture checks
  `RecordingOrchestrator.IsActive` BEFORE its own overlay-trigger bookkeeping — a `trigger` call (the
  same entry point a real PrtScr press uses) advances the chrome state machine instead of opening a
  new overlay: `setup` → BeginCapture, `capturing` → StopCaptureToReview, `reviewing` → Save. This is
  the actual, most-representative way to E2E the PrtScr contract via the automation pipe (rather than
  the `chrome` command, which drives the same handlers but not through TriggerCapture's own dispatch).

**Verified 2026-07-14** (this machine, standalone `--automation` instance with
`ROESNIP_RECORD_AUTOSAVE` set to a scratch directory, started and killed by this item's own session,
never the user's real resident): `trigger` → `select` → `record gif` → `setup` (estimate/preset/fps
all populated); three successive `trigger` calls (simulating three PrtScr presses) advanced
`setup` → `capturing` → `reviewing` → (`save` via the autosave env var) → back to `setup` with the
SAME region re-armed — the produced file (`roesnip_<timestamp>.gif`) decoded via a REAL GIF decoder
(`System.Drawing.Image.GetFrameCount`, not this codebase's own) at the exact requested 400x300
dimensions. `chrome share` with no provider configured left the session in `reviewing` untouched
(fail-closed verified) and logged the exact "configure a share provider in Settings first" message
to stderr. `preset`/`fps` commands changed the live estimate text. A screenshot
(`includeExcluded:true`) of a fresh MP4 Setup session showed RecordingChrome rendering correctly
(Mic off/System audio off pills, Quality row with High selected, FPS slider at 30, live estimate,
Start/Restart/Save/Share/Cancel with the correct Setup-phase enable states) anchored below the
RegionOutline's dotted boundary around the selected region. `chrome cancel` tore the whole session
down back to `idle` and released CaptureGate (confirmed by a following ordinary `trigger`/`escape`
cycle still working). No RoeSnip process was left running afterward, and the standalone instance's
own `%APPDATA%\RoeSnip.App` (freshly created by this test, nothing there before) was removed.
NOT verified live: the RegionOutline band drag/resize interaction itself (needs live mouse input,
same interactive-session gap this file's own item 07/08 notes already carry) and MP4 mic/system-audio
capture (this machine's WASAPI devices were not exercised by this pass — item 19's own Mp4Encoder/
AudioCapture tests already cover that path).

### Worked example

```
set ROESNIP_AUTOMATION=1
RoeSnip.exe --automation &

RoeSnip.exe --auto trigger
:: {"ok":true,"mode":"overlay","selection":null,...}

RoeSnip.exe --auto "{\"cmd\":\"select\",\"x\":100,\"y\":100,\"w\":400,\"h\":300}"
:: {"ok":true,"mode":"overlay","selection":{"x":100,"y":100,"w":400,"h":300},...}

RoeSnip.exe --auto "{\"cmd\":\"confirm\",\"action\":\"copy\"}"
:: {"ok":true,"mode":"idle",...}   -- overlay closes on success, same as a real Ctrl+C
```

**Verified 2026-07-13** (this machine, real 3-monitor HDR layout, a standalone instance started
and killed by this item's own session, never the user's real resident): `state` (idle, 3
monitors listed), `trigger` -> `overlay`, `select` -> selection echoed back, `record` -> the
structured unsupported error (not "unknown command"), `confirm copy` -> overlay closed back to
`idle`, `screenshot` (both bare and with an explicit `rect`+`includeExcluded`) -> valid PNGs at
the reported dimensions, a second `trigger`+`select`+`confirm save` -> PNG written to the given
path and overlay closed, `escape` while idle -> no-op `ok:true`.

## RoeShare owner-token/expiry/embed integration (`roesnip-integration`, 2026-07-14)

ShareConfigField gained Options/DefaultValue (generic ComboBox support + seeded/backfilled
defaults in both edit windows), RoeShare's spec gained a configurable ExpiresIn field
(Never/1h/1d/1w/30d, default Never) and `{Mime}` in its Endpoint, wiring the client half of
CONTRACT.md's B (expiry)/C (embeds)/D (owner tokens) additions from the already-deployed
`roeshare-server` package.

**Verified 2026-07-14 (live, `https://share.roelite.net`, real production API key from the
user's own already-configured resident RoeSnip provider row, a standalone `RoeSnip.App
--automation` instance in its own isolated `%APPDATA%\RoeSnip.App` — never the resident on
either app — started and killed by this session; settings.json/capture-cache.json restored to
their pre-test byte-identical contents afterward; every test share deleted from the live
server afterward):**

- **Live bug found and fixed by this pass:** RoeShare's one-shot upload route reads the stored
  file mime from a `?mime=` query param (CONTRACT.md), never from the request's Content-Type
  header — RoeShare's Endpoint template never sent it, so every RoeSnip upload landed as
  `application/octet-stream` and could never qualify for the embed feature (C) at all. Fixed by
  auto-populating `{Mime}` from the request alongside the existing `{Filename}`, referenced in
  the Endpoint as `&mime={Mime}` (commit "Send the file's mime type on RoeShare uploads").
  Confirmed via a manual raw request that an unencoded `/` in `mime=image/png` round-trips fine
  through the live server's query parsing.
- **`--auto trigger` → `select` → `confirm share`**: overlay closed immediately (`mode` went
  `overlay` → `idle`, not staying `overlay` as the pre-`roesnip-ux` behavior did), a screenshot
  taken immediately after showed the new `ShareResultWindow` in its Success state — title
  "RoeShare", "Uploaded" label, the clean URL as selectable text, Open/Copy always visible
  bottom-right — and the clean URL (no `#edit=` fragment) was on the clipboard, confirming the
  Copy/auto-clipboard-vs-Open URL split.
- **Expiry (B):** the resulting share's `GET /api/shares/:id` showed `"expiresAt":null` (RoeSnip's
  default `ExpiresIn=0` reached the server correctly, both for a fresh config and, in a separate
  test, a config with `ExpiresIn` set directly in `Values` mimicking a pre-existing row missing
  the key — both landed on `expiresAt:null`). Setting `Values.ExpiresIn="3600"` directly (the
  ComboBox itself needs synthetic mouse/keyboard input outside the `--auto` pipe's coverage, same
  documented gap as `ShareProviderEditWindow` elsewhere in this file) and uploading again produced
  `expiresAt - createdAt == 3600` exactly.
- **Embed (C):** with the mime fix in place, `GET /<id>` (the root slug fallback — the actual URL
  shape RoeSnip's one-shot upload returns) rendered full rich `og:title`/`og:description`/
  `og:image`/`og:image:type`/`twitter:*` tags, and `og:image` was fetched directly — 200,
  `Content-Type: image/png`, byte count matching the uploaded file exactly.
- **Owner token (D):** a share created with the exact same request shape RoeSnip's provider spec
  sends (`?expiresIn=0&mime=image/png`, `X-Filename`, `Authorization: Bearer`) returned a real
  `editToken`. Loading `{url}#edit={token}` in a headless Edge tab (CDP) showed
  `location.hash === ""` after load (the fragment-strip-immediately defense verified live, not
  just by code review) and the page body contained "You own this share" plus Change expiry/Make
  private/Delete controls; the single image file's preview `<img>` was present without any click
  (the single-complete-image auto-expand). Calling the page's own authenticated
  `fetch('/api/shares/:id', {method:'PATCH', body:{password:...}})` (same call the "Make
  private..." owner control makes) returned `{"ok":true,"protected":true}`, and a subsequent
  anonymous `GET /api/shares/:id` (no cookie/token) got `401` — the visitor password gate. The
  now-private share's `GET /<id>` meta reverted to the fixed generic tags, byte-identical to the
  missing-share case. `DELETE` via the same owner-authenticated fetch removed it (`404` on a
  follow-up lookup).
- `dotnet build RoeSnip.sln -c Release` / `dotnet test RoeSnip.sln -c Release` — full suite green
  throughout (1113 tests across the four test projects after this phase's additions).

**NOT verified — deliberately not driven interactively this phase:**

- Clicking the ComboBox/Options UI in `ShareProviderEditWindow` itself (WPF or Avalonia) with a
  real mouse — same documented gap as every other settings-window interaction in this file; the
  persisted-value effect it produces was instead verified directly via `settings.json` edits (see
  the expiry bullet above) plus the unit tests covering `BuildOptionsComboBox`'s selection/
  fallback logic at the model layer.
- The Discord embed specifically (an actual Discord message unfurl) — verified instead via a
  direct fetch of `og:image` and the exact meta tags Discord's unfurler reads, which is the same
  surface a real Discord fetch exercises.
- The WPF app's own share flow against the live server this phase (Avalonia only) — the WPF
  result window/presenter share the same `RoeSnip.Core` provider plumbing this phase changed and
  were unit-tested identically; only the Avalonia app was driven live, per the automation pipe's
  existing side-by-side-with-the-resident precedent in this file.

## Sharing/upload subsystem (`src/RoeSnip/Sharing/*`, added 2026-07-13)

The declarative share-upload feature: `IShareProvider`/`ProviderSpec`/`ShareProviderCatalog`/
`ShareManager` plus the settings UI (`App/ShareProvidersWindow`, `App/ShareProviderEditWindow`) and
the two integration-point UI stubs (`Overlay/ToolbarControl`'s Share split-button,
`Recording/RecordingChrome`'s Reviewing-state Share button).

**Verified this phase (2026-07-13, unit tests only — no live network, no resident launched):**

- `dotnet build RoeSnip.sln` — 0 warnings, 0 errors (all 9 projects, both RoeSnip.App TFMs), same as
  the existing Phase B matrix above.
- `dotnet test RoeSnip.sln` — full suite green, RoeSnip.Tests now 5xx/5xx including
  `tests/RoeSnip.Tests/Sharing/*` (`TemplateExpanderTests`, `ResponseUrlExtractorTests`,
  `ProviderSpecShareProviderTests`, `ShareProviderCatalogTests`, `ShareManagerTests`,
  `ShareProviderSettingsPersistenceTests`) — every HTTP call in that suite goes through
  `StubHttpMessageHandler`, a mock `HttpMessageHandler`; nothing in it opens a real socket.
  `SettingsTests.cs`'s two whole-record round-trip tests were updated for the new
  `ShareProviders` `List<T>` field (same reference-equality-quirk neutralization the file already
  applies to `RecentPickedColors`/`PaletteColors`/etc.).

**Verified 2026-07-13 (integration pass — live full-chain E2E on this machine, single-file Release
publish swapped in as the resident, local RoeShare instance on 127.0.0.1:3399 with a throwaway
DATA_DIR + fresh API key):**

- **Spanning record → chrome Share:** `trigger` → `select {x:600,y:-200,w:800,h:500}` (spans
  DISPLAY1 into DISPLAY3, red/blue marker windows on the two monitors) → `record gif` → chrome
  `start` → ~5 s → chrome `stop` → chrome `share`. stderr logged
  `recording capture started (Gif, 800x500, 50fps, 2 monitor(s) [spanning])`; the upload landed in
  the local RoeShare (share id `ArXrhy2oqR`, 11392 bytes, creatorUa `RoeSnip-Sharing`, attributed to
  the E2E API key), the URL was on the clipboard, the temp gif was deleted after upload, and the
  session re-armed to `setup`. The downloaded GIF decodes as 800x500 with the DISPLAY1 marker's
  exact red (220,20,20) in the top band and the DISPLAY3 marker's exact blue (20,60,220) below it —
  a genuine two-monitor stitch. (1 frame total: static screen, WGC only delivers dirty frames.)
- **Toolbar Share (plain selection):** `trigger` → `select {x:550,y:50,w:500,h:400}` (fully on
  DISPLAY3) → `confirm {action:"share"}`. Overlay stayed open (as designed), upload landed (share id
  `zGNFNmop6b`, 4636 bytes), URL on clipboard, and the downloaded PNG is 500x400 with the blue
  marker's exact pixels — byte-identical round trip.
- RoeShare's admin flow was also incidentally exercised for setup: `POST /api/admin/login` +
  `POST /api/admin/api-keys` (with an Origin header — its CSRF check rejects header-less curl).

**NOT verified — still open after the integration pass:**

- Every non-RoeShare built-in spec (Imgur, catbox, litterbox, 0x0.st, GoFile, file.io) against its
  real endpoint, and ShareProviderEditWindow's Test button against a real provider.
- The Share buttons clicked with a real mouse (E2E drove the same Click-raising production paths via
  the automation pipe; see the synthetic-Escape hook caveat above for why UIA/synthetic input is
  avoided on this machine).
- An upload FAILURE surfacing through the tray balloon (only success paths ran live; the error path
  is unit-tested).

**Previously NOT verified — deliberately out of scope in the original sharing phase (for the
record; each item above supersedes its counterpart here):**

- Any of the seven built-in `ProviderSpec`s (RoeShare, Imgur, catbox.moe, litterbox, 0x0.st, GoFile,
  file.io) against a REAL live endpoint. Each was checked against the provider's current public docs
  during implementation (see each `ShareProviderCatalog` entry's own `Notes`); GoFile specifically is
  marked `Verified = false` ("untested" in the settings UI) because only unofficial/community
  documentation could be found for its exact response shape and fixed-server assumption.
- `ShareProviderEditWindow`'s Test button end-to-end against a real provider — the button is fully
  implemented (drives the same `ShareManager.UploadAsync` the unit tests exercise, against a real
  generated PNG via `Sharing/ShareTestImage`), just never clicked against the real network by this
  track.
- The actual "click Share, see a URL land in the clipboard/balloon" user flow for either integration
  point. `ToolbarControl`'s Share split-button and `RecordingChrome`'s Reviewing Share button are
  fully built (events, provider-picker menu, busy/disabled states) but neither is wired to a live
  caller yet: that wiring needs `Overlay/OverlayWindow.xaml.cs` (resolves the rendered selection) and
  `Recording/RecordingController.cs` (owns the finished take's temp file path) respectively — both
  are explicitly out of scope for this track (Overlay selection internals / Recording pipeline
  files) and are left for whichever track/phase owns those files next. Both new buttons currently sit
  disabled in a running app until that wiring lands, same as the placeholder Upload button they
  replaced.
- Any WPF window in this feature (`SettingsWindow`'s new Sharing section, `ShareProvidersWindow`,
  `ShareProviderEditWindow`) opened on screen — no resident was launched this phase (single-instance
  mutex conflict with parallel tracks), so only compiled/reviewed, never clicked through interactively.

## RoeSnip.App Sharing UI (item 12, added 2026-07-14)

The Avalonia port's own Sharing UI: `AppShell/ShareProvidersWindow`, `AppShell/
ShareProviderEditWindow`, `SettingsWindow`'s new "Sharing" section, `Overlay/ToolbarControl`'s
Share split button, and `Overlay/OverlayController`'s `ShareCurrentSelection`/
`ShareToSpecificProvider`. See PARITY.md item 12 for the full behavioral write-up (Avalonia-specific
deviations: async `ShowDialog` instead of WPF's blocking one, `TextBox.PasswordChar` instead of
`PasswordBox`, an inline `ValidationErrorText`/owned Yes-No dialog instead of `MessageBox`,
`Dispatcher.UIThread.Post` instead of a per-window `Dispatcher.BeginInvoke`).

**Verified 2026-07-14 (this machine, real 3-monitor HDR layout, a standalone `--automation`
instance started and killed by this item's own session, never the user's real resident — same
discipline the item-03 automation-pipe verification above already established):**

- `--auto trigger` + `select` + `screenshot` of the toolbar region with zero providers configured:
  the Share button (up-arrow-out-of-tray icon) and its chevron render visibly DIMMED but present
  between Save HDR and Cancel (d8fa815's "disabled, not hidden, not dead" preserved).
- Added a fake enabled custom provider (`Endpoint: https://example.invalid/upload`) directly to
  this instance's own isolated `%APPDATA%\RoeSnip.App\settings.json`, restarted the resident, same
  `trigger`+`select`+`screenshot`: the Share button/chevron now render fully bright (enabled) —
  confirms the `ShareProviders`/`DefaultShareProviderId` settings round-trip and
  `ToolbarControl.SetShareProviders`'s enable logic.
- `--auto confirm {"action":"share"}` against that fake provider: response `mode` stayed
  `"overlay"` (does NOT close, unlike copy/save) with `"ok":true`; a screenshot taken immediately
  after showed the Share button DIMMED again (`SetShareBusy(true)`); stderr logged
  `Test Fake Provider: upload request failed: No such host is known. (example.invalid:443)`
  (a real HTTP attempt against the deliberately-unresolvable `.invalid` host, run through the exact
  production `ShareManager.UploadAsync` → `ProviderSpecShareProvider` path); a follow-up screenshot
  showed the Share button re-enabled (`SetShareBusy(false)` restored correctly after the failure).
- `--auto confirm {"action":"share"}` with NO provider configured (before the fake-provider edit
  above): `"ok":true`, `mode` stayed `"overlay"`; stderr logged
  `Share failed: no share provider is configured.` — `ShareCurrentSelection`'s defensive
  `ResolveDefault is null` branch.
- `RoeSnip.exe settings` (signals the running --automation resident, same as the bare CLI verb
  would) + full-screen `screenshot`: `SettingsWindow`'s new "Sharing" section renders — "Default
  share provider" combo disabled showing "No provider configured yet", "Providers..." button
  present.
- `dotnet build RoeSnip.sln` / `dotnet test RoeSnip.sln` — 0 warnings/errors, full suite green (902
  tests; net test-count unchanged — one `AutomationProtocolTests` case flipped from asserting
  `"share"` is rejected to asserting it's accepted, no test added/removed).
- Test-only settings.json edits were reverted and the standalone instance's process was killed
  before this session ended; nothing was left running.

**NOT verified — deliberately not driven interactively this phase:**

- `ShareProvidersWindow`/`ShareProviderEditWindow` opened on screen (clicking "Providers...",
  "Configure...", "Custom...", "Test", "Remove"). Driving these needs synthetic mouse/keyboard input
  outside the `--auto` automation pipe's coverage (the pipe only reaches the overlay), which this
  session avoided per the "no long-lived synthetic input" rule — same precedent the WPF app's own
  item-11 pass already set for these two windows ("unit tests only... never clicked through
  interactively"). Verified instead via clean compiles on both `RoeSnip.App` TFMs plus code review
  against the WPF reference these were ported from line-for-line.
- Any real, resolvable provider (RoeShare or any of the built-ins) end-to-end — only the
  deliberately-failing `example.invalid` fake config above was exercised.
- A SUCCESSFUL upload's tray balloon / clipboard-URL path (`ShowShareUploadedBalloon`) — only the
  failure branch (`ShowError`) was exercised live; the success branch is unit-tested-shaped
  (mirrors `ShowSavedBalloon`, which the item-03/09 passes above already verified live) but not
  driven against a real upload this phase.

## RoeSnip.App app-shell parity batch (item 14, added 2026-07-14)

Six independent fixes: hotkey-rebind suspend/resume, PrintScreen keyup-only capture, real key
names in the hotkey display, a WM_SETTINGCHANGE broadcast after the PrtScr consent registry
write, a competing-screenshot-tool startup warning, a ColorPickerEnabled toggle, and `.ico`
branding. See PARITY.md item 14 for the full behavioral write-up.

**Verified 2026-07-14 (this machine, real 3-monitor HDR layout, a standalone `--automation`
instance started and killed by this item's own session, never the user's real resident):**

- `dotnet build RoeSnip.sln` / `dotnet test RoeSnip.sln` — 0 warnings/errors, full suite green
  (946 tests; +27 from the new `HotkeyDisplayFormatTests`).
- `RoeSnip.exe settings` (signals the standalone `--automation` resident) + full-screen
  `screenshot`: the new "Enable the colour picker..." checkbox renders with the same wording as
  the WPF app; the Hotkey box reads "PrintScreen" (the default binding, round-tripped through
  `HotkeyDisplayFormat.DescribeHotkey`/`DescribeVirtualKey`'s inverted-KeyCode-table lookup, not
  raw hex); the Settings window's titlebar shows the bundled `roesnip.ico` glyph (decoded
  byte-for-byte the same orange/black icon as the source file when rendered standalone via
  `System.Drawing.Icon` for comparison) — confirms the `avares://RoeSnip/Assets/roesnip.ico`
  AvaloniaResource load path works, not the procedural-glyph fallback.
- The standalone instance was killed immediately after (`Stop-Process`); `Get-Process -Name
  RoeSnip` confirmed zero processes remained.

**NOT verified — deliberately not driven interactively this phase:**

- Actually pressing the physical PrintScreen key to exercise the new `OnKeyUp` capture path
  (SettingsWindow's Change-hotkey flow). This port's `HotkeyManager` uses a SharpHook global
  hook (observes, never claims/consumes the key), but this machine's frozen WPF `RoeSnip` app
  uses real `RegisterHotKey` for a bare-PrintScreen binding when resident — a synthetic PrtScr
  keypress risks firing that app's actual capture flow if it were ever resident, which the hard
  rule against signalling the user's resident processes forbids. Verified instead via
  `tests/RoeSnip.App.Tests/HotkeyDisplayFormatTests.cs` plus code review against the WPF
  reference (SettingsWindow.xaml.cs:305-326) this was ported from line-for-line — same
  precedent the item-12 entry above already set for windows outside the automation pipe's
  reach.
- `WarnIfPrintScreenConflict`'s actual toast firing against a real competing app (ShareX etc.)
  running, or a genuine hotkey-registration failure — reviewed against the WPF reference
  (verbatim `KnownPrintScreenApps` list, same two-signal gate) but not staged live.
- The WM_SETTINGCHANGE broadcast's real effect (Snipping Tool releasing PrtScr mid-session
  without a logoff) — the P/Invoke call itself is reviewed against the WPF app's own
  `NativeMethods.SendMessageTimeout` usage, but exercising the consent flow's "Yes" branch live
  needs a machine where Windows is actively intercepting PrtScr, which this dev machine's
  current registry state does not reproduce on demand.
