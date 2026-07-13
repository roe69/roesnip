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

- [ ] 01-core-settings: Extend Core RoeSnipSettings toward the WPF field set (color picker,
      magnifier, text annotation, palette fields) and add JsonStringEnumConverter to
      SettingsStore before any enum-bearing field lands. (S)
- [ ] 02-capture-exclusion: Platform capture-exclusion seam (WDA_EXCLUDEFROMCAPTURE on
      Windows, documented no-op elsewhere) applied to the Avalonia OverlayWindow, honoring
      ROESNIP_DIAG_NOEXCLUDE. (M)
- [ ] 03-automation-pipe: Port the --auto JSON automation pipe (protocol, server, client)
      for the overlay-only command subset; distinct pipe name so both residents coexist. (L)
- [ ] 04-recording-core-extraction: Move the from-scratch GIF pipeline plus
      MultiMonitorRecording and RecordingSizeEstimator (and their tests) into RoeSnip.Core;
      retarget WPF onto the Core copies with byte-identical output. (L)
- [ ] 05-annotation-history: Port AnnotationHistory (Add/Remove/Replace command model with a
      real redo branch) and wire Undo/Redo shortcuts in the Avalonia overlay. (M)
- [ ] 06-highlight-pixelate-magnifier: Highlight and Pixelate annotation tools (incl.
      PreviewSource wiring and the pixelate loupe UX) plus magnifier parity (wheel-adjustable
      SampleRadius, fixed footprint, click-to-copy). (L)
- [ ] 07-select-edit: The whole Feature B select/edit subsystem: selection with same-tool
      gate, hit-testing, move/resize/endpoint drag, delete, text re-edit, wheel resize,
      selection chrome with the luminance-based handle fill contrast rule. (XL)
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
