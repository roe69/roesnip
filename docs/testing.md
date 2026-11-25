# Testing RoeSnip

## Unit tests

```sh
dotnet test RoeSnip.sln -c Release
```

| Project | Covers |
|---|---|
| `tests/RoeSnip.Core.Tests` | Shared logic: tone mapping, color math, settings, GIF encoding, sharing, updates, overlay math |
| `tests/RoeSnip.App.Tests` | Avalonia app logic: automation protocol, hotkeys, single-instance names, update manager |
| `tests/RoeSnip.Platform.Windows.Tests` | Backend registration, JPEG XR round trip, MP4 encoder |
| `tests/RoeSnip.Tests` | The WPF app's own copies of the code Core was extracted from |

`RoeSnip.Tests` and `RoeSnip.Core.Tests` share several file names on purpose: each tests its own
copy of the code (the WPF app keeps its own color, capture and imaging classes in the `RoeSnip`
namespace; Core has the `RoeSnip.Core` ones).

On a Linux box without libSkiaSharp the Core tests that decode PNGs fail, and
`SelfExtractCleanupTests.SiblingLocked` fails on Linux only.

`RoeSnip.App` multi-targets, so `dotnet run` and `dotnet publish` need `-f` (for example
`-f net8.0-windows10.0.22621.0`). On Windows the Avalonia app is a WinExe: invoke the built exe
directly, because `dotnet run` output cannot be piped or redirected.

## Platform checks that need a person

Headless runs cover capture, `--diag`, `--capture` and the automation pipe. These need a person at a
real desktop (or the issue reporter):

- Mixed-DPI multi-monitor overlays (Windows).
- Real HDR content on an HDR display.
- macOS: Screen Recording and Accessibility permission prompts, the NSPasteboard path, and anything
  on an Intel Mac or macOS 14.
- Linux: global hotkeys on KDE Plasma and Hyprland (GlobalShortcuts portal), and the Wayland
  screen-share dialog.
- Keyboard shortcuts through a real physical key (the automation pipe does not press keys).

## Driving the WPF app (`src/RoeSnip`)

Applies to the WPF app, not `RoeSnip.App` (next section). Automated agents/E2E
harnesses used to drive this app with synthetic mouse input and UIA - slow, and every drag/click
timing-dependent. The dev-gated automation channel (`src/RoeSnip/App/AutomationServer.cs`)
replaces that with a deterministic, line-based JSON control pipe that calls straight into the same
production code a real click/drag runs (button `Click` events, `RegionOutline`'s own drag-finish
bookkeeping, `OverlayWindow`'s own `SetSelection`/`OnCommand`) - never a re-implementation, and
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

Either starts the normal tray app (icon, hotkey, single-instance takeover - all unchanged) plus a
second named pipe, `RoeSnip-Automation`. A launch without the flag/env var behaves exactly as
before this feature existed.

### The `--auto` CLI client

`RoeSnip.exe --auto '<json>'` connects to the running resident's automation pipe, sends one JSON
request line, prints the response line to STDOUT, and exits 0 (response `"ok":true`) or 1
(`"ok":false`, or any failure to connect). It never touches the single-instance mutex/pipe - it
can never trigger the "normal launch replaces the running instance" takeover a bare `RoeSnip.exe`
does, so it is always safe to run alongside a live resident.

Zero-arg commands (`state`, `trigger`, `escape`) accept a shorthand - the bare command name instead
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
everywhere else in this codebase) - not DIPs, not monitor-relative, unless noted. Every command
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

`mode` is one of `idle | overlay | setup | capturing | reviewing` - `overlay` is a capture overlay
with no recording session yet (selection may or may not exist); `setup`/`capturing`/`reviewing`
are `RecordingSession`'s own three phases once Record was chosen. `selection` is `{x,y,w,h}` or
`null`. `recordingFormat`/`preset`/`estimateText`/`fps`/`fpsRange` are only non-null in
`setup`/`capturing`/`reviewing` - `estimateText` is the recording chrome's live size-estimate
readout, verbatim (e.g. `"~700 KB/s * 41 MB/min (varies with motion)"`), `preset` mirrors whichever
size chip is currently checked (`max`/`quality`/`balanced`/`compact`/`minimal` - display labels in
the chrome itself read Max/High/Medium/Low/Min, but the wire protocol keeps the original tier
names), `fps` mirrors the FPS slider's current value, and `fpsRange` is
`{"min":M,"max":N}` - the CURRENT recording format's own slider bounds (`{"min":5,"max":50}` for
GIF, `{"min":5,"max":60}` for MP4 - quality/fps expansion workstream: fps is a free integer slider
now, not four fixed chips, so `fpsRange` replaces the old `allowedFps` array) - the exact range the
`fps` command below will accept right now.

