# Cross-monitor selection — design

Branch `multimon-selection`, worktree `E:\GitHub\RoeLite\roesnip-multimon`. Extends the frozen WPF
app (`src/RoeSnip`) only — `RoeSnip.App` (the Avalonia xplat port) is untouched.

**2026-07 update (branch `spanning-selection-complete`, worktree `E:\GitHub\RoeLite\roesnip-spansel`):**
resize-after-place and HDR save for a spanning selection — two of the four v1 cuts below — are now
implemented. See "v2: resize-after-place and HDR save" near the end of this document for the design;
the sections below are left as-written (v1) except where marked **[RESOLVED, see v2 section]**, so
this document still reads as an accurate history of why each cut existed in the first place.

**2026-07-13 integration pass update:** Record (MP4/GIF) for a spanning selection — the third of the
four v1 cuts — is now also resolved, once the separate `spanning-recording` work track's own
multi-monitor-aware capture pipeline landed alongside this branch. See that bullet's own
**[RESOLVED, integration pass 2026-07-13]** marker below. Only mixed-DPI handling remains a v1 cut.

## Recap: why this was a v1 non-goal

DESIGN.md's overlay section: "Selection lives on one monitor (cross-monitor is a v2 seam); starting
a drag on monitor A clears any selection on B." One `OverlayWindow` (one HWND) per monitor, each
holding its own already-tone-mapped `SdrImage` preview. All selection math is monitor-relative
physical pixels, clamped to that window's own frame at every finalize point.

## The mechanism that makes this tractable

Two facts, both verified by reading the existing code rather than assumed:

1. **`Mouse.CaptureMouse()` is a Win32 `SetCapture`, which is HWND-level, not client-area-level.**
   Once monitor A's window captures the mouse, every `WM_MOUSEMOVE` keeps being delivered to A's
   HWND for as long as the button stays down, even while the cursor is physically over monitor B's
   screen area (and B's own window never sees the move — capture is exclusive). WPF's
   `MouseEventArgs.GetPosition(this)` keeps working during this: it returns A-window-relative DIPs
   that go negative or past `ActualWidth`/`ActualHeight` once the cursor leaves A's rectangle. This
   is exactly the "click a scrollbar thumb and drag outside the window" behavior every Windows user
   already relies on.
2. **`OverlayWindow.ToPhysical` and the existing `NewSelection` drag math never clamped to the local
   frame mid-drag** — only the mouse-up finalize step called `ClampToFrame`. So the raw drag
   candidate rectangle, in window-A-local physical pixels, already legitimately goes outside
   `[0,frame.Width]×[0,frame.Height]` while the button is held.

Putting these together: monitor A's window can already compute a **virtual-desktop** candidate rect
mid-drag — `virtualRect = A.Monitor.BoundsPx.TopLeft + localCandidateRect` — for any drag, including
one that has visually left A's screen. That candidate is then handed to the session, which is the
one object that knows every monitor's bounds.

This validates the recommended shape's guess ("SetCapture keeps delivering moves beyond the window —
likely simplest correct") — it is exactly what's implemented, with zero Win32 hooking needed beyond
what `CaptureMouse()` already did.

## Architecture

**Kept exactly as-is:** one `OverlayWindow`/HWND per monitor, the pool
(`OverlayWindowPool`)/flash (`FlashDimmer`) latency machinery, `WDA_EXCLUDEFROMCAPTURE`, the
session-scoped `WH_KEYBOARD_LL` hook, per-monitor tone-mapping (`SdrImage` per window keeps its own
already-tone-mapped pixels — a spanning composite only ever copies bytes between already-tone-mapped
buffers, never re-tone-maps or shares one monitor's curve with another).

**New: a session-level shared virtual-desktop selection**, live only while a selection actually
touches ≥2 monitors:

- `OverlaySession` gains `RectPhysical? _spanningVirtual` (virtual-desktop physical pixels) and
  `OverlayWindow? _spanningPrimaryWindow`. Both stay `null` for every ordinary, single-monitor
  selection — the pre-existing code path (`window.SelectionPx`, `Confirm`'s
  `_windows.FirstOrDefault(w => w.SelectionPx is not null)`, etc.) is **completely unchanged** and is
  still what runs for a same-monitor drag. Nothing about the common case's latency or rendering
  changes.
- The **only** drag mode that can produce a spanning selection is `NewSelection` (dragging out a
  fresh rectangle). `Move`/`Resize` (dragging an already-placed selection's body/handles) are left
  untouched — they still clamp to the single owning window's frame at mouse-up exactly as before.
  Concretely this means: **you can drag a fresh selection across monitors, but you cannot resize an
  existing selection past a monitor boundary with the handles** — grabbing a handle and dragging it
  onto a neighboring monitor snaps back to that monitor's edge on release, same as it always has.
  Practically this is a small loss: replacing the selection with a new drag is one gesture anyway,
  and it avoids a real correctness hazard (see "What v1 deliberately does not do" below).
- On every `NewSelection` mouse-move, the owning window converts its local (possibly out-of-bounds)
  candidate rect to a virtual-desktop rect and calls into the session
  (`OverlaySession.OnSpanningCandidate`), which:
  1. Clamps the candidate to the union of every captured monitor's bounds (the "virtual desktop
     bounding box", computed once per session).
  2. Intersects the clamped rect against each window's own `Monitor.BoundsPx`.
  3. For every window with a non-empty intersection, converts that intersection to monitor-relative
     coordinates and pushes it into that window (`SetSpanningLocalSelection`) — this reuses the
     window's **existing** `SetSelection` → dim-mask/adorner/toolbar-placement pipeline verbatim; a
     spanning selection's per-window rendering is not a new code path, it's the old one fed a
     different rect.
  4. Windows with an empty intersection get their local selection cleared.
  5. If ≥2 windows got a non-empty intersection, the selection is "spanning": the session records
     the virtual rect and marks the *owning* window (the one whose drag produced this candidate) as
     primary. Exactly one window is ever primary for a spanning selection.
- Mouse-up finalize (`OverlaySession.FinalizeNewSelectionDrag`) applies the existing "<2px = cancel"
  rule against the **true virtual size** when spanning (not a single window's own possibly-tiny
  local slice — see the bug this avoids below), otherwise against the single owning window's local
  rect exactly as before.

This means the "distribute" function is the single new primitive, and it degenerates to the old
per-window behavior whenever the rect only ever touches one monitor — there is no branch a
single-monitor capture takes that didn't already exist.

### Toolbar and annotations

- Only the primary window ever shows a `ToolbarControl` instance while a selection is spanning
  (secondary windows call `HideToolbar()`, same method used when a monitor has no selection at all).
  This satisfies "toolbar appears on the monitor holding the drag-end cursor" — the owning window of
  the drag that produced the current spanning rect *is* wherever the user's cursor was on mouse-up
  (capture guarantees the owning window is wherever `OnPreviewMouseLeftButtonUp` actually fires).
- **Annotations are disabled for a spanning selection (the documented v1 cut) — still true in v2.**
  `ToolbarControl` gets `SetSpanningMode(bool)`, which collapses the tool row, the size input,
  undo/redo, the palette, and the Record button — leaving Save (now including Save HDR, as of v2) /
  Copy / Cancel. `OverlayWindow` forces `_currentTool = AnnotationTool.None` whenever a selection is
  spanning, so even if a stale annotation tool were somehow still selected it cannot draw (no
  annotation shapes ever exist on a spanning selection, so there is nothing for the stitcher to burn
  in — see "Rendering" below). **Re-evaluated alongside resize-after-place (v2) per the task brief's
  "IF the shared-virtual-coordinate rendering falls out naturally from the resize work" condition —
  it does not.** Resize-after-place only shares the selection RECT's own geometry across windows
  (four numbers, one shared frame of reference via `SpanningSelectionMath`); an annotation shape
  (freehand path, arrow endpoints, a text run) lives in `AnnotationLayer`, a per-window
  `DrawingVisual`-backed store with no notion of any OTHER window's coordinate space, and letting a
  shape be drawn/dragged/hit-tested across a monitor boundary would need its own cross-window design
  (a shared virtual-coordinate annotation store, cross-window hit-testing, a burn-in pass that
  samples from whichever window(s) a shape crosses) — a materially different, larger feature than
  translating one rect. Annotations stay off for a spanning selection; only the crop rect itself
  gained cross-window awareness.
- **[RESOLVED, see v2 section]** ~~Handle/body drags are refused outright while the *current*
  selection is spanning — a mouse-down that would otherwise hit a resize handle or the selection
  body instead starts a brand-new `NewSelection` drag (replacing the old spanning selection
  wholesale).~~ As of v2, a handle/body hit on a spanning selection's REAL edge starts a
  SpanningResize/SpanningMove drag instead — see the v2 section for how the correctness hazard this
  used to guard against is now avoided instead of sidestepped. A mouse-down that misses (outside the
  selection, or on an edge that's just a monitor-boundary clip) still falls back to starting a
  brand-new `NewSelection` drag exactly as before.
- `SelectionAdorner` gets `SuppressHandlesAndBadge` (secondary windows: dashed border only, no
  corner brackets, no "W×H" badge — a badge showing just that monitor's slice size would be actively
  misleading) and `OverrideSizeLabel` (primary window, spanning: shows the *true* composite
  dimensions instead of its own local slice's).

### Rendering (Copy / Save)

`OverlaySession.RenderSpanningSelection(RectPhysical virtualRect)` builds one `SdrImage` sized to
the virtual selection rect. For each window with a non-null local selection, it crops that window's
own **already tone-mapped** `SdrImage` preview (`window.Preview.Crop(...)` — the same `SdrImage.Crop`
every single-monitor capture already uses) and byte-copies it into the composite canvas at its
virtual-desktop-relative offset. Per the hard constraint, this never touches FP16/tone-mapping code
at all — it is a pure BGRA8 byte copy between two buffers that were each independently tone-mapped
with their own monitor's photometrics back when the overlay session started. **Gaps (no monitor
covers part of the rect — e.g. the DISPLAY2 portrait panel doesn't reach as far down as DISPLAY3, or
literal dead space between non-adjacent monitors) are filled opaque black** (`B=G=R=0, A=255`).
Documented choice, not a default fallen into by accident: transparent would silently produce a
partially-transparent PNG for something the user visually saw as one continuous rectangle (deceptive
for Copy/paste into an opaque-only target), and any "detect the gap and crop it out" behavior would
make the saved image's dimensions not match what the user actually dragged out (the automation
contract explicitly says selection coordinates are what they are).

Since annotations never exist on a spanning selection, there is no `RenderForExport`/
`DrawingVisual`/`RenderTargetBitmap` pass at all for this path — it's a plain array composite,
cheaper than the single-monitor annotated-render path.

### What v1 deliberately does not do

- **[RESOLVED, see v2 section] No resize of an already-placed spanning selection.** The correctness
  hazard: `Move`/`Resize` drags capture `_dragStartRect` once at mouse-down as a **single window's
  local rect**. For a spanning selection, no single window's local rect is the true selection — it's
  only that window's intersection. Resizing from a secondary window's local (cut-edge) rect would
  silently desync that window's slice from the session's `_spanningVirtual` and from every other
  window's slice, with no way to reconcile them at Confirm time. Rather than thread virtual-rect
  awareness through the Move/Resize math (`ApplyResize`, `ClampToFrame`, the per-handle corner logic
  — all written and tested for exactly one frame of reference), v1 refused to enter those drag modes
  at all once the current selection spans, and instead started a fresh `NewSelection` drag on any
  further mouse-down. Replacing the selection is one gesture; this was a scope cut for time, not a
  hard technical wall — **v2 threads `_dragStartRect` through virtual coordinates the same way
  `NewSelection` always did, via two new drag modes (`SpanningResize`/`SpanningMove`) that feed
  `OnSpanningCandidate` on every mouse-move exactly like `NewSelection` does** — see the v2 section.
- **[RESOLVED, integration pass 2026-07-13] No Record (MP4/GIF) for a spanning selection — was still
  true as of v2, now wired.** Recording is a real-time per-frame WGC capture session against a single
  monitor's duplication output (`RecordingController`/`RegionOutline`); stitching multiple monitors'
  live capture streams frame-by-frame is a materially different (and much larger) feature than a
  one-shot still composite — which is exactly why the parallel `spanning-recording` work track had to
  build a whole separate capture path for it (`SpanningCanvasCompositor`, `RecordingSession.
  BeginCapture`'s own intersected-monitor re-derivation, `EncoderLoopSpanning`) rather than reusing
  the single-monitor `RegionRecorder`/`EncoderLoop` pair unchanged. Once that track landed alongside
  this one, the integration was a plumbing-only change, exactly as this bullet originally predicted:
  `OverlaySession.Record` (renamed doc comment, logic moved to the new `RecordSpanning`) packages the
  anchor (primary) monitor plus the spanning virtual rect — converted to be relative to that anchor's
  own origin — onto `OverlayResult`, and `RecordingSession.Start()`/`BeginCapture` do the rest exactly
  as they already did for a single-monitor take; nothing downstream of `OverlayResult` needed to know
  the selection came from a spanning drag at all. The toolbar no longer hides Record while spanning
  (`ToolbarControl.SetSpanningMode`); `RecordForAutomation`/the toolbar-driven `Record()` both allow
  it now, gated only by the same "something is actually selected" check every other shape already
  gets.
- **[RESOLVED, see v2 section] No HDR save (`.jxr`) for a spanning selection.** `JxrWriter` encodes
  one monitor's FP16 crop verbatim (untouched HDR original, per DESIGN.md — "no annotations, raw
  crop"). v1's reasoning was: there is no defined operation for "the untouched HDR original of a
  rectangle that came from two monitors with different SDR-white/peak photometrics and possibly
  different FP16-vs-BGRA8 delivered formats" — concatenating two different linear color spaces into
  one FP16 buffer would misrepresent both. **This reasoning was too conservative and is corrected in
  v2**: raw scRGB is not "two different linear color spaces" at all — it is ONE absolute linear space
  (1.0 = 80 nits) that every monitor's frame is already expressed in (or convertible to, via the
  existing `CapturedFrame.ReadPixelScRgb` per-pixel decode, for a degenerate Bgra8Srgb monitor)
  regardless of that monitor's own SDR-white or peak-luminance settings — those photometrics only
  ever matter for TONE-MAPPING (a completely different code path, `SdrImage.FromCapturedFrame` /
  `Color.ToneMapper`), which the raw HDR save never touched even for a single monitor. Stitching raw
  crops is therefore exactly as well-defined as stitching already-tone-mapped SDR crops (which v1
  always did for Copy/Save-PNG via `RenderSpanningSelection`) — see the v2 section for
  `JxrWriter.WriteSpanning`. v1's toolbar/OverlayResult-level gating (hide the menu item, force
  `SaveHdrRequested` false, skip `AutoSaveHdrCopy`) is removed in v2 accordingly.
- **No mixed-DPI handling** (per the task brief — this machine is all-96-DPI). The distribute
  function works entirely in physical pixels end-to-end (virtual-desktop bounds, per-monitor
  intersection, the composite canvas) and never touches `_scaleX`/`_scaleY` — DPI only enters at the
  existing view-layer conversion (`ToPhysical`, `SelectionAdorner`'s DIP rendering), unchanged. If a
  future mixed-DPI machine composites two monitors' crops (each already physical-pixel, already
  tone-mapped, already at that monitor's own native resolution) side by side, the byte-copy in
  `RenderSpanningSelection` doesn't care that the two source images came from different DPI monitors
  — it already worked in device pixels. The one thing that would need real design work for mixed DPI
  is `ClampToVirtualDesktop`/the intersection math if monitor bounds ever overlapped in physical-pixel
  space (they don't, on Windows, regardless of DPI — `BoundsPx` is already the OS's own
  non-overlapping virtual-desktop layout), so this seam is believed to carry over unchanged, but is
  explicitly untested.

## v2: resize-after-place and HDR save (`spanning-selection-complete`)

### Resize-after-place

The v1 refusal existed because `Move`/`Resize` captured `_dragStartRect` as a single window's own
local rect, and a spanning selection has no single window whose local rect IS the true selection. v2
solves this the same way v1 already solved the equivalent problem for a fresh `NewSelection` drag:
never let a drag trust any one window's local rect as ground truth — always work in virtual-desktop
coordinates and redistribute through the session on every mouse-move.

**The distribute primitive was extracted into a pure, unit-tested static class,
`Overlay/SpanningSelectionMath.cs`** — no WPF `Window`, no mutable session/window state:

- `Distribute(candidateVirtual, virtualDesktopBounds, monitorBounds)` is `OnSpanningCandidate`'s old
  inline clamp/intersect logic, factored out verbatim, plus one new piece of information per monitor:
  **which of that monitor's own local rect's 4 edges are REAL edges of the true selection**, versus
  merely where that monitor's own screen boundary happened to CLIP the selection. An edge is real iff
  the intersection didn't have to pull it inward from the (virtual-desktop-clamped) candidate's own
  edge — exactly the same comparison the intersection math already made internally, just surfaced as
  a `SelectionEdges` flags result (`SpanningHit.RealEdges`) instead of being thrown away.
- `ApplyResize(start, handle, px)` is the pre-existing per-handle corner/edge-replacement math
  (previously a private method on `OverlayWindow`), made `public static` and moved here so both the
  plain single-monitor `Resize` drag AND the new spanning one call the exact same function — it was
  always a pure function of (start rect, handle, pointer position); "pointer position" just means
  something different (window-local vs. virtual-desktop) depending on the caller.

**`SelectionAdorner` gained `RealEdges` (replacing the old all-or-nothing
`SuppressHandlesAndBadge`, which is now split into `RealEdges` + a narrower `SuppressBadge`):**
`HitTestHandle` only ever returns a corner/side handle when that handle's edge(s) are real, and
`OnRender`'s corner brackets are only drawn for corners where both adjacent edges are real — a
clipped edge draws its dashed border (useful feedback: "this monitor's own piece ends here") but no
resize affordance, because there is nothing real to grab there. This applies uniformly to EVERY
window (not just the primary) — a secondary window's own real edges get real handles too, so
resizing a spanning selection's far edge works from whichever monitor that edge is actually visible
on, not just from the primary window's own (possibly fully-clipped-on-that-side) slice.

**`OverlayWindow` gained two new drag modes, `SpanningResize`/`SpanningMove`:** a mouse-down while
`IsSpanningSelection` is true now hit-tests through `Adorner.HitTestHandle` (which already respects
`RealEdges`) instead of unconditionally starting a fresh `NewSelection` drag. A handle hit starts
`SpanningResize`; a body hit starts `SpanningMove`; anything else (outside the selection, or — in
principle — a hit that somehow isn't real) still falls back to `NewSelection`, replacing the
selection wholesale exactly as v1 always allowed. Both new modes:

1. Capture the CURRENT shared virtual rect (`_spanningVirtualRectPx`, populated by every
   `SetSpanningLocalSelection` call — the session already handed the same value to every window, not
   just the primary, so this needed no new plumbing) as their drag-start reference frame.
2. On every mouse-move, compute a candidate rect **in virtual-desktop coordinates**
   (`SpanningResize`: this window's own monitor origin folded into the cursor's local position, then
   `ApplyResize` against the virtual start rect; `SpanningMove`: a plain pixel delta — monitor-
   independent — applied to the virtual start rect) and feed it to `_onSpanningCandidate` —
   **the exact same session-level distribute call `NewSelection` always used.** Neither mode ever
   calls `SetSelection` directly or touches any window's own local rect as an intermediate value.
3. On mouse-up, `_onFinalizeNewSelection` (unchanged) applies the existing "&lt;2px = cancel" rule
   against the redistributed result — this already worked for any drag that funnels through
   `OnSpanningCandidate`, so no changes were needed there either.

Whichever window the drag started on (mouse-down) becomes primary for the drag's duration — so
grabbing a handle from a secondary window naturally promotes it. Unlike a `NewSelection` drag,
though, this is NOT simply `isPrimary = ReferenceEquals(w, owner)`: a `NewSelection` candidate's
anchor is always a point on the owner's own monitor, so the owner is guaranteed to hold a slice for
the whole drag, but `SpanningResize`/`SpanningMove` start from an ALREADY-PLACED selection and can
move the candidate clean off the owner's monitor while it still spans ≥2 OTHER monitors (drag the
left edge of a 3-monitor span rightward past the leftmost monitor's own boundary, for instance).
`OnSpanningCandidate` falls back to the lowest-indexed monitor that still holds a slice whenever the
owner itself no longer does, so some window is always primary (and therefore some window always
shows the toolbar/size-badge) for as long as `Distribute` reports `IsSpanning`. If a resize shrinks
the selection back down to a single monitor, `Distribute` naturally reports `IsSpanning = false` and
the selection transparently becomes an ordinary single-monitor one — no special-case code needed for
that transition either.

**`SpanningMove` needs a different clamp strategy than `SpanningResize`/`NewSelection`.** Both edges
of a Move candidate shift by the same delta, so pulling it back inside the virtual desktop's bounds
must SLIDE the rect (preserve width/height) — exactly what the single-monitor `Move` drag's
`ClampToFrame` already does. Naively reusing `Distribute`'s own `ClampToVirtualDesktop` (which clamps
each of the 4 edges independently — correct for `Resize`/`NewSelection`, where only one or two edges
move per gesture and stopping just that edge at the boundary is the expected result) would instead
SHRINK a Move candidate that overhangs an edge, which reads as the selection silently losing size
mid-drag. `SpanningSelectionMath.SlideToBounds` is the Move-specific equivalent of `ClampToFrame`;
`OnSpanningCandidate` takes a `preserveSize` bool (true only for `SpanningMove`) and runs the
candidate through `SlideToBounds` before distributing when it's set. `SlideToBounds` only guarantees
the candidate stays inside the virtual desktop's own bounding BOX, though — with non-adjacent
monitors (a real gap between them), a same-size candidate can still land entirely in that gap and
intersect nothing; `OnSpanningCandidate` treats a zero-hit `preserveSize` candidate against an
already-placed spanning selection as a no-op (keeps the drag's last valid state) rather than letting
a Move gesture delete the selection with no way to undo it.

**Toolbar suppression during a live drag is keyed off more than the local `_dragMode`.**
`OnSpanningCandidate` redistributes the candidate to EVERY window on EVERY mouse-move, not just the
drag owner (which is the only window whose `_dragMode` is actually set) — so a `SpanningMove`/
`SpanningResize` drag that lands its candidate on a monitor other than the owner's would otherwise
have that window's `UpdateToolbarPlacement` see `_dragMode == None` and show/reposition a toolbar
mid-drag. `SetSpanningLocalSelection` takes a `dragInProgress` bool (true only from
`OnSpanningCandidate`'s mid-drag calls) that every window latches into
`_suppressToolbarForSpanningDrag`; `OverlaySession.FinalizeNewSelectionDrag` clears it on every window
via `OverlayWindow.NotifySpanningDragEnded()` once the drag has genuinely ended, so a non-owner
window's toolbar visibility gets correctly re-resolved (shown, hidden, or left suppressed) exactly
once, right after release — never mid-drag.

### HDR save for a spanning selection

The v1 deferral reasoned that combining two monitors' "differently-photometric FP16 crops" had no
defined operation. This was too conservative: **raw scRGB is one absolute linear space (1.0 = 80
nits) for every monitor**, independent of that monitor's own SDR-white level or peak brightness —
those photometrics only ever enter into TONE-MAPPING (`Color.ToneMapper`/`SdrImage.FromCapturedFrame`,
used for Copy/Save-PNG's preview), a code path the raw HDR save never touched even for a single
monitor. `CapturedFrame.ReadPixelScRgb` already performs the one well-defined per-pixel decode (Fp16
pass-through, or Bgra8 decoded via the sRGB EOTF and rescaled by that monitor's own `SdrWhiteNits`)
that makes any two monitors' pixels directly comparable in the same units — `JxrWriter.Write` (the
existing single-monitor HDR save) already relies on exactly this to handle a degenerate `Bgra8Srgb`
monitor uniformly with a native `Fp16ScRgb` one. Stitching raw crops from different monitors is
therefore exactly as well-defined as stitching already-tone-mapped SDR crops, which v1 always did for
Copy/Save-PNG (`RenderSpanningSelection`).

**`JxrWriter.WriteSpanning(path, virtualRect, crops)`** (`Imaging/JxrWriter.cs`) is the new entry
point: it builds one tightly-packed `R32G32B32A32Float` canvas sized to the spanning selection,
pre-fills it to opaque linear black (the same documented gap-fill choice `RenderSpanningSelection`
already made for the SDR composite, just in linear scRGB instead of BGRA8), overwrites each
contributing monitor's own region by reading through `ReadPixelScRgb`, and writes it through the
exact same WIC `128bppRGBAFloat`/`ContainerFormat.Wmp` encode path `Write` already uses for a single
monitor (factored into a shared `EncodeAndWrite` helper). `crops` is a list of
`RoeSnip.SpanningFrameCrop(Frame, LocalCropPx, DestX, DestY)` — one per monitor the selection touches
— computed by `OverlayController.OverlaySession.BuildSpanningFrameCropsForHdr`, which shares its
offset geometry (`ComputeSpanningCropGeometry`) with `RenderSpanningSelection`'s own SDR composite:
same destination math, two different pixel sources (raw `CapturedFrame` vs. already-tone-mapped
`SdrImage`), computed once.

**Wiring:** `OverlayResult` gained `SpanningFrameCrops` (populated unconditionally on every spanning
result, mirroring how `SourceFrame`/`SelectionPx` are always populated on a non-spanning one — so
`settings.AutoSaveHdrCopy` keeps working transparently, not just an explicit Save-HDR click) and
`SaveHdrRequested` is no longer forced false for a spanning result — `ConfirmSpanning` now passes the
real value through, same as the non-spanning `Confirm`. `AppComposition.WriteJxrSpanning` (set by
`Imaging/JxrWriter.cs`'s module initializer, mirroring the existing `WriteJxr` hook) is the new
Program.cs (WP-A) seam WP-C's writer registers into; `Program.cs`'s HDR-export branch now calls it for
a spanning result instead of skipping. `ToolbarControl`'s Save-HDR context-menu item was never
actually hidden by `SetSpanningMode` in v1 (a pre-existing doc/code mismatch — the menu item was
always visible but the write was a defensive no-op logging "not available"); v2 makes that behavior
genuine instead of changing the toolbar.

### What's still deferred

Record (MP4/GIF) for a spanning selection and mixed-DPI handling remain exactly as v1 left them — see
their own bullets above, now marked accordingly. Annotations were explicitly re-evaluated alongside
this work (see the "Toolbar and annotations" section above) and confirmed to still require their own,
separate design; they were not enabled.

## Automation (`select`)

`OverlaySession.SetSelectionForAutomation(RectPhysical virtualDesktopPx)` (backing the `select`
command) now routes through the exact same `OnSpanningCandidate` + `FinalizeNewSelectionDrag`
functions a real drag uses — never a separate implementation. The owning/"primary" window is chosen
the same way the pre-existing single-monitor automation path already did (whichever monitor contains
the rect's top-left corner). `GetSelectionForAutomation` (backing `state`'s `selection` field)
returns the virtual spanning rect directly when one is active, else falls back to the existing
per-window lookup — so an agent driving the pipe sees one consistent `{x,y,w,h}` in virtual-desktop
coordinates whether or not the rect happens to span, matching the wire protocol's existing "always
virtual-desktop physical pixels" contract with no special case in the JSON shape itself.

## Files touched (expected)

- `src/RoeSnip/Overlay/OverlayWindow.xaml.cs` — new fields/method for the local half of spanning
  state, `NewSelection` drag routed through the new callbacks, the handle/body-drag refusal guard.
- `src/RoeSnip/Overlay/OverlayController.cs` — `OverlaySession`'s new spanning state, distribute/
  finalize/composite methods, `Confirm`/`Record`/`GetSelectionForAutomation`/
  `SetSelectionForAutomation`/`CancelStage` updated to branch on it.
  `Overlay*` files) — flag this to whoever merges the perf branch back if it ends up touching the
  same drag-mode switch statements in `OverlayWindow.xaml.cs`'s `OnPreviewMouseMove`/
  `OnPreviewMouseLeftButtonUp` for unrelated reasons; nothing here touches the pool/flash machinery
  itself.
- `src/RoeSnip/Overlay/SelectionAdorner.cs` — `SuppressHandlesAndBadge`, `OverrideSizeLabel`.
- `src/RoeSnip/Overlay/ToolbarControl.xaml(.cs)` — `SetSpanningMode`.
- `src/RoeSnip/Program.cs` — `OverlayResult.SpanningVirtualSelectionPx`, HDR-export branch skips it.
- No changes expected to `App/AutomationServer.cs` itself (the wire protocol/DTOs already carry
  plain `{x,y,w,h}` in virtual-desktop pixels, which was already spanning-shaped) or to any
  `Capture/*`/`Imaging/*`/`Color/*` file (tone-mapping is untouched; only already-tone-mapped bytes
  are composited).

## Files touched — v2 (`spanning-selection-complete`)

Note the last bullet above (no `Imaging/*` changes expected) turned out to be wrong for v2 — HDR save
needed real WIC-encode code, not just OverlayResult plumbing, so it's the one exception to "leaf
Capture/Imaging/Color files stay untouched."

- `src/RoeSnip/Overlay/SpanningSelectionMath.cs` — **new file.** Pure geometry: `Distribute`,
  `ComputeVirtualDesktopBounds`, `ClampToVirtualDesktop`, `ApplyResize` (moved here from
  `OverlayWindow.xaml.cs`), and the new `SelectionEdges`/`SpanningHit`/`SpanningDistribution` types.
  Unit-tested directly (`tests/RoeSnip.Tests/SpanningSelectionMathTests.cs`) with no WPF `Window`
  involved.
- `src/RoeSnip/Overlay/OverlayWindow.xaml.cs` — two new `DragMode` values (`SpanningResize`/
  `SpanningMove`), `_spanningVirtualRectPx`/`_dragStartVirtualRect` fields, the mouse-down spanning
  branch rewritten to hit-test via `Adorner.HitTestHandle` instead of always starting a fresh
  `NewSelection`, new mouse-move/mouse-up/cursor/toolbar-suppression cases for both new drag modes,
  `SetSpanningLocalSelection` gained a `SelectionEdges realEdges` parameter. The private `ApplyResize`
  method was removed (moved to `SpanningSelectionMath`, all call sites updated).
- `src/RoeSnip/Overlay/OverlayController.cs` — `OnSpanningCandidate` now delegates its clamp/
  intersect/real-edge math to `SpanningSelectionMath.Distribute`; new `ComputeSpanningCropGeometry`/
  `BuildSpanningFrameCropsForHdr` helpers (the latter feeds HDR save); `RenderSpanningSelection`
  refactored to share crop geometry with the new HDR path; `ConfirmSpanning`/
  `ConfirmSaveForAutomation` now propagate `SaveHdrRequested` and populate `SpanningFrameCrops`
  instead of forcing HDR off.
- `src/RoeSnip/Overlay/SelectionAdorner.cs` — `SuppressHandlesAndBadge` replaced by `RealEdges`
  (`SelectionEdges` flags, gates hit-testing AND corner-bracket rendering per edge) + a narrower
  `SuppressBadge` (badge-only, primary-vs-secondary — unchanged in spirit from before).
- `src/RoeSnip/Overlay/ToolbarControl.xaml.cs` — `SetSpanningMode`'s doc comment updated (HDR now
  genuinely supported while spanning; Record's gate gets a pointer to the parallel-track integration
  note); no visibility-logic changes (Save-HDR was never actually toggled by this method).
- `src/RoeSnip/Program.cs` — new `SpanningFrameCrop` record, `OverlayResult.SpanningFrameCrops`,
  `AppComposition.WriteJxrSpanning` hook, HDR-export branch now calls it for a spanning result.
- `src/RoeSnip/Imaging/JxrWriter.cs` — new `WriteSpanning`/`BuildSpanningFloatBuffer`, `Write`'s WIC
  encode call refactored into a shared `EncodeAndWrite` helper both paths use; module initializer
  also registers `WriteJxrSpanning`.
- `tests/RoeSnip.Tests/SpanningSelectionMathTests.cs` — new file, pure geometry.
- `tests/RoeSnip.Tests/JxrSpanningRoundTripTests.cs` — new file, WIC round-trip (mirrors the existing
  `JxrRoundTripTests.cs` pattern) for `WriteSpanning`.
- Explicitly NOT touched: `Recording/*`, `App/*` (the Record-for-spanning gate — see "What's still
  deferred" above — is a single `return`/error-string in `OverlayController.cs`, not a new file in
  either of those directories).

## Reconciling with the mainline perf work

`Overlay/OverlayController.cs` and `Overlay/OverlayWindow.xaml.cs` are exactly the files the
instant-dim/pool architecture lives in, so a mainline perf change touching the same files is
possible. The new code in both files is additive (new fields, new methods, a few new branches at
existing decision points) and never changes `Show`/`Hide`/pool-take/pool-reprovision mechanics, the
flash handoff, or the keyboard hook — a merge conflict is more likely than a semantic collision, and
should resolve as "keep both hunks." Everything spanning-specific is reachable only through the new
`_spanningVirtual`/`_isSpanningSelection` state, which stays `null`/`false` unless a drag actually
crosses a monitor boundary, so it cannot regress the hot path being optimized elsewhere.
