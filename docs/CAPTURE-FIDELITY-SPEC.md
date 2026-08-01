# Capture-fidelity implementation spec (2026-08-02)

> **Committed retroactively.** This was the working planning doc for the 2026-08-02 capture-fidelity
> pass; ~13 doc comments across both apps (FlashDimmer.cs, Magnifier.cs, SelectionAdorner.cs,
> OverlayWindow xaml.cs, the capture backends, both test files) and docs/PARITY.md cite it by name/
> item number, but it was never actually committed to the repo until now — every one of those
> citations was a dangling reference. Note item 1's claim that "Avalonia has no flash-dimmer
> subsystem at all" is WRONG and was corrected in docs/PARITY.md shortly after this was written —
> Avalonia does have one (`src/RoeSnip.App/Overlay/FlashDimmer.cs`, ported near-verbatim) and it got
> the identical foreground-claim removal in a follow-up pass. Treat this file as the historical
> record of the reasoning, not as a live source of truth for current app-parity state — PARITY.md is
> the up-to-date one.

Source report: user says the app can't see DRM/tooltips/"some random things"; the cursor seems to
"become unavailable" before the freeze, dismissing tooltips before they're captured; the loupe
should preview selection borders while dragging. This spec resolves all three plus states the DRM
non-fix plainly. Verified directly against current code (paths/line numbers below), not just the
three prior investigation reports.

---

## 1. CAPTURE ORDERING

### Decision: **(b′)** — keep the dim exactly where it is; delete the flash's own foreground claim
instead of "deferring" it. Do not move `CaptureAll()` earlier, and do not touch `SWP_NOACTIVATE`
window positioning at all.

### Why the mechanism is what it is

`TrayApp.TriggerCapture` (`src/RoeSnip/App/TrayApp.cs:381-415`) is already, provably, ordered:
`MarkTriggerTimestamp()` → synchronous `OverlayController.TryShowFlash(monitors)` (logs
`hotkey-to-dim`) → fire-and-forget `RunCaptureFlowAsync` (which is where `CaptureAll()` eventually
runs, `Program.cs:635`). `TryShowFlash` → `FlashDimmer.ShowAll` (`FlashDimmer.cs:189-294`) does two
distinct things and they are NOT equally responsible for the bug:

