# Cross-platform parity tracker (WPF RoeSnip vs Avalonia RoeSnip.App)

Synthesized 2026-07-13 from an eight-way subsystem audit comparing src/RoeSnip (the mature
WPF app, source of truth for behavior) against src/RoeSnip.App + RoeSnip.Core +
RoeSnip.Platform.{Windows,Linux,MacOS} (the port). "1:1" means the Avalonia app on Windows
behaves identically to the WPF app; on Linux/macOS as close as the OS allows, degrading
gracefully behind Platform.* rather than taking a hard Windows dependency in shared code.

Items are ordered by dependency and value: shared-Core extractions and load-bearing
infrastructure first, then features, then polish. Every item must leave the whole solution
green (dotnet build RoeSnip.sln -c Release; dotnet test RoeSnip.sln -c Release --no-build)
and be independently committable. The WPF app must not be destabilized by any item; where an
item retargets WPF code onto a Core extraction, behavior must be byte-identical and the
existing WPF test suite is the proof.

## Work items (ordered)

- [x] 01-core-settings: Extend Core RoeSnipSettings toward the WPF field set (color picker,
      magnifier, text annotation, palette fields) and add JsonStringEnumConverter to
      SettingsStore before any enum-bearing field lands. (S)
      Landed ColorPickerEnabled, RecentPickedColors, the 6 legacy ColorFormatShow* bools,
      MagnifierSampleRadius, the 4 TextFont*/Text* fields, and CustomColors/PaletteColors —
      every no-new-type field this item covers. ColorFormats/ShareProviders/GifSizePreset/
      Mp4SizePreset intentionally NOT ported here (need new types / land with items 11 & 20).
      JsonStringEnumConverter added to SettingsStore ahead of any Core enum field so it's in
      place before one lands. No platform-specific behavior; identical on all three OSes.
- [x] 02-capture-exclusion: Platform capture-exclusion seam (WDA_EXCLUDEFROMCAPTURE on
      Windows, documented no-op elsewhere) applied to the Avalonia OverlayWindow, honoring
      ROESNIP_DIAG_NOEXCLUDE. (M)
      Landed src/RoeSnip.App/AppShell/WindowCaptureExclusion.cs (Apply/ClearOnOwnWindows/
      Restore, OperatingSystem.IsWindows() runtime-guarded so the net8.0 TFM still compiles),
      called from OverlayWindow's Opened handler once the platform handle exists. Live-verified
      on Windows: started a standalone RoeSnip.App instance, triggered its overlay ("capture"
      verb), confirmed every overlay HWND reports GetWindowDisplayAffinity = 0x11
      (WDA_EXCLUDEFROMCAPTURE), then ran a second-process headless --capture and confirmed the
      resulting PNG shows the clean desktop behind the overlay, not the overlay's own dim/
      chrome. Linux/macOS: documented no-op (already recorded under Accepted limitations below)
      — X11/Wayland expose no per-window capture-exclusion API and Avalonia does not surface
      NSWindow.sharingType.