While an overlay session is up, `state` also reports `loupeRadius` (the magnifier's sample radius,
1-15; every session opens at 15 = max zoom out) and `pointerScale` (the pointer-speed multiplier the
mouse hook applies right now: `(2r+1)/31` floored at 0.1, and `1.0` whenever the loupe is not up,
e.g. once a selection exists). Both are `null` outside an overlay session.

#### `zoom`

`{"cmd":"zoom","radius":3}` (whole number 1-15) sets the loupe zoom through the same code path the
mouse wheel uses (`OverlayWindow.ApplyLoupeRadius`), so `state` then shows the matching
`loupeRadius`/`pointerScale`. Errors outside an overlay session. The zoom is per session; nothing
persists.

#### `toast` (cross-platform app only)

`{"cmd":"toast","text":"..."}` shows an info toast through `TrayApp.ShowToast`, so its placement can
be checked with a `screenshot` and the log line `toast placed at X,Y (source)`.

#### `trigger`

Opens the capture overlay - the same action as `--signal-capture` / the hotkey / the tray icon's
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
through the same code path Ctrl+A (select-all) uses - selection becomes visible and the annotation
toolbar shows, exactly as a completed drag would leave it. In `setup`/`capturing`/`reviewing` mode
(any phase of a recording session): applies the rect to `RegionOutline` through the exact band-drag
code path, so `RegionChanged` -> `RecordingSession.OnRegionMoved` -> the chrome's `UpdateSelection`
all fire, same as a real drag - including, once a take exists (`capturing`/`reviewing`), the
size-locked MOVE-ONLY semantics a real drag has then (`w`/`h` in the request are ignored; only
`x`/`y` - the desired top-left - are honored, and the recorder's own crop follows, same as dragging
the band with the mouse). Multi-monitor recording (drag handoff): a `select` that moves the region
so it's no longer fully contained in its current monitor's bounds during `capturing`/`reviewing`
triggers the same cross-monitor handoff a real drag across that boundary would - see "Cross-monitor
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

`{"cmd":"preset","tier":"balanced"}` - one of `max`/`quality`/`balanced`/`compact`/`minimal`.

Only valid in `setup` (the size row is locked once a take exists, same as the real chrome). Clicks
the matching size chip (`RecordingChrome.InvokeSizePreset`, which raises the chip's real `Click`
event) and persists it, same as a mouse click. Errors with the current phase if not in `setup`.
`minimal` is the quality/fps expansion workstream's fifth, most aggressive tier (GIF: a 16-color
palette, a large lossy run-extension threshold, and a half-resolution render; MP4: the lowest
bitrate tier) - see GifSizePresets.ForPreset's own doc comment for the exact calibrated numbers.

#### `fps`