1. **Position the dim windows topmost**, via `SetWindowPos(..., SWP_NOACTIVATE)`
   (`FlashWindow.Position()` / `ShowOnMonitor()`). `SWP_NOACTIVATE` is defined by Win32 specifically
   to *not* activate the window — it does not send `WM_ACTIVATE`/`WM_ACTIVATEAPP` to the previously
   active app, and mouse hit-testing (which is what makes the input-swallow work — clicks go to
   whatever's topmost under the cursor by z-order, not to whatever has focus) does not depend on
   foreground either. **This step is not what dismisses tooltips.**
2. **Fire a background `SetForegroundWindow(hwnd)`** (`FlashDimmer.cs:266-286`, `Task.Run`,
   unsynchronized by the class's own doc comment at lines 69-88). `SetForegroundWindow` is the
   actual Win32 mechanism that reassigns foreground activation away from whatever app the user was
   in — the standard trigger `comctl32` tooltips and most custom hover-popups use to self-dismiss.
   **This is the concrete cause.**

Critically, re-reading the code turned up something the prior investigations under-weighted: **this
foreground claim is no longer load-bearing for anything it originally existed for.**
- Esc-during-flash is covered by `FlashEscapeHook`, a global `WH_KEYBOARD_LL` hook installed by
  `TryShowFlash` (`OverlayController.cs:240`) — it fires regardless of focus/foreground. The class's
  own comment says so explicitly: *"With the capture off-thread, flash-phase Esc no longer DEPENDS
  on this claim winning"* (`FlashDimmer.cs:264-265`).
- Click-swallowing needs topmost + non-click-through, not foreground (see point 1 above).
- The **real** session, once it actually opens post-capture, already does its own robust foreground
  claim via `ForegroundActivator.Activate(_activeWindow, "session-start")`
  (`OverlayController.cs:786`) — a proper 3-tier ladder (`Activate()` → `SetForegroundWindow` →
  `AttachThreadInput` ladder → `SendInput` Alt-tap unlock) that is strictly more robust than the
  flash's single best-effort call. This call already happens **after** `CaptureAll()` has returned
  frames, because the overlay can't open before that.

So the flash-phase `SetForegroundWindow` call is a *vestigial second, weaker, earlier* foreground
claim whose only remaining effect is the tooltip-dismissal side effect the user is reporting. It was
never counted in any latency number either: `hotkey-to-dim` is measured by `flashWatch` around the
synchronous part of `TryShowFlash` (`TrayApp.cs:395-401`), and the foreground call is already
fire-and-forget on a `Task.Run` queued *before* that stopwatch is read — so it was never on the
clock. Removing it costs **zero** on every number this codebase already logs:
`hotkey-to-dim`, `capture-to-overlay`, and the cold/post-idle first-trigger case are all unaffected,
because none of them time this call and the dim itself doesn't move.

### Exact change

**`src/RoeSnip/Overlay/FlashDimmer.cs`, `ShowAll` (lines ~250-288):** delete the
`if (first is not null) { ... Task.Run(() => ... SetForegroundWindow(hwnd)); }` block entirely (the
`WindowInteropHelper` HWND fetch, the epoch snapshot, and the `Task.Run`). Keep everything before it
(positioning/presentation) and after it (`Dispatcher.CurrentDispatcher.Invoke(..., Loaded)`,
`ArmWatchdog()`) unchanged.

**Leave as defensive dead-code, do not delete:** `s_foregroundClaimEpoch`,
`InvalidateForegroundClaim`, `s_foregroundBeforeClaim`, `TryRestoreForegroundFromFlash`. With no
claim ever queued, `TryRestoreForegroundFromFlash`'s `foregroundIsFlash` check will simply never be
true in the normal case, so `ReleaseFlash` (`OverlayController.cs:267-288`) and `OnFlashEscape`
(`OverlayController.cs:294-308`) keep calling it — it becomes a no-op safety net rather than a
load-bearing restore, which is strictly safer than removing it (cheap insurance against some future
change re-introducing a claim).

**Doc-comment updates required** (so the next reader doesn't reintroduce this bug):
- `FlashDimmer.cs:30-35` (input policy doc): note that swallowing is achieved by topmost +
  non-click-through hit-testing, not by holding foreground.
- `FlashDimmer.cs:69-88` (`s_foregroundClaimEpoch` doc): note the flash phase no longer stakes a
  foreground claim at all — the epoch/restore machinery is retained only as a safety net for
  `TryRestoreForegroundFromFlash`'s no-op path.
- `FlashDimmer.cs:250-266` (the block being deleted): replace with a short comment explaining the
  removal and pointing at `OverlayController.cs:786`'s session-start `ForegroundActivator.Activate`
  as the one and only foreground claim in the whole flow, and that it already runs after
  `CaptureAll()` returns.
- `TrayApp.cs:383-391` (flash purpose doc): add one sentence — the flash no longer touches OS
  foreground, so anything visible/interactive at hotkey-press time (tooltips, hover menus) survives
  until the frame is actually read.

### User-visible timing, stated in numbers

| | before | after |
|---|---|---|
| hotkey-to-dim (warm) | 3-7 ms | **unchanged, 3-7 ms** — the deleted call was never on this clock |
| hotkey-to-dim (cold/first trigger) | ~65-90 ms | **unchanged** — same reason |
| capture-to-overlay | ~50-60 ms warm, up to ~600 ms-1 s cold GPU-wake | **unchanged** — capture still starts at the same instant, still thread-pooled, still covered by the dim |
| foreground-claim latency (previously invisible to the user, now removed) | best-effort, could take up to ~60 ms in the background, occasionally raced the overlay's own activation | **eliminated** — one fewer moving part; the session-start ladder is the only claim now |
| tooltip / hover-popup survival | destroyed by the background `SetForegroundWindow` racing ahead of `CaptureAll()` reading pixels | **preserved** — no `WM_ACTIVATE`/`WM_ACTIVATEAPP` fires anywhere between hotkey press and the frame being read |

This is a rare case where the fix is a straight improvement with no honest trade-off to disclose —
unlike options (a) (capture-before-dim, which *would* regress hotkey-to-dim by exposing the full
capture-to-overlay stretch as a live undimmed screen) or (c) (issue capture before presenting the
dim, which is architecturally the same regression as (a) since presenting the dim IS the visible
side effect being raced against). Both (a) and (c) are rejected because they reopen the exact
50-60 ms (warm) / 600 ms-1 s (cold) window the flash exists to cover, for no additional benefit —
the actual bug was never the dim, only the foreground claim riding on top of it.

### Failure paths — confirm nothing regresses

- **Capture deadline** (`Program.cs:644-667`, `CaptureDeadline`): unaffected. The deadline race is
  between `captureTask` and `Task.Delay`; nothing about the foreground-claim removal touches this.
  `ClearPendingOverlayTrigger` and the abandoned-task disposal continuation are untouched.
- **Capture failed on every monitor** (`Program.cs:671-679`, `frames.Count == 0`): unaffected, same
  reasoning.
- **Esc during the flash phase**: still fully covered — `FlashEscapeHook` is a global low-level
  keyboard hook independent of focus/foreground (`OverlayController.cs:240`), untouched by this
  change. `OnFlashEscape` (`OverlayController.cs:294-308`) still hides the flash and (now harmlessly)
  calls `TryRestoreForegroundFromFlash`.
- **Flash Esc hook lifetime**: untouched — still installed in `TryShowFlash`, disposed in
  `DisposeFlashEscapeHook`, called from `ReleaseFlash` / `OnFlashEscape` / the real session's ctor,
  exactly as today (`OverlayController.cs:251-261`). No change touches this file.
- **Gate busy / RunOverlay unavailable**: both already call `ClearPendingOverlayTrigger` and return
  before any session exists; `ReleaseFlash`'s now-no-op restore call is harmless.

### Constraint check
No new setting. `ROESNIP_NO_FLASH=1` remains as-is (untouched escape hatch). Both apps: **WPF only**
— the Avalonia port (`src/RoeSnip.App`) does not have a `FlashDimmer`/flash-dim system at all (it has
no equivalent instant-dim r5-latency subsystem), so there is nothing to port for item 1; record that
explicitly in `docs/PARITY.md` rather than silently leaving an asymmetry unexplained.

---

## 2. CURSOR

### Decision: no functional change. State the truth; make it undeniable in the docs so nobody
"re-fixes" a non-bug later.

Verified: a repo-wide check of the hotkey/flash/capture path shows no `Cursor.Hide`, `ShowCursor`,
`ClipCursor`, or `SetCapture`/`SetCursorPos` anywhere in it (the only `SetCapture`/`ReleaseCapture`
usage in the app is `Recording/RegionOutline.cs:191,224`, unrelated). `FlashWindow`'s constructor
does set `Cursor = Cursors.Cross` the instant the window is shown (`FlashDimmer.cs`, `FlashWindow`
ctor) — that is a **glyph swap**, not a hide, and it is deliberate (it's the same crosshair the real
overlay's own select-tool cursor uses, signaling "snip mode is active").

What the user is actually perceiving, precisely:
1. The OS cursor's *glyph* instantly becomes a crosshair the moment the flash windows go topmost
   (same instant as the dim — both are part of `ShowOnMonitor`/window presentation).
2. Because those windows are topmost and swallow all input (by design, not a defect — see item 1
   above), every click/hover the cursor makes from that instant on stops reaching whatever
   app/control it's physically over. That reads, subjectively, as "the cursor stopped working,"
   even though Windows' actual mouse pointer never moved, hid, or was captured.
3. Separately (and unrelated to #1/#2): **neither capture backend has ever included the OS cursor in
   a screenshot.** `WgcCapturer.cs:529` sets `session.IsCursorCaptureEnabled = false` explicitly;
   Desktop Duplication never reads DXGI's separate pointer-shape buffer at all
   (`DesktopDuplicationCapturer.cs`). This is intentional and asymmetric with recordings on purpose
   (`WindowsRegionCaptureSource.cs:136`, `RegionRecorder.cs:152` both set the recording path's
   cursor capture **true**, with an explicit "the recording should show the cursor" comment) — clean,
   cursor-free screenshots is the stated design (`PARITY.md`). This is a shipped product decision,
   not something this pass should change; flagging it here only so the orchestrator can tell the
   user honestly that captured stills never showing a cursor is not a bug and not new.

### Smallest change that makes the real behavior obvious
No functional/behavioral change (there is nothing to fix). Two documentation-only edits so the next
person reading this code doesn't mistake the crosshair-swap-plus-swallow for a cursor-hide bug and
"fix" something that isn't broken:
- `FlashWindow` ctor's `Cursor = Cursors.Cross` line: add a one-line comment — "glyph swap only;
  Windows' actual cursor is never hidden/clipped/captured — the perceived 'cursor unavailable'
  effect is this glyph change plus total input-swallow by the topmost window, see item 2 of
  CAPTURE-FIDELITY-SPEC.md."
- `WgcCapturer.cs:529`/`DesktopDuplicationCapturer.cs`: add a one-line cross-reference comment
  noting screenshots deliberately exclude the cursor (contrast recordings), so a future "why is the
  cursor missing from my screenshot" report is answered in five seconds instead of re-investigated.

---

## 3. LOUPE SELECTION BORDERS (full drawing spec, both apps)

Re-verified directly: `Magnifier.cs` (`OnRender`, `src/RoeSnip/Overlay/Magnifier.cs:135-241`) samples
`_sampleX`/`_sampleY` (clamped physical-pixel cursor position, set by `Update()`) and draws each
swatch at `swatchX = loupeLeft + (dx + _sampleRadius) * swatchDip`, `swatchY` analogous
(`Magnifier.cs:219-220`) — confirmed exact formula to reuse. `SelectionAdorner.cs:50-54` confirmed:
WPF's real on-screen crop border uses two **private static readonly** `Pen`s,
`BorderUnderPen` (dark understroke, 1.0 DIP, solid) and `BorderDashPen` (light dashed, 1.0 DIP,
`Color.FromArgb(0xFF,0xDC,0xDC,0xE0)`, dash `[3,3]`) — these must widen to `internal` so `Magnifier`
can reuse the identical frozen instances. `OverlayWindow.xaml.cs` confirmed: `SetSelection`
(`:1553-1564`) is the single place `_selectionPx`/`Adorner.SelectionPx` are written for every drag
kind (`NewSelection` distribute, `Move`, `Resize`, spanning variants, clear); the mouse-move handler
(`OnPreviewMouseMove`) calls `MagnifierControl.Update(...)` at `:1109-1114` — **before** the
`switch (_dragMode)` block that may flip `_newSelectionPending` and call `SetSelection` — but because
WPF's `OnRender` only actually runs on a later render pass (after this whole synchronous handler
returns), reading `_selectionPx`/a `ShowSelectionPreview` flag from `OnRender` is always current
*if* the flag itself is written **after** the switch, not merged into the earlier `Update()` call.
The switch statement ends at line 1236, immediately before the method's closing brace — that is the
correct write site.

### What is drawn
Inside `Magnifier.OnRender`/`Render`, between the swatch-grid loop and the crosshair block (crosshair
must stay the topmost/last-drawn element — it's the pixel-precision indicator and must never be
occluded by the new border stroke):
1. Read `SelectionPx` (new property, see below); if null, draw nothing extra.
2. Normalize it, then map all four edges through the *same* affine the swatches use:
   `MapX(int physX) = loupeLeft + (physX - cx + _sampleRadius) * swatchDip`, `MapY` analogous with
   `cy`/`loupeTop`. `RectPhysical`'s `Left/Top/Right/Bottom` are boundary coordinates (Right/Bottom
   one-past-last-pixel, confirmed at `src/RoeSnip.Core/Capture/RectPhysical.cs:7-16`), which maps
   exactly onto the swatch-index formula with no half-pixel correction.
3. `PushClip` to exactly the loupe's own square (`new Rect(loupeLeft, loupeTop, loupeSize, loupeSize)`
   — WPF needs a `RectangleGeometry`; Avalonia's `PushClip` takes a `Rect` directly), stroke the
   mapped rect, `Pop()`. Off-screen edges are simply clipped with no artifact — no extra
   bounds-checking needed.

### Colour/weight (reuse existing per-app tokens verbatim — do not invent a new style)
- **WPF**: stroke with `SelectionAdorner.BorderUnderPen` then `SelectionAdorner.BorderDashPen` (both
  widened from `private` to `internal static readonly`, same assembly, no other change) — the
  identical frozen `Pen` instances the real on-screen crop edge uses, so the loupe preview is
  unambiguously "this is the crop edge," never confusable with a solid sampled-pixel swatch.
- **Avalonia**: `SelectionAdorner.cs:23,122` builds a single solid pen fresh per render
  (`new Pen(BorderBrushColor, 1.5)`, `BorderBrushColor = Color.FromRgb(0x2E,0xC8,0xFF)`) — extract
  that into `internal static readonly IPen BorderPen` (a minor dedup of the per-render allocation as
  a side benefit) and reuse it in `Magnifier`. Do **not** port WPF's two-tone dash style into
  Avalonia in this pass — that would be an unrelated, larger visual-parity change; each app keeps its
  own already-shipped border language, just now also drawn inside the loupe.

### New members (both apps, `Magnifier.cs`)
```
public RectPhysical? SelectionPx { get; set; }
public bool ShowSelectionPreview { get; set; }
```
Plain auto-properties, no `InvalidateVisual` needed in their setters — every frame that can
meaningfully change them already triggers `Update()`'s own `InvalidateVisual` within the same
dispatcher tick.

### Call-site wiring (both apps, identical shape)
- `SetSelection(RectPhysical? rect)` (WPF `OverlayWindow.xaml.cs:1553-1564`; Avalonia
  `OverlayWindow.axaml.cs:1357-1364`): add `MagnifierControl.SelectionPx = rect;` /
  `_magnifier.SelectionPx = rect;` right beside the existing `Adorner.SelectionPx = rect;` line —
  single source of truth, correct for every call site (plain `NewSelection`, spanning distribute,
  `Move`, `Resize`, clear-to-null) since it just mirrors what the Adorner already receives.
- End of the mouse-move handler, **after** the `switch (_dragMode)` block (WPF: right before
  `OnPreviewMouseMove`'s closing brace at `:1236`; Avalonia: the equivalent point in its
  pointer-moved handler, after its own switch, before `UpdateCursor()`):
  `MagnifierControl.ShowSelectionPreview = _dragMode == DragMode.NewSelection && !_newSelectionPending;`
  (`_magnifier.ShowSelectionPreview = ...` in Avalonia). Computing it here (not merged into the
  earlier `Update()` call at `:1109-1114`) is what avoids the one-frame-stale read described above.

### Gating — states that must NOT show the border
- **Pending-click phase** (`_newSelectionPending == true`): excluded by the flag formula itself.
  `_selectionPx` may still hold an unrelated *previous* selection's rect at this point (nothing has
  replaced it yet) — showing that stale rect would be actively misleading.
- **Resize / Move of an existing selection**: excluded by construction — in WPF the whole magnifier
  is already hidden for the full duration of these drags (`IsMagnifierActive` false,
  `OverlayWindow.xaml.cs:303-311`, with explicit `MagnifierControl.Hide()` calls at each drag-start
  site, confirmed at lines 819/859/871/889/914/1034/2181). Nothing to extend there without first
  reversing that separate, already-shipped UX decision (out of scope here). In Avalonia there is
  **no** equivalent hide/gate at all — confirmed by grep, `_magnifier.Update()` runs unconditionally
  every pointer move regardless of drag mode (pre-existing parity gap, not introduced by this
  change, flagged in `docs/PARITY.md` as a separate open item) — but gating `ShowSelectionPreview`
  strictly to `DragMode.NewSelection` keeps the *new* border-preview feature symmetric across both
  apps regardless of that pre-existing gap.
- **Blur/pixelate placement loupe** (`_currentTool == Pixelate && _dragMode == None`): excluded —
  that state's `_selectionPx` is the already-confirmed crop rect, not the blur region being placed;
  drawing it would be irrelevant noise inside a loupe whose whole purpose there is placing a
  *different* rectangle. `ShowSelectionPreview`'s `DragMode.NewSelection` check already excludes this
  since that placement mode runs with `_dragMode == DragMode.None`.
- **Spanning resize/move of an already-placed spanning selection**: excluded, same reasoning as plain
  Resize/Move — kept symmetric with the existing hidden-loupe behavior there; not explicitly asked
  for and not added.

### Why the useful case is covered
During a live `NewSelection` drag, one corner of the candidate rect is always exactly the current
cursor position (`RectPhysical.FromSize(Min(anchor,px), ..., Abs(px-anchor))`,
`OverlayWindow.xaml.cs:1145-1149`) — the same point the loupe samples around. So the actively-dragged
corner's two edges are always at or immediately next to the loupe's own crosshair: exactly the case
the user asked for (aligning an edge to the pixel while zoomed in). The far (anchor) corner's edges
additionally appear only when the selection is currently smaller than roughly 2x the sample radius;
otherwise `PushClip` correctly drops them with no artifact.

### Constraint check
No new setting (`MagnifierSampleRadius` unchanged; no new persisted state). Both apps stay 1:1 for
this feature specifically (unlike item 1, which is WPF-only because Avalonia has no flash subsystem
to touch). `RectPhysical`, `CapturedFrame`/`SdrImage`, and `DeviceScaleX`/`Y` need no changes — the
new mapping stays purely in physical-pixel-count space multiplied by `swatchDip`, identical in kind
to the existing per-swatch loop.

---

## WHAT CANNOT BE FIXED — say this to the user plainly

- **DRM/HDCP/PlayReady-protected content**: withheld by the Windows display/composition pipeline from
  *every* user-mode screen-capture consumer — both `IDXGIOutputDuplication` (Desktop Duplication) and
  `Windows.Graphics.Capture` are subject to the same OS/driver-level protected-content enforcement.
  It renders as black wherever the protected surface is on screen, for any app using these APIs, not
  just this one. There is no supported bypass, and this codebase deliberately relies on the *exact
  same* mechanism itself (`SetWindowDisplayAffinity(WDA_EXCLUDEFROMCAPTURE)` on its own windows —
  `FlashDimmer.cs`, `OverlayWindow.xaml.cs`, `RecordingChrome.cs`, `ColorPickerWindow.xaml.cs`) —
  proving the team already trusts this is an OS-enforced, any-process-usable mechanism. The ordering
  fix in item 1 does not and cannot touch this; it was never an ordering bug.
- **Other apps' own `WDA_EXCLUDEFROMCAPTURE` windows** (some password managers, some browsers'
  protected-video overlays, some chat clients): `SetWindowDisplayAffinity` is a public, any-process
  callable Win32 API. Any app that excludes its own window is invisible to *every* capture consumer,
  including RoeSnip, on both backends, regardless of timing. Not fixable from our side by design of
  the API.
- **"Some random things"**: no code-level fix can address this without a concrete example — ask the
  user for the specific app/window next time it happens, then check it against the two categories
  above (real answer) versus the tooltip-ordering bug (now fixed).

Everything else in the user's report — tooltips and other transient on-screen content being lost due
to *this app's own* pre-capture side effects — is fixed by item 1. Nothing about the DRM/foreign-app
exclusion category is fixed by that change, and it must not be described to the user as fixed.

---

## docs/PARITY.md entry (add under the existing capture/overlay section)

> **Flash-phase foreground claim removed (WPF only, 2026-08-02).** `FlashDimmer.ShowAll` no longer
> fires a background `SetForegroundWindow` when presenting the instant dim — it was racing ahead of
> `CaptureAll()` and dismissing tooltips/hover UI that was on screen at hotkey-press time before the
> frame was ever read. The dim's positioning (`SWP_NOACTIVATE`, unaffected) already didn't need
> foreground for either its visual effect or its input-swallow; Esc during the flash phase is
> separately covered by the focus-independent `FlashEscapeHook`; and the real overlay session's own
> `ForegroundActivator.Activate("session-start")` — a much more robust 3-tier ladder — already claims
> foreground once the session opens, which is always after the frame has been captured. Net effect:
> zero change to any measured latency number (hotkey-to-dim, capture-to-overlay), transient on-screen
> content now survives into the capture. **Avalonia has no equivalent flash-dimmer subsystem at all**
> — nothing to port for this item; this is a rare, intentional 1-app-only change.
>
> **Loupe now previews the live selection border while dragging a new selection (both apps).**
> `Magnifier` gained `SelectionPx`/`ShowSelectionPreview`, fed from the same `SetSelection`/pointer-move
> call sites that already feed `SelectionAdorner`. WPF reuses `SelectionAdorner`'s existing frozen
> border pens (now `internal`); Avalonia reuses its existing solid border pen (now extracted to a
> shared field). Gated to `DragMode.NewSelection` only, matching each app's existing loupe-visibility
> behavior during Resize/Move. **Pre-existing, unrelated gap noted while touching this code:** the
> Avalonia magnifier has no visibility gate at all (stays up through Resize/Move/annotation drags,
> unlike WPF) — tracked as a separate future parity item, not addressed here.

---

## Summary of files touched

- `src/RoeSnip/Overlay/FlashDimmer.cs` — delete the background `SetForegroundWindow` block in
  `ShowAll`; update doc comments (items 1, 2).
- `src/RoeSnip/App/TrayApp.cs` — one doc-comment addition (item 1).
- `src/RoeSnip/Capture/WgcCapturer.cs`, `src/RoeSnip/Capture/DesktopDuplicationCapturer.cs` —
  one doc-comment addition each (item 2).
- `src/RoeSnip/Overlay/Magnifier.cs`, `src/RoeSnip.App/Overlay/Magnifier.cs` — new properties +
  border-drawing code (item 3).
- `src/RoeSnip/Overlay/OverlayWindow.xaml.cs`, `src/RoeSnip.App/Overlay/OverlayWindow.axaml.cs` —
  two call-site edits each (item 3).
- `src/RoeSnip/Overlay/SelectionAdorner.cs`, `src/RoeSnip.App/Overlay/SelectionAdorner.cs` — widen
  pen fields to `internal` (item 3).
- `docs/PARITY.md` — two new entries (above).

No new settings. No changes to `RectPhysical`, `CapturedFrame`, `SdrImage`, `CaptureCache`,
`CaptureGate`, or any DRM/affinity-exclusion code anywhere in the app.
