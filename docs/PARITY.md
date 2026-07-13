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
- [ ] 08-toolbar-parity: Popup-safe toolbar hit routing first, then the 10-slot persisted
      palette with right-click Replace, numeric size ComboBox (SizeInput), text style row
      (Bold/Italic + font family on the shape model), and the Redo button. (L)
- [ ] 09-spanning-selection: Multi-monitor spanning selection (SpanningSelectionMath to Core,
      per-window distribution, real-edge handles) plus the spanning HDR .jxr export. (XL)
- [ ] 10-capture-hot-path-perf: WGC capturer pre-provisioning/caching/keepalive/Prewarm in
      Platform.Windows plus the ToneMapper LUT + AVX2 fast path (with reuseOutput) in Core,
      byte-identical to the scalar reference. (L)
- [ ] 11-sharing-core: Move the Sharing subsystem (ProviderSpec, ShareManager, catalog,
      template/url extraction) into RoeSnip.Core, rewrite ShareTestImage without
      System.Drawing, retarget WPF, add the settings fields. (L)
- [ ] 12-sharing-ui: Provider management windows, Settings entry point, and the overlay
      Share split button + per-provider dropdown wired into OverlayController upload
      flows. (M)
- [ ] 13-install-self-update: Single-instance replace-on-run takeover (InstanceSignal.Exit),
      Windows install-to-LOCALAPPDATA + GitHub Releases self-update (own asset name, swap
      discipline, ApplyUpdateLock, idle gate, --self-update-now), version surfaced in
      About/tooltip, passive new-version notice on Linux/macOS. (XL)
- [ ] 14-shell-parity-batch: Hotkey rebind fixes (suspend live hotkey, PrintScreen
      keyup-only capture, real key names), WM_SETTINGCHANGE broadcast after the PrtScr
      consent registry write, competing-screenshot-tool startup warning, ColorPickerEnabled
      toggle, .ico tray/window branding. (M)
- [ ] 15-elevated-startup: Port ElevationManager (schtasks run-at-logon task via one-time
      UAC, error relay file, Settings checkbox + status text, hidden CLI verbs); hidden
      entirely on non-Windows. (L)
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