`{"cmd":"fps","value":20}` - any integer from 5 up to the CURRENT recording format's own ceiling
(GIF: 5-50; MP4: 5-60 - see `state`'s own `fpsRange` doc above). Quality/fps expansion workstream:
fps is a free integer slider now, not four fixed chips per format - GIF's ceiling is still a HARD
format limit (GIF89a's delay field is whole centiseconds and every real decoder clamps a 0-1cs
delay to 10cs, so 2cs/50fps is the fastest legal frame time), but every integer up to that ceiling
is legal now, not just divisors of 100: the encoder's patch-behind delay scheme
(`GifEncoder.PatchLastDelay`) carries the sub-centisecond rounding remainder forward frame to
frame, so a non-divisor rate (e.g. 37fps) still averages out to EXACTLY the requested speed over a
long take instead of silently drifting.

Only valid in `setup`, same as `preset` above. Sets the FPS slider to this value
(`RecordingChrome.InvokeFps`, which moves the same slider a real drag would and persists
immediately rather than waiting out the slider's drag-debounce window - see that method's own doc
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

`{"cmd":"chrome","action":"start"}` - one of `start`/`stop`/`save`/`copy`/`share`/`cancel`/`pause`/
`resume`/`another`/`done`/`copyagain`/`saveagain`/`retryupload`.

Raises the same button `Click` event a real mouse click on that chrome button would - `start`/
`stop` share one button exactly like the real UI (`start` valid only in `setup`, `stop` only in
`capturing`). `pause` requires `capturing` and not already paused; `resume` requires already
paused (valid from `capturing` or `reviewing` - a soft-stopped take is a paused take, see
`RecordingSession.Resume`'s own doc comment); `save` requires `reviewing`; `cancel` is valid in any
phase. An action invalid for the current phase errors with that phase instead of silently no-op'ing
the way a disabled button would.

`copy` requires `reviewing` and errors if a save/share/copy is already running. It hard-stops the
pipeline (same finalize `save` uses), moves the finished take into the clipboard staging directory
(`%TEMP%\RoeSnip\clipboard`, pruned after a day - the clipboard carries a PATH, so the file has to
outlive the session) and puts it on the clipboard as CF_HDROP, i.e. exactly what copying the file in
Explorer produces. Verify it from PowerShell with
`[System.Windows.Forms.Clipboard]::GetFileDropList()`, and clear the clipboard first so a stale
entry can't masquerade as a fresh copy. The same thing happens on Ctrl+C while `reviewing`
(`ReviewCopyHook`, a WH_KEYBOARD_LL hook because the chrome never takes focus) - that path is NOT
reachable through this pipe, so testing it needs a real synthetic keystroke.

`another`/`done` answer the "Record another?" prompt that `save`/`copy`/`share` now park on instead
of silently re-arming: `another` re-arms into `setup` with the same region (PrtScr does the same),
`done` tears the session down. Both error unless a finished take is actually waiting on a choice.

`copyagain`/`saveagain` are the same prompt's other two buttons, added because the clipboard is
shared state and a finished take used to be unreachable the moment anything else copied. `copyagain`
puts the take's file back on the clipboard (same CF_HDROP as `copy`; verify it the same way);
`saveagain` COPIES it to a path from the save dialog - or, under `ROESNIP_RECORD_AUTOSAVE`, straight
into that directory, which is how to drive it without UIA against a native file picker. Neither
dismisses the prompt; both re-word it to name what they just did. Both error unless a finished take
is waiting on a choice AND it still has a file to act on (a SHARED take does not - the upload's
success callback deletes the only copy).

When a shared take's upload fails (or is cancelled) while its prompt is still up, the prompt turns
into the upload-failed form: the error, then Retry upload, Copy, Save as... and Show in folder. The
temp take is kept until an upload succeeds; Done or Record another from there leave it in place and
raise a tray error naming its path. `retryupload` presses Retry upload and errors unless that form
is showing. Copy there stages a COPY in the clipboard directory, so a later successful retry
(which deletes the temp take) cannot break the paste. The toast offers the same Retry, plus Show in
folder for a recording; a toolbar still share retries from the PNG bytes it still holds. To force a
failure, point the RoeShare provider at an unreachable server URL. Every attempt, retry and failure
is in `roesnip.log` ("share upload ..." lines).

`share` (Sharing/* subsystem, added alongside the toolbar's own Share wiring) requires `reviewing`
and errors if a save or share is already in progress. It hard-stops the still-alive pipeline (same
finalize Save uses), uploads the temp file to the configured default share provider WITHOUT moving
it, then re-arms into `setup` immediately - the upload itself is handed to `ShareFlowPresenter`,
which shows its own small `ShareResultWindow` toast (Uploading -> Success/Failure) independent of
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
on-screen window - useful for verifying the "this area will be recorded" frame is where it should
be. The overlay/chrome/flash windows ARE `WDA_EXCLUDEFROMCAPTURE` and so are invisible to a plain
screenshot; pass `"includeExcluded":true` to temporarily clear that affinity on every one of this
process's own currently-excluded windows, capture, then restore it. **Caveat:** the affinity
change and the capture are not atomic with the compositor - a window mid-repaint at the exact
capture instant could show a stale or partially composited frame for that one screenshot. Accepted
for an automation/E2E screenshot; do not rely on `includeExcluded` for pixel-perfect verification of
transient animation frames.

```json
{"ok":true,"path":"C:\\temp\\shot.png","width":1920,"height":1080}
```

Only the path/width/height are returned - never the image bytes (hard 64k response cap; one file
write per response).

#### `confirm`

`{"cmd":"confirm","action":"copy"}` or `{"cmd":"confirm","action":"save","path":"C:\\temp\\out.png"}`

Multimon-selection addition: the overlay's Copy/Save had no automation entry point at all before
this - the toolbar's Copy button raises the same `OverlayCommand.Copy` this does, but Save's real
path (`TryShowSaveDialog`) pops an interactive `SaveFileDialog`, which must never happen from a
headless automation call (it would hang the pipe waiting for a human). `action: "save"` therefore
**requires an explicit `path`** and writes directly via `PngWriter`, skipping the dialog entirely  - 
the same production render (`RenderSelectionWithAnnotations`, or `RenderSpanningSelection` for a
selection spanning multiple monitors) either path already uses. Requires an active overlay session
with a selection; errors otherwise. HDR export (`SaveHdrRequested`) is not reachable through this
command - use the toolbar's Save right-click menu for that on a single-monitor selection (never
available for a spanning one; see the cross-monitor selection section below).

```
--auto '{"cmd":"confirm","action":"copy"}'
```
```json
{"ok":true,"mode":"idle", ...}
```

Closes the overlay on success, same as a real Copy/Save click - the trailing `state` snapshot
reflects the post-close (`idle`) state, not the selection that was just confirmed.

`action: "share"` (Sharing/* subsystem, wired in the same integration pass as the chrome `share`
action above) raises the same `OverlayCommand.Share` the toolbar's Share button raises: renders the
current selection through the exact path Copy uses (including a spanning stitch), hands it to
`ShareFlowPresenter`, and closes the overlay IMMEDIATELY, same as `copy`/`save` (the trailing `state`
snapshot reflects the post-close state, not `overlay`). The upload itself runs detached; its
progress/result is reported via the presenter's own `ShareResultWindow` toast - a successful URL is
copied to the clipboard automatically, a failed/cancelled upload names the kept file/error in the
toast. Requires a selection and at least one enabled share provider (Settings > Sharing).

#### `settings`

`{"cmd":"settings","action":"open"}` or `{"cmd":"settings","action":"close"}`

Settings-legibility-pass addition: opens or closes the Settings window over the SAME
single-instance path the tray icon's "Settings..." item / a second tray click use
(`TrayApp.OpenSettings`/`TrayApp._settingsWindow`), rather than constructing a second window - so a
`screenshot` command issued right after `settings open` captures the exact window a human would see.
`"open"` is idempotent (activates the existing window instead of stacking a second one, exactly like
a second tray click) and never errors. `"close"` errors if no settings window is currently open.
Available in any `mode` - the settings window has no effect on overlay/recording state, so the
trailing `state` snapshot's `mode` just reflects whatever it already was.

```
--auto '{"cmd":"settings","action":"open"}'
```
```json
{"ok":true,"mode":"idle", ...}
```
```
--auto '{"cmd":"settings","action":"close"}'
```
```json
{"ok":true,"mode":"idle", ...}
```

#### `lastcapture`

`{"cmd":"lastcapture","action":"status"}`, `{"cmd":"lastcapture","action":"copy"}`, or
`{"cmd":"lastcapture","action":"save"}` (optionally with a `"path"`).

The still capture the resident is holding on to after the overlay closed
(`RoeSnip.Core/Clipboard/LastCapture.cs`) - the same two actions the tray menu's "Copy again" and
"Save last capture" items perform, through the same `TrayApp` methods. A still has no post-capture
prompt the way a recording does (the overlay IS its post-capture UI, and confirming closes it), so
this is how a capture stays reachable once something else has taken the clipboard.

`status` is a plain read and answers `"kept":null` when nothing is held - that is an answer, not an
error. `copy` puts the kept pixels back on the clipboard in both formats the original copy used
(PNG + CF_DIBV5; verify with `[System.Windows.Forms.Clipboard]::GetImage()`, after clobbering the
clipboard first so a stale entry cannot masquerade as a fresh copy). `save` writes a PNG: to
`"path"` when one is given, else into the configured save directory under the capture's own
`roesnip_yyyyMMdd_HHmmss.png` name, never overwriting an earlier save of the same capture (`_2`,
`_3`, ...). `copy`/`save` error when nothing is being kept.

```
--auto '{"cmd":"lastcapture","action":"copy"}'
```
```json
{"ok":true,"kept":{"width":640,"height":400,"takenAt":"2026-09-20 18:44:43"},"savedPath":null}
```

The kept capture lives in memory only - one at a time, replaced by the next capture and gone when
the process exits - so `status` reports nothing after a restart.

#### `tray`

`{"cmd":"tray","action":"menu"}` or `{"cmd":"tray","action":"closemenu"}`

Drops the REAL tray context menu (the same `ContextMenuStrip` a right-click opens) at the tray
corner so a `screenshot` can photograph it, with no synthetic mouse input - the same visual-QA
purpose `settings open` serves for the Settings window. Nothing dismisses it on its own (there is
no foreground window to lose focus to), so `closemenu` is how it goes away; `closemenu` errors if
no menu is open. `RoeSnip.App` validates this command for wire-shape parity but has no live
handler and never will: its tray menu is a native menu the OS owns.

### Cross-monitor selection (`select` spanning multiple monitors)

`select`'s rect can cross a monitor boundary - e.g. on a 3-monitor layout with `DISPLAY3` at
`(0,0)-(2560,1440)` and `DISPLAY1` at `(0,-1440)-(2560,0)` directly above it,
`{"cmd":"select","x":600,"y":-200,"w":800,"h":500}` selects a rect that's part `DISPLAY1`, part
`DISPLAY3`. This routes through the exact same distribute mechanism a real mouse drag crossing that
same boundary uses (`OverlaySession.OnSpanningCandidate`) - never a separate implementation. `state`'s
`selection` reports the union rect in the same virtual-desktop physical pixels as always; nothing
about the wire shape changes for a spanning rect.

Annotations are off for a spanning selection (see [architecture.md](architecture.md)).
`confirm`'s `copy`/`save` actions both work (a byte composite stitched from each intersected
monitor's already tone-mapped crop; gaps where no monitor covers part of the rect are opaque black),
and so do Record and HDR save. `record` hands the spanning virtual rect to `RecordingSession`, whose
`BeginCapture` re-derives the intersected monitor set and takes the spanning capture path
(`SpanningCanvasCompositor`/`EncoderLoopSpanning`) once it has 2+ monitors.

Trap: `OverlayInputInterop`'s low-level keyboard hook does not filter injected input
(`LLKHF_INJECTED`). On a busy desktop a synthetic Escape from another program can reach it while an
automation call does UI work and cancel the session; a passive `state` poll is unaffected.

### Cross-monitor recording (drag a live take across a monitor boundary)

Distinct from cross-monitor SELECTION above, which is about the one-shot capture/annotate flow. This
is about a REGION THAT'S ALREADY RECORDING (`capturing` or `reviewing`) being dragged (or `select`ed
 -  see the `select` command's own doc above) across a monitor boundary mid-take.

Phase 1 is "snap, not spanning": the recorded content is never a stitched composite of two
monitors at once. The instant the region's bounding rect would stop
being fully contained in its current monitor, RoeSnip tears down the `RegionRecorder` on the monitor
it's leaving and builds a fresh one on the monitor now holding the majority of the region - a brief
"pop" in the recording (a few dropped frames while the new WGC session spins up; VFR timestamps and
GIF's own patch-behind carry absorb the gap without any visible time distortion) followed by content
from the NEW monitor. The encoder's canvas dimensions never change (only the position moves), and the
tone-map exposure is RECOMPUTED fresh for the destination monitor rather than carried over or
blended - a visible brightness/color shift across the handoff on two differently-calibrated monitors
is the expected, photometrically-correct behavior, not a bug.

`select` during `capturing`/`reviewing` is a size-locked MOVE (see that command's own doc above)  - 
this is the production path automation drives a handoff through, exactly mirroring what dragging the
region's band with the mouse does. A single `select` call that jumps straight onto a different
monitor (rather than a smooth incremental drag) triggers exactly one handoff, landing on whichever
monitor the FINAL rect mostly overlaps - intermediate monitors the jump would have crossed are never
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
:: That factor includes Compact's 0.75 render scale: the lossy-run threshold is limited to what
:: smooth gradient/photo content tolerates, which leaves Compact's byte ratio (0.865) nearly the
:: same as Balanced's, so the Low tier gets its real size step from a 0.75-resolution render
:: instead - text stays legible, just softer.
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

## Driving the Avalonia app (`src/RoeSnip.App`)

Applies to the Avalonia app, not the WPF app above. Same idea as the WPF app's own automation channel - a deterministic, line-based JSON
control pipe that calls straight into the same production code a real click/drag runs
(`OverlayWindow`'s own `SetSelection`/`OnCommand` via the `*ForAutomation` hooks on
`OverlayController`/`OverlayWindow`), never a re-implementation, never simulated input - ported
into `src/RoeSnip.App/AppShell/AutomationServer.cs`, ONLY covering what this port currently has:
the overlay (capture/select/copy/save), not recording. `NamedPipeServerStream`/
`NamedPipeClientStream` map to Unix domain sockets on macOS/Linux, so the whole channel is
portable - nothing here is Windows-only.

**Distinct pipe name from the WPF app on purpose**: `RoeSnip.App-Automation`, not
`RoeSnip-Automation` - both apps' residents run side by side on this machine (same rationale as
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

(`RoeSnip.exe` here is the Avalonia app's own binary - same `AssemblyName` as the WPF app, so
make sure you're launching the one you mean; `src/RoeSnip.App/bin/<config>/<tfm>/RoeSnip.exe`.)
`AppComposition.RunTray`'s hidden-flag allowlist accepts `--automation` specifically (mirroring
the WPF app's `TrayApp.Run`); any other unrecognized argument still prints usage and exits 1.

### The `--auto` CLI client

`RoeSnip.exe --auto '<json>'` connects to the running resident's automation pipe, sends one JSON
request line, prints the response line to STDOUT, and exits 0 (`"ok":true`) or 1 (`"ok":false`, or
any failure to connect). Intercepted in `Program.Main` before any single-instance machinery, so it
never touches `AppShell/SingleInstance.cs`'s mutex/pipe - always safe to run alongside a live
resident.

Zero-arg commands (`state`, `trigger`, `escape`) accept the same shorthand as the WPF app:

```
RoeSnip.exe --auto state
RoeSnip.exe --auto '{"cmd":"state"}'      # equivalent
```

### Commands implemented in this port

Same wire shape as the WPF app's `state` response (see that section above for the full field
reference) - `mode` is `idle`/`overlay` while no recording is active, and `setup`/`capturing`/
`reviewing` (with `recordingFormat`/`preset`/`estimateText`/`fps`/`fpsRange` populated) while one is
 -  see the recording section below.

- **`state`** - current mode/selection/monitor list.
- **`trigger`** - opens the capture overlay (same action as the tray icon / hotkey / the bare
  `capture` CLI verb / a second-instance signal). Blocks (up to ~5s) until the overlay is
  actually up.
- **`select`** - `{"cmd":"select","x":100,"y":100,"w":400,"h":300}`. Sets the selection on
  whichever monitor contains the rect's top-left corner, through the exact same
  `SetSelection`/clamp/too-small-cancel path a real mouse-up finishing a drag takes. Errors if
  idle (nothing to select - use `trigger` first).
- **`escape`** - cancels the open overlay (if any) and returns to `idle`. A no-op
  (`"ok":true`) if already idle.
- **`screenshot`** - `{"cmd":"screenshot","path":"C:\\temp\\shot.png","rect":{...}}`. Captures via
  this app's OWN `CaptureService`/`SdrImage`/`PngWriter` pipeline (the same one `--capture` and
  the interactive overlay use), not a raw desktop grab - fully portable, but means an explicit
  `rect` must fall within a SINGLE monitor's bounds (its top-left corner picks the monitor; the
  rect is then clamped to that monitor's captured frame). Omit `rect` to capture the primary
  monitor's full bounds. `"includeExcluded":true` temporarily clears
  `WDA_EXCLUDEFROMCAPTURE` on this process's own windows (`WindowCaptureExclusion.ClearOnOwnWindows`/`Restore`) for the duration of the capture, same
  caveat as the WPF app's version (the affinity change and the capture aren't atomic with the
  compositor). Only path/width/height are returned, never image bytes.
- **`confirm`** - `{"cmd":"confirm","action":"copy"}` or
  `{"cmd":"confirm","action":"save","path":"C:\\temp\\out.png"}` or
  `{"cmd":"confirm","action":"share"}`. `"save"` **requires an explicit `path`** and writes
  directly via `PngWriter`, never popping the interactive save picker (which would hang the pipe
  waiting for a human) - same production render (`RenderSelectionWithAnnotations`) either path
  already uses. `"share"` (Sharing subsystem) uploads to the configured default
  provider through the exact same `OverlayCommand.Share` path the toolbar's Share button raises  - 
  same as `copy`/`save` it closes the overlay immediately (the response's `mode` reflects the
  post-close state, not `"overlay"`); the upload's own progress/result - success or failure  - 
  is reported via a `ShareResultWindow` toast, same as a real click. Requires an active overlay
  session with a selection; errors otherwise.
- **`settings`** - `{"cmd":"settings","action":"open"}` or `{"cmd":"settings","action":"close"}`.
  Settings-legibility-pass addition: opens/closes the Settings window over the same single-instance
  path the tray icon's "Settings..." item uses (`TrayApp.OpenSettings`/`_openSettingsWindow`) rather
  than constructing a second window, so a `screenshot` right after `settings open` captures the
  exact window a human would see. `"open"` is idempotent (activates the existing window, never
  errors); `"close"` errors if none is open. Available in any `mode`.

### `record` / `preset` / `fps` / `chrome` (recording)

Live, same wire shape as the WPF app's own protocol section above (`mode` becomes `setup`/
`capturing`/`reviewing` while a recording is active; `recordingFormat`/`preset`/`estimateText`/
`fps`/`fpsRange` populate accordingly):

- **`record`** - `{"cmd":"record","format":"gif"}` or `"mp4"`. Closes the overlay (same as a
  toolbar Record menu pick) and opens a recording session in `setup`. Requires an active overlay
  session with a selection; a multi-monitor (spanning) selection errors instead of silently
  recording one monitor (spanning recording is not ported - single-monitor takes only).
- **`preset`** / **`fps`** - Setup-only, same tier/range validation as the WPF wire contract.
- **`chrome`** - `{"cmd":"chrome","action":"start"}` (also `stop`/`pause`/`resume`/`save`/`share`/
  `cancel`). Drives the exact same button handler a real chrome click would; an action invalid for
  the current phase errors instead of silently no-opping. `save` and `saveagain` take an optional
  `"path"` (`{"cmd":"chrome","action":"save","path":"/tmp/take.mp4"}`): the take is written there
  with no dialog, and a failed write comes back `ok:false` (the take is kept). Without `path` they
  click the real button, which opens the save dialog (Avalonia StorageProvider: Win32 dialog, xdg
  FileChooser portal, NSSavePanel) unless `ROESNIP_RECORD_AUTOSAVE` is set on the resident. Cancel
  in the dialog leaves the take in `reviewing` (log: `recording save dialog cancelled; the take
  stays in review`); `save` while the dialog is open errors. `share` with no configured provider is a fail-closed no-op (the take stays
  exactly as it was, still soft-stopped, still Resumable) - see RecordingOrchestrator.RequestShare's
  own doc comment for the full contract (ported verbatim from the WPF reference's 422e87a fix).
- **`select`** while a recording is active moves/resizes the (Windows-only) RegionOutline instead of
  the overlay selection - size-locked (move-only) once Capturing/Reviewing, same as a real band drag.
- **PrtScr / `trigger` while a recording is active**: TrayApp.TriggerCapture checks
  `RecordingOrchestrator.IsActive` BEFORE its own overlay-trigger bookkeeping - a `trigger` call (the
  same entry point a real PrtScr press uses) advances the chrome state machine instead of opening a
  new overlay: `setup` -> BeginCapture, `capturing` -> StopCaptureToReview, `reviewing` -> Save. This is
  the actual, most-representative way to E2E the PrtScr contract via the automation pipe (rather than
  the `chrome` command, which drives the same handlers but not through TriggerCapture's own dispatch).

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
