# Cross-monitor selection — design

Branch `multimon-selection`, worktree `E:\GitHub\RoeLite\roesnip-multimon`. Extends the frozen WPF
app (`src/RoeSnip`) only — `RoeSnip.App` (the Avalonia xplat port) is untouched.

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
- **Annotations are disabled for a spanning selection (the documented v1 cut).** `ToolbarControl`
  gets `SetSpanningMode(bool)`, which collapses the tool row, the size input, undo/redo, the
  palette, and the Record button — leaving exactly Save / Copy / Cancel, per the task's own
  "acceptable v1" carve-out. `OverlayWindow` forces `_currentTool = AnnotationTool.None` whenever a
  selection is spanning, so even if a stale annotation tool were somehow still selected it cannot
  draw (no annotation shapes ever exist on a spanning selection, so there is nothing for the
  stitcher to burn in — see "Rendering" below).
- Handle/body drags are refused outright while the *current* selection is spanning — a mouse-down
  that would otherwise hit a resize handle or the selection body instead starts a brand-new
  `NewSelection` drag (replacing the old spanning selection wholesale). See "What v1 deliberately
  does not do" for why this guard exists (it is a correctness fix, not just a UX simplification).
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

- **No resize of an already-placed spanning selection.** The correctness hazard: `Move`/`Resize`
  drags capture `_dragStartRect` once at mouse-down as a **single window's local rect**. For a
  spanning selection, no single window's local rect is the true selection — it's only that window's
  intersection. Resizing from a secondary window's local (cut-edge) rect would silently desync that
  window's slice from the session's `_spanningVirtual` and from every other window's slice, with no
  way to reconcile them at Confirm time. Rather than thread virtual-rect awareness through the
  Move/Resize math (`ApplyResize`, `ClampToFrame`, the per-handle corner logic — all written and
  tested for exactly one frame of reference), v1 refuses to enter those drag modes at all once the
  current selection spans, and instead starts a fresh `NewSelection` drag on any further mouse-down.
  Replacing the selection is one gesture; this is a scope cut for time, not a hard technical wall —
  a v2 could thread `_dragStartRect` through virtual coordinates the same way `NewSelection` now
  does.
- **No Record (MP4/GIF) for a spanning selection.** Recording is a real-time per-frame WGC capture
  session against a single monitor's duplication output (`RecordingController`/`RegionOutline`);
  stitching multiple monitors' live capture streams frame-by-frame is a materially different (and
  much larger) feature than a one-shot still composite. The toolbar hides Record while spanning;
  `RecordForAutomation`/the toolbar-driven `Record()` both refuse it defensively even if reached.
- **No HDR save (`.jxr`) for a spanning selection.** `JxrWriter` encodes one monitor's FP16 crop
  verbatim (untouched HDR original, per DESIGN.md — "no annotations, raw crop"). There is no defined
  operation for "the untouched HDR original of a rectangle that came from two monitors with
  different SDR-white/peak photometrics and possibly different FP16-vs-BGRA8 delivered formats" —
  concatenating two different linear color spaces into one FP16 buffer would misrepresent both. This
  is a real semantic gap, not a trivial composite, so it's deferred rather than guessed at. The
  toolbar hides the Save-HDR menu item while spanning; `OverlayResult.SaveHdrRequested` is forced
  false for a spanning result and `Program.cs`'s auto-save-HDR-copy branch is skipped (logged) when
  `OverlayResult.SpanningVirtualSelectionPx` is set, even if the user's `AutoSaveHdrCopy` setting is
  on.
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

## Reconciling with the mainline perf work

`Overlay/OverlayController.cs` and `Overlay/OverlayWindow.xaml.cs` are exactly the files the
instant-dim/pool architecture lives in, so a mainline perf change touching the same files is
possible. The new code in both files is additive (new fields, new methods, a few new branches at
existing decision points) and never changes `Show`/`Hide`/pool-take/pool-reprovision mechanics, the
flash handoff, or the keyboard hook — a merge conflict is more likely than a semantic collision, and
should resolve as "keep both hunks." Everything spanning-specific is reachable only through the new
`_spanningVirtual`/`_isSpanningSelection` state, which stays `null`/`false` unless a drag actually
crosses a monitor boundary, so it cannot regress the hot path being optimized elsewhere.