- [x] 03-automation-pipe: Port the --auto JSON automation pipe (protocol, server, client)
      for the overlay-only command subset; distinct pipe name so both residents coexist. (L)
      Landed src/RoeSnip.App/AppShell/AutomationServer.cs (AutomationProtocol/AutomationServer/
      AutomationClient), pipe name "RoeSnip.App-Automation" (distinct from the WPF app's
      "RoeSnip-Automation"). Implements state/trigger/select/escape/screenshot/confirm(copy|save)
      live against new *ForAutomation hooks on OverlayController/OverlayWindow (a static
      s_activeSession mirroring the WPF app's own, cleared on every exit path including the two
      early-return branches that don't call Finish()). record/preset/fps/chrome are still parsed/
      validated (byte-identical wire shape to WPF, for a future recording port) but return a
      structured "unsupported until recording is ported" error, never "unknown command".
      screenshot is routed through this app's own CaptureService/SdrImage/PngWriter pipeline
      (portable) rather than a Windows-only GDI CopyFromScreen — one simplification: an explicit
      rect must fall within a single monitor (WPF's raw desktop grab could span monitors freely).
      --auto intercepted in Program.Main before any single-instance machinery; --automation
      added to AppComposition.RunTray's hidden-flag allowlist. Ported AutomationProtocolTests +
      AutomationClientTests (keeping the abrupt-close disposal-order regression coverage) into a
      new tests/RoeSnip.App.Tests project (net8.0-windows10.0.22621.0, added to the sln) — 54
      tests, all green. NamedPipeServerStream/NamedPipeClientStream are Unix-domain-socket-backed
      on macOS/Linux, so the whole channel is already portable; nothing Windows-only was added.
      Live-verified on Windows: started a standalone --automation instance (never the user's real
      resident, killed after), drove state/trigger/select/record(unsupported)/confirm
      copy/screenshot(bare + rect + includeExcluded)/confirm save/escape end-to-end over the real
      pipe.
- [x] 04-recording-core-extraction: Move the from-scratch GIF pipeline plus
      MultiMonitorRecording and RecordingSizeEstimator (and their tests) into RoeSnip.Core;
      retarget WPF onto the Core copies with byte-identical output. (L)
      Landed RoeSnip.Core/Recording/{GifEncoder.cs,Gif/*} (GifDelta, GifEncoderOptions,
      GifEncoderStageTimings, GifLzwEncoder, GifNearestColorLut, GifOctreeQuantizer,
      GifSizePreset), RoeSnip.Core/Recording/{MultiMonitorRecording.cs,RecordingSizeEstimator.cs,
      Mp4BitrateEstimator.cs} — the last one a new pure-math extraction of Mp4Encoder.ComputeBitrate
      /AudioAacBytesPerSecond, needed so RecordingSizeEstimator's MP4 term has no Vortice/Media
      Foundation dependency. RoeSnip.csproj now references RoeSnip.Core; the WPF app's own
      GifEncoder.cs became a thin wrapper (its own richer SdrImage, with a WPF-only
      ToBitmapSource() and a recording-cadence reuseOutput overload, gets re-wrapped into a Core
      SdrImage per call with a last-buffer cache so no per-candidate-frame allocation is
      reintroduced) and Mp4Encoder.ComputeBitrate/AudioAacBytesPerSecond now delegate to
      Mp4BitrateEstimator — every existing WPF call site and test (Mp4EncoderTests,
      GifEncoderTests, GifSizeBenchmarkTests, GifEncoderStageProfilingTests,
      GifLzwEncoderRealDecoderTests) kept its own source unmodified beyond `using` fixes.
      TWO DELIBERATE DEVIATIONS from a literal move, both because the WPF app's own types are
      genuinely richer than the portable ones this ticket assumed were shared: (1)
      MultiMonitorRecording is NOT deleted from WPF — its MonitorInfo carries a live HMONITOR
      that WgcCapturer/DesktopDuplicationCapturer read directly, which the portable MonitorInfo
      (opaque BackendKey string, for cross-OS use) has no equivalent for; unifying them means
      refactoring the WPF capture layer, out of scope here. Both copies are independently
      unit-tested. (2) GifLzwEncoderRealDecoderTests stays in RoeSnip.Tests, namespace-retargeted
      only — it verifies GifLzwEncoder's output through WPF's GifBitmapDecoder AND GDI+
      (System.Drawing), both PresentationCore/Windows-only, so it cannot run on Core.Tests'
      portable net8.0 TFM without violating "never drag PresentationCore into Core"; it still
      tests the real (now Core) GifLzwEncoder class, just from the WPF-side project. 4 of the 5
      named Gif tests (GifEncoderByteIdentityRegressionTests, GifDeltaBehaviorTests,
      GifQuantizerAndLzwTests, GifSizePresetTests) plus MultiMonitorRecordingTests (MonitorInfo
      construction adapted to BackendKey/Scale) and RecordingSizeEstimatorTests moved into
      RoeSnip.Core.Tests. 714 -> 770 tests total (482 RoeSnip.Tests + 229 Core.Tests + 54
      App.Tests + 5 Platform.Windows.Tests), all green.
- [x] 05-annotation-history: Port AnnotationHistory (Add/Remove/Replace command model with a
      real redo branch) and wire Undo/Redo shortcuts in the Avalonia overlay. (M) AnnotationHistory
      moved into RoeSnip.Core.Overlay verbatim (WPF's own copy is left untouched — only the
      Avalonia AnnotationLayer now shares it, to avoid touching the frozen WPF app). Avalonia
      AnnotationLayer's placement/text-commit paths now route through history.Add instead of a
      bare list.Add, and Undo()/Redo()/Clear() mirror WPF's (minus the Select-tool Deselect()
      calls, since Feature B doesn't exist in the port yet — that's item 07). Ctrl+Z/Ctrl+Y/
      Ctrl+Shift+Z wired in OverlayWindow.axaml.cs's key handler, same bindings as WPF. Redo
      toolbar button is item 08. 19 AnnotationHistoryTests ported verbatim into
      RoeSnip.Core.Tests (against a plain string payload, proving the class stays
      framework-free). No linux/mac degradation — this is pure model logic.
- [x] 06-highlight-pixelate-magnifier: Highlight and Pixelate annotation tools (incl.
      PreviewSource wiring and the pixelate loupe UX) plus magnifier parity (wheel-adjustable
      SampleRadius, fixed footprint, click-to-copy). (L)
      Avalonia AnnotationTool gained Highlight (Freehand polyline, ~35% alpha, 3x width, flat
      caps — Avalonia's Pen has one LineCap for both ends, unlike WPF's separate Start/End, so
      both just get Flat) and Pixelate. AnnotationLayer.PreviewSource now holds the raw frozen
      SdrImage (set once by OverlayWindow right after it builds its own preview bitmap); Pixelate
      rendering is a new RoeSnip.Core.Overlay.PixelateMosaic (box-average shrink + implicit
      nearest-neighbor upscale as flat-colored block rectangles, computed directly rather than
      through WPF's CroppedBitmap+TransformedBitmap+BitmapScalingMode.NearestNeighbor pipeline,
      which Avalonia has no equivalent for) — pure, unit-tested (5 new RoeSnip.Core.Tests),
      identical on screen and at export since both draw through the same AnnotationLayer.Draw.
      ToolbarControl gained Highlight/Pixelate buttons in WPF's exact order (after Freehand,
      before Text), same Focusable=false/always-visible pattern as every other tool button.
      Magnifier: SampleRadius is now wheel-adjustable (1..15, clamped, seeded from
      RoeSnipSettings.MagnifierSampleRadius at OverlayWindow construction) with a genuinely
      FIXED loupe footprint (154 DIP, matching WPF's historical fixed size) — the per-swatch
      size scales instead of the widget; ShowColorReadout suppresses the hex/RGB/nits lines
      (kept as the existing 3 lines; format-catalog-driven lines are item 22) down to just the
      pixel grid, wired to `ColorPickerEnabled && tool != Pixelate` at OverlayWindow's own
      pointer-move handler (WPF OverlayWindow.xaml.cs:1056) — click-to-copy is a Magnifier
      OnPointerPressed override, matching WPF's own OnMouseLeftButtonDown mechanism exactly
      (including its reachability: the window's own tunnel-routed pointer handler pre-handles
      most clicks before they'd reach this, same as WPF's Preview/Bubble split — it's here for
      the cases that fall through, and for the future standalone eyedropper, item 22). Added
      OverlayWindow's first PointerWheelChanged handler (tunnel-routed), deliberately scoped to
      ONLY the loupe-zoom half of WPF's 140-line wheel handler (OverlayWindow.xaml.cs:2253-2394):
      Pixelate tool or no tool active zooms SampleRadius and persists it via a new mutable
      _liveSettings field (renamed from the old readonly _settings) + TrySaveLiveSettings,
      mirroring WPF's own pattern. In-progress/selected-shape stroke and font resizing is
      explicitly NOT ported here — there is no selected-shape or in-progress-stroke-resize
      concept in the port yet (Feature B, item 07) — the wheel handler's doc comment says so
      and item 07 is expected to extend this same handler rather than add a second one.
      Deliberately NOT changed: WPF's drag-based magnifier show/hide gating (IsMagnifierActive,
      OverlayWindow.xaml.cs:287-295) was never ported to Avalonia in an earlier item — the
      Avalonia magnifier already updates unconditionally on every pointer move regardless of
      drag state, which trivially satisfies "visible while dragging a Pixelate region" (it's
      visible during every drag, a strict superset of WPF's narrower rule) without needing new
      gating logic; a full port of WPF's per-drag-mode hide/show belongs with item 07's Feature B
      selection system, which is what actually introduces most of those drag modes. No
      linux/mac degradation — this is pure rendering/model logic, no OS-specific behavior.
      Build + full test suite green (791 tests: 482 WPF + 250 Core + 54 App + 5 Platform.Windows).
      Live-verified on Windows: started a standalone --automation instance (never the user's
      resident, killed after), triggered the overlay, selected a region, and screenshotted the
      desktop (includeExcluded) to confirm the toolbar renders Select/Rect/Ellipse/Arrow/Line/
      Freehand/Highlight/Pixelate/Text in that exact order with the correct icons.
- [x] 07-select-edit: The whole Feature B select/edit subsystem: selection with same-tool
      gate, hit-testing, move/resize/endpoint drag, delete, text re-edit, wheel resize,
      selection chrome with the luminance-based handle fill contrast rule. (XL)
      Ported WPF's AnnotationLayer.cs:308-921 wholesale into the Avalonia AnnotationLayer:
      IsEditable/IsRectResizable/IsClickEditableTool, EditableBounds, HitsShapeBody (with the
      Shift/Ctrl interiorGrab relaxation), HitTestEditable (restrictToTool same-tool gate),
      HitTestSelectedHandle (8 rect handles) and HitTestSelectedEndpoint (Line/Arrow), Select/
      Deselect, BeginDragSelected/EndDragSelected/CommitPendingDrag (snapshot-diff-into-one-
      Replace gesture model — unit-tested proving a 3-notch wheel gesture collapses into ONE
      undo step, not three), TranslateSelected (frame-bounds clamped), SetSelectedRect/
      SetSelectedEndpoint/SetSelectedStrokeWidth/SetSelectedPixelateBlock/SetSelectedFontSize,
      DeleteSelected, ReplaceShape (text re-edit), plus the selection chrome (dashed gold
      outline, flat square handles) and the pixelate gray-dotted placement chrome WPF keeps
      right next to it (806-831) — item 06 hadn't landed that half yet, so it's included here;
      both stay screen-only, excluded from RenderForExport exactly like WPF. The pure geometry/
      color-math slice with zero UI-framework dependency (DistanceToSegment, WCAG
      RelativeLuminance, the near-mid-gray inverse-fill fallback rule) moved into a new
      RoeSnip.Core.Overlay.AnnotationGeometry, unit-tested directly (9 new RoeSnip.Core.Tests);
      the WPF app keeps its own inline copy of this same math untouched. AnnotationShape's
      StrokeWidthPx is now a mutable setter (was init-only) and gained Clone(), mirroring WPF's
      own field exactly — Feature B's snapshot-before/diff-after gesture model needs both.
      OverlayWindow.axaml.cs: DragMode gained SelectedMove/SelectedResize/SelectedEndpoint
      (WPF's Spanning* modes have no port yet, item 09); the pointer-pressed click-priority
      chain, pointer-move drag routing, and pointer-released gesture-end handling all mirror
      WPF's (modifier-grab via PointerEventArgs.KeyModifiers — Avalonia's direct analog of
      WPF's Keyboard.Modifiers). Delete/Back deletes a selection, Esc deselects before
      escalating to CancelStage, double-click on a Text shape reopens the inline editor as a
      Replace (BeginTextReEdit, reusing BeginTextEditor's machinery with an editingExisting
      param). The wheel handler now covers the full WPF range (2253-2345): active-text-editor
      font resize, mid-drag SetInProgressStrokeWidth, and the selected-shape branch funneled
      through BeginDragSelected/CommitPendingDrag, falling through to the existing loupe-zoom
      only when none of those apply. Toolbar's ToolSelected handler now commits any open text
      editor first and drops a tool-mismatched selection on a drawing-tool switch, matching
      WPF's ShowToolbar wiring. Two deliberate degradations from the cursor vocabulary gap
      (both Windows/Linux/macOS-uniform, not OS-specific): Avalonia's StandardCursorType has no
      generic diagonal resize cursor, so the Line/Arrow endpoint cursor's diagonal buckets reuse
      TopLeftCorner/TopRightCorner instead of dedicated SizeNWSE/SizeNESW glyphs; and there is
      no ToolCursorCache-style custom per-tool brush cursor bitmap in this port (a drawing tool
      still shows a plain crosshair, same as before this item), so only the hand/handle/endpoint
      affordance over an already-placed shape was added on top of it. SizeInput's numeric clamp
      ranges (1-32px stroke, 6-96pt font) are duplicated as local consts pending item 08's actual
      SizeInput port. Live-verified on Windows: standalone --automation instance (killed after),
      triggered the overlay, made a selection, screenshotted (includeExcluded) confirming the
      toolbar (Select tool checked) and crop-selection adorner render cleanly with the new
      AnnotationLayer/OverlayWindow code in the render/input path; full click-drag annotation
      interaction (placing/selecting/resizing a shape) has no automation-pipe surface to drive
      it and remains an interactive-session verification gap, same category as TESTING.md's
      existing "Overlay parity A/B against the frozen WPF app" item. No linux/mac degradation —
      this is pure pointer/keyboard/rendering logic with no OS-specific behavior; the two cursor-
      vocabulary approximations above apply identically on every OS.
      Build + full test suite green (817 tests: 482 WPF + 260 Core + 70 App + 5 Platform.Windows).
- [x] 08-toolbar-parity: Popup-safe toolbar hit routing first, then the 10-slot persisted
      palette with right-click Replace, numeric size ComboBox (SizeInput), text style row
      (Bold/Italic + font family on the shape model), and the Redo button. (L)
      OverlayWindow.IsWithin (was toolbar-only visual-parent walk) now mirrors WPF's
      IsWithinToolbar exactly: prefers the LOGICAL parent at each step (Avalonia.Visual itself
      implements ILogical, so this needed no new bridging), falls back to visual, and treats a walk
      that dead-ends outside the window as popup chrome (the size/font ComboBox dropdowns, the
      palette's right-click menus, the Replace flyout all render in a disconnected PopupRoot, same
      as WPF's own Popup) — this had to land first since every popup-bearing control below depends
      on it. Also converted to an instance method + reused to hand keyboard focus back to the
      window on any toolbar click that isn't into the size box (mirrors WPF OverlayWindow.xaml.cs
      :706-717). SwatchPalette.cs and SizeInput.cs ported verbatim into RoeSnip.App/Overlay (both
      framework-free, so — like item 05's AnnotationHistory — this is a parallel copy, not a shared
      Core extraction; WPF's own copies stay untouched). Palette: ToolbarControl.SetPaletteColors
      rebuilds the 10-slot row from SwatchPalette.EffectivePalette every toolbar show; each swatch's
      ContextMenu has exactly one "Replace..." item, which shows a new ColorReplaceFlyout (a
      DefaultColors quick-pick grid + a typed "#RRGGBB" TextBox) — the closest in-toolkit
      substitute for WPF's System.Windows.Forms.ColorDialog, which has no Avalonia equivalent.
      OverlayWindow.OnPaletteReplaceRequested/CurrentEffectivePalette/UpdatePalette mirror WPF's own
      persistence pattern (PaletteColors written immediately via SettingsStore). One deliberate wire
      deviation: ToolbarControl.PaletteReplaceRequested carries (index, Color) instead of WPF's
      index-only shape, because the Flyout is anchored async UI only the control holding the swatch
      can show, unlike WPF's blocking modal dialog OverlayWindow itself could invoke. Size: the
      3-dot StrokeWidthPanel is gone; SizeComboBox is a real Avalonia IsEditable ComboBox (px for
      drawing tools, pt for Text) driven by the ported SizeInput's clamp/parse/format/presets,
      wired the same way WPF's does (commit on Enter/focus-loss via IsKeyboardFocusWithinProperty,
      revert on Esc, apply-and-return-focus on a preset pick). The wheel handler's fall-through tail
      (OverlayWindow.xaml.cs:2382-2391) — pre-dialing the DEFAULT stroke/font size for the next
      shape when nothing is selected/mid-drawn — was also completed here (item 07 had stopped short
      of it), now syncing the toolbar's own size box on every wheel-driven size change everywhere in
      the handler. Text style: AnnotationShape/AnnotationLayer gained TextFontFamily/TextBold/
      TextItalic (Clone, ShapeContentEquals, CommitText, and Draw's Typeface all updated) mirroring
      WPF's AnnotationLayer.cs:61-84 exactly; OverlayWindow gained the _textFontFamily/_textFontSize
      /_textBold/_textItalic fields WPF has always had (seeded from RoeSnipSettings, previously
      entirely absent from this port), wired through BeginTextEditor/CommitActiveTextEditor and the
      toolbar's new FontSizeSelected/BoldToggled/ItalicToggled/FontFamilySelected events exactly
      like WPF's ShowToolbar wiring. The font-family ComboBox lists FontManager.Current.SystemFonts
      (Avalonia's FontFamily.Name is already the best cross-platform display name — no
      XmlLanguage/FamilyNames localization lookup needed, that was WPF-specific plumbing); the
      per-row live "AaBb" font preview WPF's FontFamilyItemTemplate renders was deliberately not
      ported (plain text labels only) — cosmetic-only simplification, noted rather than gold-plated.
      Redo: a fixed-position sibling of Undo (both in the Row-1 Auto|*|Auto Grid's right column,
      same as WPF), wired to AnnotationLayer.Redo(); SetHistoryState grays both from the existing
      HistoryChanged event (item 05). Restructured ToolbarControl.axaml into WPF's exact 3-row shape
      (tools+size | undo+redo; palette | Copy/Save/SaveHdr/Cancel; collapsed text-style row) without
      touching Record/Share (items 21/12's territory — no placeholders added) or the existing Avalonia
      flat-dark styling (the RL design-token system WPF's ToolbarControl.xaml carries is item 16's
      job, not this one's). Build + full test suite green (863 tests: 482 WPF + 260 Core + 116 App +
      5 Platform.Windows — +46 in App.Tests: ported SwatchPaletteTests/SizeInputTests verbatim
      against this port's own copies, plus a small AnnotationShapeTextStyleTests for the new shape
      fields). Live-verified on Windows: started a standalone --automation instance (killed after),
      triggered the overlay, selected a region, and screenshotted (includeExcluded) to confirm the
      toolbar renders the 2 visible rows correctly (9 tool icons, "4px" size box with chevron,
      grayed Undo+Redo, the 10-swatch palette, Copy/Save/SaveHDR/Cancel) with the Text-tool-only
      third row correctly collapsed; real click-drag interaction with the size box/palette
      right-click/text-style row has no automation-pipe surface to drive it and remains an
      interactive-session verification gap, same category as item 07's own note. No linux/mac
      degradation — this is pure UI/rendering/model logic with no OS-specific behavior; the font
      list naturally differs per OS (FontManager.Current.SystemFonts enumerates whatever's actually
      installed there), which is correct behavior, not a gap.
- [x] 09-spanning-selection: Multi-monitor spanning selection (SpanningSelectionMath to Core,
      per-window distribution, real-edge handles) plus the spanning HDR .jxr export. (XL)
      SpanningSelectionMath.cs ported verbatim (Distribute/ComputeVirtualDesktopBounds/
      ClampToVirtualDesktop/SlideToBounds/ApplyResize + SelectionEdges/SpanningHit/
      SpanningDistribution) into RoeSnip.Core.Overlay, with SelectionHandle moved there too (was
      App-only) so both ports' adorners and the shared resize math reference one enum; 20 unit
      tests ported into RoeSnip.Core.Tests, all passing with no WPF Window involved. Reworked
      OverlayController.OverlaySession off the one-monitor model: session-level `_spanningVirtual`/
      `_spanningPrimaryWindow`, `OnSpanningCandidate` (the shared distribute+redistribute primitive
      every NewSelection/SpanningResize/SpanningMove drag funnels through), `FinalizeNewSelectionDrag`,
      `RenderSpanningSelection` (byte-composite of already-tone-mapped per-window crops, opaque-black
      gap fill), `ConfirmSpanningAsync`, spanning-aware `GetSelectionForAutomation`/
      `SetSelectionForAutomation`/`ConfirmSaveForAutomation`. OverlayWindow gained `SpanningResize`/
      `SpanningMove` drag modes (hit-tested through `SelectionAdorner.HitTestHandle`, gated by
      `RealEdges` so a handle only appears on a genuine selection edge, never a monitor-boundary
      clip), `SetSpanningLocalSelection`/`NotifySpanningDragEnded`, toolbar suppression during a live
      spanning drag on non-owner windows. `SelectionAdorner` gained `RealEdges`/`SuppressBadge`/
      `OverrideSizeLabel`. `ToolbarControl.SetSpanningMode` collapses the tool/undo-redo/palette rows
      while spanning (Copy/Save/Save HDR/Cancel stay live). HDR: `JxrWriter.WriteSpanning`/
      `BuildSpanningFloatBuffer` ported into RoeSnip.Platform.Windows (stitches each contributing
      monitor's raw scRGB crop via `ReadPixelScRgb`, linear-black gap fill, same WIC 128bppRGBAFloat
      encode `Write` uses), a new `RoeSnip.Core.Capture.SpanningFrameCrop` type (Core, not App, so
      Platform.Windows can reference it without depending on App), `AppComposition.
      WriteHdrExportSpanning` hook wired in `Program.RegisterPlatformHooks` alongside the existing
      single-monitor hook, and the HDR-export branch in `RunCaptureFlowAsync` now branches on
      `OverlayResult.SpanningVirtualSelectionPx`. 4 new WIC round-trip tests
      (`JxrSpanningRoundTripTests.cs`, ported into `RoeSnip.Platform.Windows.Tests`) cover headroom
      survival across two monitors' offsets, the gap fill, a degenerate Bgra8Srgb crop, and the
      out-of-bounds-crop throw. Build + full solution test suite green (887 tests: 482 WPF +
      280 Core + 116 App + 9 Platform.Windows). Live-verified on Windows via the automation pipe
      against this machine's real 3-monitor HDR layout (a standalone `--automation` instance started
      and killed by this item's own session): `select` with a rect straddling the portrait DISPLAY1/
      landscape DISPLAY3 seam echoed back the full unclipped virtual rect (not a per-window slice),
      `confirm copy`/`confirm save` produced a correctly-dimensioned (600x400) stitched PNG, and
      (after restarting the standalone instance with `AutoSaveHdrCopy` temporarily flipped in its own
      settings.json, then restored byte-for-byte afterward) a spanning `.jxr` was written alongside
      the PNG confirming the HDR hook fires end to end. Record (MP4/GIF) for a spanning selection was
      NOT ported — RoeSnip.App has no Recording subsystem at all yet (a separate, not-yet-landed
      item), so there is nothing for a spanning selection to integrate with; this mirrors the WPF
      app's own history where spanning-record required its own separate `spanning-recording` work
      track. Non-Windows (linux/mac net8.0 TFM): spanning SDR selection/render/Copy/Save all build
      and run identically (no OS-specific code in the distribute/render path); spanning HDR save
      stays reported "not available on this platform/build" via the existing `WriteHdrExportSpanning
      is null` branch, exactly as the single-monitor HDR path already degrades — no new gap
      introduced. Mixed-DPI is untested (this machine is all-96-DPI), matching the WPF reference's
      own documented v1 cut.
- [x] 10-capture-hot-path-perf: WGC capturer pre-provisioning/caching/keepalive/Prewarm in
      Platform.Windows plus the ToneMapper LUT + AVX2 fast path (with reuseOutput) in Core,
      byte-identical to the scalar reference. (L)
      Platform.Windows/WgcCapturer.cs ported verbatim from src/RoeSnip/Capture/WgcCapturer.cs:
      per-monitor MonitorSlot cache (device + GraphicsCaptureItem reused, session/framepool
      created fresh per capture so the yellow border never persists), retry-once-on-failure
      against freshly provisioned resources, a 10s background keepalive timer that proactively
      re-provisions a TDR-dead cached device off the hot path, `Prewarm(monitor, throwawayFrame)`
      and `TrimCachedDeviceMemory()` static hooks for item 17's warmup/IdleMemoryTrimmer to call
      once landed. Mechanical adaptation only: HMONITOR comes from
      `MonitorEnumerator.ParseHMonitor` (Core's MonitorInfo carries BackendKey, not a raw handle)
      instead of being stored directly. Windows-only file (Platform.Windows) — no linux/mac
      degradation to note; WGC/DXGI have no equivalent on those OSes and their own capturers are
      unaffected.
      Core/Color/ToneMapper.cs ported verbatim from src/RoeSnip/Color/ToneMapper.cs: per-scale
      65536-entry LUTs (Linear/Encoded), AVX2-vectorized frame-max scan with an exact FP16->FP32
      widening and scalar NaN-block fallback, and the `reuseOutput` overload for item 20's
      recording cadence. One behavioral generalization carried over from the existing Core port:
      the scale step reads `1.0 / frame.SdrWhiteInBufferUnits` instead of the WPF app's hardcoded
      `80.0 / Monitor.SdrWhiteNits` (algebraically identical on Windows, correct for macOS EDR).
      The AVX2 path now runtime-checks both `Avx2.IsSupported` and
      `Vector256.IsHardwareAccelerated` (WPF only checked the former) so the scalar MaxScalarPixels
      loop is what actually ships on the arm64 macOS target; MapToSdrScalar stays byte-identical
      on every platform since it has no SIMD in it. New ToneMapperEquivalenceTests.cs in
      RoeSnip.Core.Tests (ported from tests/RoeSnip.Tests/ToneMapperEquivalenceTests.cs, plus one
      new EDR-convention case and one reuseOutput case) proves optimized == scalar byte-for-byte
      across pass-through, shoulder, adversarial bit-pattern, and full 1440p frames; all green on
      this (x64) machine, and structurally correct (pure runtime IsSupported branching, no
      platform-conditional compilation) for the arm64 fallback.
- [x] 11-sharing-core: Move the Sharing subsystem (ProviderSpec, ShareManager, catalog,
      template/url extraction) into RoeSnip.Core, rewrite ShareTestImage without
      System.Drawing, retarget WPF, add the settings fields. (L)
      Moved all 9 src/RoeSnip/Sharing/*.cs files into RoeSnip.Core/Sharing (namespace
      RoeSnip.Core.Sharing, mirroring the Capture/Color/Imaging extraction pattern), deleted the
      WPF originals, and retargeted every WPF call site (Program.cs, App/SettingsWindow.xaml.cs,
      App/ShareProvidersWindow.xaml.cs, App/ShareProviderEditWindow.xaml.cs,
      Overlay/OverlayController.cs, Overlay/OverlayWindow.xaml.cs,
      Recording/RecordingController.cs) onto the Core copies — one implementation, zero
      duplication. One deliberate API change while porting: ShareManager.EffectiveConfigs/
      ResolveDefault now take the ShareProviders list / DefaultShareProviderId directly instead
      of a whole RoeSnipSettings object — a shared Core facade can't depend on either app's own
      settings record shape (the WPF RoeSnipSettings in Program.cs and RoeSnip.Core.Settings.
      RoeSnipSettings are two distinct records by design, per the two-settings-files split), so
      every call site now passes settings.ShareProviders/settings.DefaultShareProviderId
      explicitly; behavior (default-resolution order, fallback rules) is unchanged. ShareTestImage
      was rewritten against Core's own SdrImage + SkiaSharp PngWriter (a per-pixel diagonal
      gradient) instead of System.Drawing/GDI+ (Windows-only since .NET 7, incompatible with
      Core's portable net8.0 TFM) — not pixel-identical to the old GDI+ output, but produces the
      same 32x32 fully-opaque gradient PNG shape the Test button's real upload pipeline needs; a
      valid decodable PNG is the only actual contract (see that class' own doc comment). Added
      ShareProviders (List<Sharing.ShareProviderConfig>) and DefaultShareProviderId to
      RoeSnip.Core.Settings.RoeSnipSettings — item 01's JsonStringEnumConverter (added ahead of
      time for exactly this) makes ShareUploadKind/ResponseUrlMode persist as JSON strings, same
      as the WPF settings file. The WPF RoeSnipSettings record itself (Program.cs) was left
      untouched apart from retargeting its ShareProviders field's element type from the
      now-deleted RoeSnip.Sharing.ShareProviderConfig to RoeSnip.Core.Sharing.ShareProviderConfig
      — an unavoidable consequence of deleting the duplicate, not a settings-shape/schema change;
      the two apps' settings.json files remain fully independent. Ported all 6 WPF Sharing test
      files (63 test methods: ProviderSpecShareProviderTests, ResponseUrlExtractorTests,
      ShareManagerTests, ShareProviderCatalogTests, ShareProviderSettingsPersistenceTests,
      TemplateExpanderTests) into RoeSnip.Core.Tests/Sharing verbatim except ShareManagerTests
      (rewritten against the new list/id-based ShareManager signature) and
      ShareProviderSettingsPersistenceTests (now round-trips RoeSnip.Core.Settings.RoeSnipSettings
      via Core's own SettingsStore instead of the WPF one); added ShareTestImageTests (2 new
      tests: decodable 32x32 PNG, opaque + genuinely gradiented) — 65 Sharing tests total now
      live in RoeSnip.Core.Tests/Sharing. SettingsTests.cs's two whole-record round-trip tests
      were extended to neutralize the new ShareProviders list field the same way they already do
      for RecentPickedColors/CustomColors/PaletteColors (List<T> reference-equality quirk, not a
      regression). Build + full solution test suite green (902 tests: 419 RoeSnip.Tests + 358
      Core.Tests + 116 App.Tests + 9 Platform.Windows.Tests). No linux/mac degradation: the whole
      Sharing subsystem is pure BCL (HttpClient,
      System.Text.Json, System.Text.RegularExpressions) plus Core's own portable SkiaSharp-backed
      imaging — it already builds and behaves identically on every OS RoeSnip.Core targets. No UI
      wiring changed in RoeSnip.App (still has no Sharing surface) — that is item 12's job.
- [x] 12-sharing-ui: Provider management windows, Settings entry point, and the overlay
      Share split button + per-provider dropdown wired into OverlayController upload
      flows. (M)
      Ported ShareProvidersWindow (master list, code-built rows, Enabled toggle persists
      immediately, Configure.../Custom... open the edit window) and ShareProviderEditWindow
      (per-provider ConfigFields form, real Test button via ShareTestImage/ShareManager) to
      Avalonia — Avalonia has no synchronous ShowDialog (it's a Task), so the WPF "ShowDialog();
      RefreshList();" straight line became an awaited OpenEditWindow; no PasswordBox (TextBox.
      PasswordChar instead); no MessageBox (an inline ValidationErrorText for the Save-endpoint
      guard, a small owned Yes/No dialog for Remove, mirroring TrayApp's own precedent). Settings
      window gained a "Sharing" section (default-provider combo + Providers... button) with the
      same _current/_original split the WPF window uses so ShareProvidersWindow's immediate
      self-persist is picked up on close without discarding this window's own in-progress edits.
      ToolbarControl gained the Share split button (default-provider click + a separately
      always-visible, DISABLED-not-hidden chevron dropdown when zero providers are configured,
      preserving d8fa815) via a MenuFlyout (Avalonia's Popup-rooted dropdown — OverlayWindow's
      existing item-08 popup-safe IsWithin walk already covers it, no extra plumbing needed).
      Ported OverlayController.ShareCurrentSelection/ShareToSpecificProvider verbatim (render
      through the same RenderSelectionWithAnnotations/RenderSpanningSelection path Copy uses,
      resolve the provider against the SAME OverlayWindow.LiveSettings snapshot the dropdown was
      populated from, detached ShareManager.UploadAsync, URL to clipboard via
      ClipboardService.TryCopyTextAsync, tray balloon via a new ITrayNotifier.
      ShowShareUploadedBalloon, overlay stays open through the whole upload) — Dispatcher.
      BeginInvoke became Dispatcher.UIThread.Post (this session runs on the Avalonia UI thread
      already, but the upload's continuation resumes on a thread-pool thread after
      ConfigureAwait(false)). Also wired the `--auto confirm share` automation action (WPF already
      had it; this port's own automation harness had explicitly deferred it pending this item) —
      AutomationProtocol.ValidateArgs and OverlaySession.ConfirmForAutomation both updated, plus
      the one AutomationProtocolTests case that asserted the old rejection is now a positive test.
      Verified live via the --auto pipe (own automation-enabled instance, own AppData profile,
      started and killed by this item's own session, never the user's real resident): toolbar
      Share button/chevron render disabled with zero providers configured (screenshot), enable
      once a settings.json ShareProviders entry is added and the resident restarted (screenshot),
      go busy mid-upload and re-enable after a real (deliberately failing — example.invalid) HTTP
      attempt, `confirm action:share` with no provider configured surfaces "Share failed: no share
      provider is configured." without closing the overlay, SettingsWindow's new Sharing section
      renders with the disabled "No provider configured yet" combo. ShareProvidersWindow/
      ShareProviderEditWindow's own rendering was not driven interactively (would need synthetic
      mouse/keyboard input this session avoided per the no-synthetic-input rule) — verified via
      build/compile of both TFMs plus code review instead, matching the WPF app's own item-11
      precedent of "unit tests only, no live UI" for these exact two windows. Build + full
      solution test suite green (902 tests unchanged in count: one AutomationProtocolTests case
      renamed from a rejection assertion to an acceptance assertion, no net new/removed test). No
      linux/mac degradation: Sharing/* is pure BCL already portable via item 11; this item's own
      UI (ToolbarControl, the two new windows, OverlayController) is ordinary Avalonia control code
      with no OS-specific branches.
- [x] 13-install-self-update: Single-instance replace-on-run takeover (InstanceSignal.Exit),
      Windows install-to-LOCALAPPDATA + GitHub Releases self-update (own asset name, swap
      discipline, ApplyUpdateLock, idle gate, --self-update-now), version surfaced in
      About/tooltip, passive new-version notice on Linux/macOS. (XL)
      SingleInstance.cs gained InstanceSignal.Exit + TryTakeOver (WPF TrayApp.cs:75-92 semantics):
      a plain (no-flag) launch now sends Exit and waits up to 3s for the mutex before force-
      terminating as a last resort, replacing the old (buggy) behavior where a bare relaunch
      signalled TriggerCapture at the OLD process and exited. An explicit CLI verb ("capture"/
      "settings") still just signals the resident without replacing it — RunResident branches on
      InstanceSignal.None specifically. KillOtherInstances discriminates candidates by their own
      MainModule exe path (matching either this process's own path or UpdateManager.InstalledExePath),
      NEVER by process name — RoeSnip.App's AssemblyName is "RoeSnip", identical to the WPF app's,
      so Process.GetProcessesByName("RoeSnip") returns both products' processes on a machine
      running both; a by-name kill would have murdered the user's separate WPF resident.
      AppShell/UpdateManager.cs (new) ported from src/RoeSnip/App/UpdateManager.cs: installs to
      %LOCALAPPDATA%\RoeSnip.App (distinct from the WPF app's \RoeSnip), Run key value
      "RoeSnip.App" (matches StartupManager's own value name), the same .old/.new atomic swap +
      pending-source-cleanup-marker discipline, and the same static ApplyUpdateLock serializing
      concurrent triggers. Portable/Windows-only split: CurrentVersion(Text)/InstallExists/
      IsInstalled/CheckForUpdateAsync/ParseUpdateInfo compile and run on every OS (no
      [SupportedOSPlatform] — an Assembly version read and a GET to the GitHub API are OS-agnostic);
      Install/ApplyUpdateAsync/CleanupStale*/ProcessPendingSourceCleanup are attributed
      windows-only and TrayApp only ever calls them behind `OperatingSystem.IsWindows()`. Release
      asset name is "RoeSnipApp-win-x64.exe" — deliberately NOT "RoeSnip.exe" (the WPF app's own
      asset); ParseUpdateInfo's `requireWindowsAsset` gate refuses to report an update on Windows
      when that exact asset is missing from the release (mirrors the WPF reference's original
      all-or-nothing gate), while the Linux/macOS passive-notice caller passes false and gets a
      notice from Version/ReleaseUrl alone, never touching DownloadUrl. release.yml's build-windows
      job now also publishes+renames+uploads this asset (win-x64.pubxml, same SelfContained/
      PublishSingleFile/ReadyToRun-off shape as the WPF app's own profile); the prep job bumps both
      apps' <Version> together so one release covers both. Program.cs gained
      AppComposition.IsCaptureBusy (portable poll of the existing s_captureInProgress flag) for the
      self-updater's beforeLaunch idle gate — WaitForIdleAsync polls it every 15s, same shape as
      the WPF reference's CaptureGate/RecordingController poll; a comment flags that once Recording
      lands (item 20) this gate must also poll its own "active" flag. Tray menu on Windows: "Install
      RoeSnip" gated on !InstallExists (evaluated once at menu-build time, same as WPF), "Check for
      updates" always present, both wired via named method-group handlers (not inline lambdas) so
      the platform-compat analyzer can see the Windows-only calls are genuinely guarded — an inline
      lambda closure hides the enclosing `if (OperatingSystem.IsWindows())` from CA1416's flow
      analysis, which cost real (now-fixed) warnings during this item. --self-update-now added to
      Program.cs's HiddenFlags allowlist, intercepted in TrayApp.Run before any single-instance
      machinery, exactly like the WPF reference. Version (item 13c): CurrentVersionText now in the
      tray tooltip and About text on every OS (RoeSnip.App.csproj gained a matching <Version>
      element, none existed before this item). Linux/macOS (item 13d): CheckForNewVersionPassivelyAsync
      runs at startup unconditionally (no InstallExists/IsInstalled gating — those are Windows-only
      concepts), shows a toast linking straight to the GitHub release page on click, never
      auto-applies or offers an Install/Check-for-updates menu item — accepted limitation, already
      listed below. Tests: 20 new UpdateManagerTests.cs cases exercise ParseUpdateInfo's full
      gating matrix (newer/same/older version, v-prefix/bare-tag parsing, missing tag_name,
      unparseable version, Windows-asset-required vs. not-required, case-insensitive asset name
      matching, the "must not match the WPF app's own RoeSnip.exe asset" regression case, html_url
      vs. constructed-fallback release URL, InstallDir identity, CurrentVersionText's no-revision
      shape) with zero network/OS dependency — `requireWindowsAsset` is an explicit parameter
      (rather than reading OperatingSystem.IsWindows() internally) specifically so both the Windows
      and the Linux/macOS-passive-notice code paths are testable from this one Windows-hosted test
      project. The install/registry/exe-swap mutation paths are reviewed by eye, not unit-tested,
      matching the WPF reference's own documented precedent (mutates the real registry/filesystem,
      talks to a real HTTP endpoint). Build + full solution test suite green (919 tests: 419
      RoeSnip.Tests + 358 Core.Tests + 133 App.Tests + 9 Platform.Windows.Tests — +17 net new in
      App.Tests vs. item 12's count). Live-verified on Windows, this machine, standalone instances
      started and killed by this item's own session (never the user's real resident, confirmed none
      running before/after): (1) replace-on-run — started instance A with --automation (probed live
      over its automation pipe to confirm residency), then launched a PLAIN instance B with no args;
      within 4s A's PID had exited and B alone remained resident, and a follow-up pipe probe against
      B timed out (confirming B is genuinely a fresh non-automation resident, not a leftover A).
      (2) The real download+atomic-swap discipline — published two distinct self-contained
      single-file win-x64 builds (v1.0.0 "old", v2.0.0 "new"), seeded %LOCALAPPDATA%\RoeSnip.App
      with the old build, served the new build's bytes from a local loopback HTTP listener (never
      the real GitHub API, nothing pushed), and called UpdateManager.ApplyUpdateAsync directly
      against that URL: the installed exe's SHA-256 matched the new build exactly afterward, the
      swapped-out old exe was atomically renamed to .old, Process.Start actually launched the new
      exe as a real running process from the installed path (confirming the published single-file
      asset format is genuinely runnable end to end, not just byte-swapped), and that new process's
      own startup then cleaned up the .old file itself (CleanupStaleUpdateFiles firing correctly on
      the newly-launched build). All test artifacts (install dir, settings dir, HKCU Run key
      absence, spawned process) were verified clean and removed afterward. NOT separately exercised
      live: the single combined scenario of an actual resident calling ApplyUpdateAsync on itself
      and handing off via replace-on-run in one continuous run — the two pieces above independently
      prove each half (replace-on-run takeover; real HTTP download+swap+successful relaunch) and
      their interplay is a straightforward composition of both, reviewed by eye rather than staged
      live, since the real trigger path (CheckForUpdatesOnStartupAsync hitting the real GitHub API)
      cannot be exercised without either a live published release with this exact asset name or
      debug-only URL-injection plumbing, both out of scope here. --self-update-now was not driven
      live for the same reason (it always calls the real GitHub API, never a mock).
- [x] 14-shell-parity-batch: Hotkey rebind fixes (suspend live hotkey, PrintScreen
      keyup-only capture, real key names), WM_SETTINGCHANGE broadcast after the PrtScr
      consent registry write, competing-screenshot-tool startup warning, ColorPickerEnabled
      toggle, .ico tray/window branding. (M)
      - SettingsWindow now takes suspendGlobalHotkey/resumeGlobalHotkey callbacks from TrayApp
        (Unregister/Register on the same HotkeyManager instance, not Dispose), suspended on
        Change click and resumed on commit or window close - same Bugs 2/5 fix shape as WPF.
        Added an OnKeyUp override (Key.Snapshot only) alongside OnKeyDown, both funneling into
        one CommitCapturedKey, for the PrintScreen keyup-only quirk.
      - DescribeVirtualKey moved to a new public HotkeyDisplayFormat class (testable without an
        InternalsVisibleTo edit, matching this codebase's convention) and inverts
        HotkeyManager.VirtualKeyToKeyCode's own SharpHook KeyCode names (stripping the "Vc"
        prefix) instead of P/Invoking GetKeyNameText, which this port has no HWND to hang off -
        raw hex remains the last-resort fallback for an unmapped vk.
      - ResolvePrintScreenConsentAsync now broadcasts WM_SETTINGCHANGE (Windows-only P/Invoke,
        [SupportedOSPlatform("windows")]) after writing PrintScreenKeyForSnippingEnabled=0, same
        as the WPF app.
      - WarnIfPrintScreenConflict ported verbatim (same KnownPrintScreenApps list) and called
        once at startup after the hotkey is (re)armed; surfaces via the existing toast
        (isError:true) since Avalonia's TrayIcon has no balloon-icon-kind API to distinguish a
        warning from an error.
      - ColorPickerCheckBox added to SettingsWindow.axaml/.axaml.cs (load + save), same copy as
        WPF; the feature itself still arrives in item 22.
      - Assets/roesnip.ico copied into RoeSnip.App as an AvaloniaResource; a cached AppIcon
        (avares:// load, falling back to the existing procedural glyph on any failure) is now
        used for the tray icon and the Settings/About/toast windows.
      - Verified live on this machine: a standalone `--automation` RoeSnip.App instance (never
        the user's resident) opened Settings via the CLI verb; a real-desktop screenshot
        confirmed the ColorPicker checkbox text, the "PrintScreen" hotkey display text (proving
        HotkeyDisplayFormat's inverted-table lookup), and the bundled .ico rendering correctly in
        the window's titlebar (not the procedural fallback) - decoded byte-for-byte the same
        orange/black glyph as the source .ico. The instance was killed immediately after
        (verified no RoeSnip process left running).
      - NOT verified live: actually pressing the physical PrintScreen key to exercise the
        OnKeyUp capture path. This port's HotkeyManager uses a SharpHook global hook, not
        RegisterHotKey - it does not claim/consume the key - but this machine's frozen WPF
        RoeSnip app (when resident) DOES use RegisterHotKey for a real bare-PrintScreen binding,
        so a synthetic PrtScr keypress risks firing that app's actual capture flow if it were
        ever resident, which the hard rule against signalling the user's resident processes
        forbids. Verified instead via the DescribeVirtualKey/DescribeHotkey unit tests
        (tests/RoeSnip.App.Tests/HotkeyDisplayFormatTests.cs) plus code review against the WPF
        reference this was ported from line-for-line - same precedent TESTING.md's item-12
        entry already set for windows outside the automation pipe's reach.
- [x] 15-elevated-startup: Port ElevationManager (schtasks run-at-logon task via one-time
      UAC, error relay file, Settings checkbox + status text, hidden CLI verbs); hidden
      entirely on non-Windows. (L)
      - AppShell/ElevationManager.cs: token-elevation check, schtasks /create /delete /query,
        the cross-process error-relay temp file, and the two hidden CLI verbs
        (RunEnableElevatedStartupCli/RunDisableElevatedStartupCli), ported line-for-line from
        src/RoeSnip/App/ElevationManager.cs. DELIBERATE deviation: TaskName is "RoeSnip.App"
        (not the WPF app's "RoeSnip") and the relay file is "RoeSnip.App-elevate-error.txt" -
        same per-app-identity split as StartupManager's Run-key value name and UpdateManager's
        install dir, so both apps' Scheduled Tasks/UAC round-trips can never collide.
      - Program.cs intercepts --enable-elevated-startup/--disable-elevated-startup directly in
        Main, before any single-instance machinery - same placement as the WPF app's own
        Program.cs - so a runas-relaunched child never gets routed through CliOptions.Parse or
        RunTray's own hidden-flag allowlist (that allowlist is for tray-launch-compatible flags
        like --automation; these two are one-shot verbs that must exit immediately instead).
      - The elevated exe path prefers UpdateManager.InstalledExePath when item 13's install
        exists, else Environment.ProcessPath (ElevationManager.ResolveTargetExePath, unit
        tested - the one pure/portable slice of this class, everything else code-reviewed
        rather than unit-tested per the same call StartupManager's own doc comment already
        makes).
      - SettingsWindow: "Run as administrator" checkbox (in-process toggle when already
        elevated, else a runas relaunch with the matching hidden verb, awaited via
        Process.WaitForExitAsync), elevation status text, and a "Restart elevated now" button
        - plus the WPF app's own post-enable "restart now?" prompt, reusing TrayApp's
        ShowYesNoDialogAsync (made internal for this). Avalonia's CheckBox has one
        IsCheckedChanged event instead of WPF's separate Checked/Unchecked, so the two WPF
        handlers collapse into one. The Run-key/task interplay (task installed -> Run key
        stays cleared, "Start with Windows" shown checked+disabled with a hint) is ported
        unchanged, including SaveButton_Click's own re-check of IsElevatedTaskInstalled.
      - Linux/macOS: ElevatedStartupSection.IsVisible is set in code-behind based on
        OperatingSystem.IsWindows() - the whole section is HIDDEN, not just disabled, since
        Scheduled Tasks and UIPI have no portable equivalent. Accepted limitation.
      - Verified live on this machine: `dotnet build`/`dotnet test` green (whole solution,
        including the untouched WPF app); `schtasks /query /tn "RoeSnip.App"` confirmed absent
        before and after; running the built RoeSnip.exe directly with
        --enable-elevated-startup and --disable-elevated-startup (both unelevated, no UAC
        accepted) exercised the real hidden-verb dispatch end to end - each correctly refused
        with the "must be run elevated" message, on both stderr AND the error-relay temp file
        (which was then deleted), and neither call touched the Scheduled Task. NOT verified
        live: the actual UAC-accepted round-trip (schtasks /create succeeding and the task
        appearing) - that needs an interactive secure-desktop consent click this agent has no
        tool access to perform; the code path itself is a line-for-line port of the WPF
        reference's already-shipped, real-world-exercised ElevationManager/SettingsWindow logic.
- [ ] 16-visual-parity: Design-token theming (near-black surfaces, one #FF6B35 accent, OLED
      black Settings background) replacing the generic dark palette, plus per-tool
      color/width-aware bitmap cursors. (L)
- [ ] 17-perf-startup-idle: Warmup-thread slice of the instant-dim architecture (pre-JIT
      capture/tonemap/encode/window type, WGC Prewarm), IdleMemoryTrimmer port with a
      Platform trim hook, TieredCompilation=false plus publish-profile tuning. (L)
- [ ] 18-flash-dim-windows: Windows-only instant-dim flash + parked overlay window pool
      behind a platform strategy, ROESNIP_NO_FLASH fallback, measured latency before/after;
      Linux/macOS keep the direct path. (XL)
- [ ] 19-recording-seams: Core encoder/audio abstractions (IVideoEncoder,
      IAudioCaptureDevice, RecordingCapabilities) with Platform.Windows Media Foundation MP4
      and WASAPI implementations; non-Windows reports MP4/audio unsupported. (L)
- [ ] 20-recording-engine: RegionRecorder/RecordingController port: schedule throttle,
      patch-behind delta, pause clock, RawFramesEqual dedupe, LOH-avoidance buffer reuse,
      ROESNIP_RECORD_AUTOSAVE hook, Windows staging-ring readback behind Platform. (XL)
- [ ] 21-recording-chrome: 3-state RecordingChrome UI, RegionOutline click-through,
      PrtScr stop-and-save state machine, recording Share integration (temp file kept on
      failed upload), automation record/preset/fps/chrome commands. (XL)
- [ ] 22-color-picker: Standalone eyedropper (ColorPickerWindow, format catalog, shade
      strip, recent colors, magnifier format-driven value lines, Esc-closes-picker fix). (XL)

## Platform limitations (accepted)

These are not work items. They are behaviors whose honest resolution on the named OS is
"document and degrade gracefully", either because the OS has no equivalent capability or
because a correct implementation needs live hardware this repo cannot exercise.

- Capture exclusion on Linux and macOS: X11/Wayland compositors expose no per-window
  capture-exclusion API and Avalonia does not surface NSWindow.sharingType; overlay windows
  may appear in re-captures there (Windows gets WDA_EXCLUDEFROMCAPTURE via item 02).
- Global hotkey on Wayland: compositors forbid global key grabs; already mitigated by the
  one-time toast, Settings caption, and DE-shortcut docs. No further code planned.
- Instant-dim window parking on Wayland: clients cannot position windows, so the parked
  flash/overlay pool cannot exist there; Linux gets the warmup slice (item 17) only, and
  full-pool tuning needs a live Linux machine (tracked in PLAN-XPLAT.md section 7).
- Elevated run-at-startup: schtasks/UAC is a Windows concept; the control is hidden on
  Linux/macOS rather than emulated (no root-daemon equivalent is appropriate for a tray app).
- MP4 encoding and audio capture off Windows: Media Foundation and WASAPI have no portable
  analog; recording degrades to GIF-only with audio toggles disabled until a PipeWire /
  CoreAudio (or ffmpeg) backend is scoped as its own project.
- Self-update swap for the Linux AppImage and macOS .dmg: a wrong atomic-swap strategy can
  brick an install and cannot be end-to-end tested here; those platforms get a passive
  new-version notice with a link (item 13) until a real update strategy is decided and
  tested live.
- Windows staging-ring GPU readback (item 20) has no Linux/macOS equivalent; non-Windows
  recording pays plain per-frame readback cost.
- PrintScreen/Snipping Tool consent, WM_SETTINGCHANGE broadcast, HKCU Run key: Windows-only
  registry/shell concepts, already correctly gated to Windows.
- Tray activation: Avalonia's TrayIcon exposes Clicked only (no DoubleClick), so a single
  click triggers capture where WPF requires a double click. Accepted API-level divergence.
- ROESNIP_NO_FLASH is meaningful only where the flash architecture exists (Windows after
  item 18); it stays unimplemented elsewhere.
- Wayland portal robustness and mixed-DPI portal slicing remain in PLAN-XPLAT.md section 7;
  both need live Linux hardware and are deliberately not duplicated as items here.

## Notes

- Core's FallbackCaptureBackend has stale-memo self-healing the WPF CaptureService lacks;
  the port is ahead of WPF there. No action, do not "fix" the divergence backwards.
- The two apps keep deliberately distinct identities everywhere: settings dir
  (%APPDATA%\RoeSnip vs %APPDATA%\RoeSnip.App), mutex/pipe names, Run key value names,
  and (after item 13) install dir and release asset names. Any new named OS object must
  follow the split so both residents can run side by side.
