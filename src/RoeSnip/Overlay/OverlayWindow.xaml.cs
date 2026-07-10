using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using RoeSnip.App;
using RoeSnip.Capture;
using RoeSnip.Imaging;
using RoeSnip.Interop;

namespace RoeSnip.Overlay;

// RoeSnip.csproj enables both UseWPF and UseWindowsForms, so System.Windows.Forms/System.Drawing
// are in scope alongside System.Windows/System.Windows.Media/.Input/.Controls — alias the
// colliding names to WPF's. (Declared after the namespace line — see AnnotationLayer.cs for why:
// RoeSnip.Color, a sibling WP-A namespace, would otherwise shadow an outer-scope alias for "Color".)
using Point = System.Windows.Point;
using Color = System.Windows.Media.Color;
using TextBox = System.Windows.Controls.TextBox;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using Brushes = System.Windows.Media.Brushes;
using Orientation = System.Windows.Controls.Orientation;
using Size = System.Windows.Size;
using FontFamily = System.Windows.Media.FontFamily;
using Cursors = System.Windows.Input.Cursors;
using Cursor = System.Windows.Input.Cursor;

/// <summary>One per monitor. Shows the frozen tone-mapped preview, dims everything outside the
/// current selection, handles drag-to-select / resize / move, hosts the magnifier and (once a
/// selection exists) the annotation toolbar, and renders the final annotated crop on confirm.
/// All selection/annotation/crop math is physical pixels; DIPs from WPF mouse events are converted
/// via <see cref="_scaleX"/>/<see cref="_scaleY"/> (from CompositionTarget.TransformToDevice) at
/// the point of use, per the mixed-DPI contract in DESIGN.md / PLAN.md §3.2.</summary>
public partial class OverlayWindow : Window
{
    // SelectedMove/SelectedResize/SelectedEndpoint (Feature B, additive) are the Select-tool drags
    // on a picked annotation itself — distinct from NewSelection/Resize/Move (the CROP selection)
    // and from Annotation (drawing a brand-new shape with a tool active). SelectedEndpoint (item 2
    // of the second editing round) is Line/Arrow's analogue of SelectedResize: there's no rect to
    // grow/shrink, so dragging repositions one endpoint (see _dragEndpointIndex) instead.
    private enum DragMode { None, NewSelection, Resize, Move, Annotation, SelectedMove, SelectedResize, SelectedEndpoint }

    // r5-latency overlay pool (D1/D2): these were `readonly`, set once by the (previously only)
    // constructor. A pooled/parked window (see the MonitorInfo-only constructor and Initialize()
    // below) constructs with placeholder content and inert no-op callbacks, then receives its real
    // session content later — so they can no longer be readonly. The full (fallback) constructor
    // still sets them exactly once, up front, exactly as before; only the parked path defers the
    // "real" set to Initialize().
    private CapturedFrame _frame;
    private SdrImage _preview;
    private RoeSnipSettings _settings;
    private Action<OverlayWindow> _onActivatedByMouse;
    private Action<OverlayWindow> _onSelectionStarted;
    private Action<OverlayCommand> _onCommand;
    private Action<PickedColorInfo> _onColorPicked;
    private bool _pickOnlyMode;

    // Mutable working copy of settings for changes made live during the session (custom colors from
    // the toolbar's "+" swatch) — persisted immediately via App.SettingsStore so they survive even
    // though the immutable _settings snapshot handed in at session start does not change in place.
    private RoeSnipSettings _liveSettings;

    /// <summary>True for a window built by the MonitorInfo-only (parked-pool) constructor — see
    /// OverlayWindowPool. False for one built by the normal full constructor (the always-available
    /// fallback path). Read by OnFirstContentRendered (to skip its on-screen move for a pool
    /// window's own first, placeholder-content render — see that method) and by Initialize (to
    /// refuse being called on a non-pooled window).</summary>
    internal bool IsPooled { get; }

    /// <summary>Armed by the full constructor immediately, or by Initialize() once a pooled window
    /// has received real session content — see IsTextInputFocused's sibling doc comments for the
    /// general pattern. Belt-and-braces guard (D2) at the top of every Preview* input handler: a
    /// parked pool window is never on-screen/activated and so should never receive real input, but
    /// this makes that invariant explicit rather than assumed.</summary>
    private bool _initialized;

    /// <summary>The throwaway placeholder CapturedFrame a parked (IsPooled) window builds for
    /// itself so every field this class touches during construction/first-render stays non-null —
    /// nothing else references it, so this window itself owns disposing it, either when Initialize
    /// swaps in the session's real frame or, if this window is closed while still parked (pool
    /// teardown/rebuild), from OnClosed. Null for a non-pooled window and for a pooled window that
    /// has already been Initialize()'d.</summary>
    private CapturedFrame? _ownedPlaceholderFrame;

    private double _scaleX = 1.0, _scaleY = 1.0;
    private RectPhysical? _selectionPx;

    /// <summary>A mouse-down that would start a new selection is treated as a *click* (color pick)
    /// until the cursor has travelled at least this many physical pixels — only then does it become
    /// a selection drag.</summary>
    private const double ClickDragThresholdPx = 4.0;

    private DragMode _dragMode = DragMode.None;
    private SelectionHandle _activeHandle = SelectionHandle.None;
    private Point _dragAnchorPx;
    private Point _dragAnchorDip;
    private bool _newSelectionPending;
    private RectPhysical _dragStartRect;

    // Feature B: the selected Pixelate's own rect at the start of a SelectedResize drag — the
    // ApplyResize/ClampToFrame math below is reused verbatim from the crop-selection resize, just
    // fed this rect instead of _dragStartRect.
    private RectPhysical _dragStartShapeRect;

    // Item 2 (second editing round): which endpoint (0 = P0, 1 = P1) a DragMode.SelectedEndpoint
    // drag is repositioning — set once at mouse-down from AnnotationLayer.HitTestSelectedEndpoint,
    // read on every subsequent mouse-move until mouse-up ends the gesture.
    private int _dragEndpointIndex;

    // Feature B item 4: the ORIGINAL shape being re-edited via double-click, so
    // CommitActiveTextEditor knows to Replace it instead of adding a new shape, and
    // CancelActiveTextEditor knows to leave it untouched. Null for a brand-new text placement.
    private AnnotationShape? _textEditReplacing;

    private AnnotationTool _currentTool = AnnotationTool.None;
    private Color _currentColor = Colors.Red;
    private double _currentStrokeWidth = 4.0;

    // Last-used text-annotation style (item 4 / DESIGN addendum), seeded from settings and updated
    // live from the toolbar's text-style group; applies to new text annotations and to an
    // in-progress edit.
    private string _textFontFamily;
    private double _textFontSize;
    private bool _textBold;
    private bool _textItalic;

    // Transient scroll-wheel size indicator (item 3): a single reusable element, re-triggered on
    // each wheel tick rather than stacking overlapping fade animations.
    private System.Windows.Controls.Border? _sizeIndicator;
    private DispatcherTimer? _sizeIndicatorTimer;

    private ToolbarControl? _toolbar;
    private TextBox? _activeTextEditor;
    private Point _activeTextEditorOriginPx;

    // Tool-indicating cursor (item 1, UX round 3) — one small custom cursor bitmap per
    // (tool, color, strokeWidth) combination, cached and disposed for this window's lifetime.
    private readonly ToolCursorCache _toolCursorCache = new();

    internal MonitorInfo Monitor => _frame.Monitor;
    internal CapturedFrame Frame => _frame;
    internal RectPhysical? SelectionPx => _selectionPx;

    /// <summary>True while this window has anything worth cancelling short of closing the whole
    /// overlay — used by OverlayController's two-stage Esc (stage: clear snip, stay open).</summary>
    internal bool HasSnipInProgress =>
        _selectionPx is not null || Annotations.HasAnnotations || _dragMode != DragMode.None
        || Annotations.SelectedShape is not null;

    /// <summary>True while a placed Pixelate/Text annotation is selected via the Select tool
    /// (Feature B) — read by SessionKeyboardHook so it only treats Delete/Back as a session key
    /// (swallowed globally, dispatched to ProcessKeyCommand) when there's actually something for
    /// them to delete; otherwise they must pass through untouched to whatever app really has focus,
    /// exactly like every other non-session key.</summary>
    internal bool HasSelectedAnnotation => Annotations.SelectedShape is not null;

    /// <summary>True while an inline text annotation TextBox is being edited. Read by the
    /// session-scoped low-level keyboard hook (SessionKeyboardHook) to decide whether to swallow
    /// session keys globally (normal case) or let everything except Esc reach the real focused
    /// control through the normal WPF pipeline (text-editing case) — see OverlayInputInterop.cs.</summary>
    internal bool IsTextEditingActive => _activeTextEditor is not null;

    /// <summary>Volatile mirror of "the in-progress text editor holds WPF keyboard focus",
    /// maintained by the editor's Got/LostKeyboardFocus events. Read by the session hook from its
    /// own thread: only when this is true can the normal focus-dependent WPF pipeline be trusted
    /// to deliver typing; otherwise the hook forwards keys via TextEditKeyForwarder.</summary>
    internal bool TextEditorHasKeyboardFocus => _textEditorHasKeyboardFocus;

    private volatile bool _textEditorHasKeyboardFocus;

    /// <summary>The in-progress text annotation editor, if any — read by TextEditKeyForwarder (item
    /// 3b's guaranteed fallback tier) to apply keystrokes directly when the window doesn't hold real
    /// OS focus. Null whenever <see cref="IsTextEditingActive"/> is false.</summary>
    internal TextBox? ActiveTextEditor => _activeTextEditor;

    /// <summary>Volatile "some text input inside this window holds WPF keyboard focus" flag (UX
    /// round 5, item 2) — the generalization of <see cref="TextEditorHasKeyboardFocus"/> to ANY
    /// TextBoxBase in the window, today the toolbar size ComboBox's editable TextBox (and the
    /// annotation editor, which the more specific IsTextEditingActive machinery checks first).
    /// Maintained by window-level Got/LostKeyboardFocus class handlers; read by SessionKeyboardHook
    /// from its own thread to pass ALL keys through (Enter included — the ComboBox commits it
    /// locally) instead of swallowing them as session commands while the user is typing a size.</summary>
    internal bool IsTextInputFocused => _textInputFocused;

    private volatile bool _textInputFocused;

    /// <summary>True while the magnifier loupe (item 4, UX round 3; lifecycle fixed in UX round 4)
    /// should track the cursor: pick-only mode always shows it (it's the point of that mode);
    /// otherwise it's visible only while this window owns no selection AND no drag of any kind is
    /// in progress. The moment a drag starts — including a brand-new selection drag, still inside
    /// the click-threshold-pending phase — this goes false and the magnifier is hidden immediately
    /// (see the explicit MagnifierControl.Hide() calls at each drag-start site in
    /// OnPreviewMouseLeftButtonDown/BeginTextEditor), rather than staying visible until the drag
    /// finishes. Independently of this, MouseLeave/Deactivated always hide the magnifier outright
    /// (even in pick-only mode) so it can never freeze on a monitor the cursor has left — see
    /// OnMouseLeave/OnDeactivated.</summary>
    private bool IsMagnifierActive => _pickOnlyMode || (_selectionPx is null && _dragMode == DragMode.None);

    internal OverlayWindow(
        CapturedFrame frame,
        SdrImage preview,
        RoeSnipSettings settings,
        Action<OverlayWindow> onActivatedByMouse,
        Action<OverlayWindow> onSelectionStarted,
        Action<OverlayCommand> onCommand,
        Action<PickedColorInfo> onColorPicked,
        bool pickOnlyMode)
    {
        InitializeComponent();
        AssignSessionFields(
            frame, preview, settings, onActivatedByMouse, onSelectionStarted, onCommand, onColorPicked, pickOnlyMode);
        WireInputHandlers();
        _initialized = true;
    }

    /// <summary>Parked-pool constructor (r5-latency, D1/D2 — see OverlayWindowPool): knows only its
    /// target MONITOR (bounds/DPI — everything OnSourceInitialized needs to park itself off-screen
    /// and compute _scaleX/_scaleY) plus a throwaway 1x1 placeholder frame/preview, so every field
    /// this class touches during construction/first-render stays non-null. The caller
    /// (OverlayWindowPool.EnsureBuilt) still Show()s this window and flushes its first render itself
    /// — exactly like the full constructor's caller does — so the pooled window's first paint (the
    /// expensive part this whole design exists to move off the hot path) happens at pool-build time,
    /// against the placeholder. Input/keyboard handlers stay inert (see _initialized) until a real
    /// session claims this window via Initialize().</summary>
    internal OverlayWindow(MonitorInfo monitor)
    {
        InitializeComponent();
        IsPooled = true;

        var placeholderFrame = new CapturedFrame(FrameFormat.Bgra8Srgb, 1, 1, 4, new byte[4], monitor);
        _ownedPlaceholderFrame = placeholderFrame;
        var placeholderPreview = new SdrImage(1, 1, new byte[4]);

        AssignSessionFields(
            placeholderFrame, placeholderPreview, RoeSnipSettings.Default,
            static _ => { }, static _ => { }, static _ => { }, static _ => { }, pickOnlyMode: false);
        WireInputHandlers();
        // _initialized deliberately stays false — armed by Initialize() once a real session claims
        // this window (see that method).
    }

    /// <summary>Common field-assignment shared by both constructors — see IsPooled's doc comment for
    /// why the fields it sets can no longer be readonly. Also reused by Initialize() (the parked-pool
    /// hot path), so this is the SINGLE place that maps (frame, preview, settings, callbacks,
    /// pickOnlyMode) onto this window's fields — the full constructor, the parked constructor, and
    /// Initialize can never disagree about what "assigning session content" means.
    /// [MemberNotNull] tells the nullable analyzer these fields are non-null after this call
    /// returns — without it, every non-readonly field set only through this indirection (rather
    /// than directly in the constructor body) would warn CS8618 at both constructors' closing
    /// brace, since flow analysis doesn't follow through an ordinary method call.</summary>
    [System.Diagnostics.CodeAnalysis.MemberNotNull(
        nameof(_frame), nameof(_preview), nameof(_settings), nameof(_liveSettings),
        nameof(_onActivatedByMouse), nameof(_onSelectionStarted), nameof(_onCommand), nameof(_onColorPicked),
        nameof(_textFontFamily))]
    private void AssignSessionFields(
        CapturedFrame frame,
        SdrImage preview,
        RoeSnipSettings settings,
        Action<OverlayWindow> onActivatedByMouse,
        Action<OverlayWindow> onSelectionStarted,
        Action<OverlayCommand> onCommand,
        Action<PickedColorInfo> onColorPicked,
        bool pickOnlyMode)
    {
        _frame = frame;
        _preview = preview;
        _settings = settings;
        _liveSettings = settings;
        _onActivatedByMouse = onActivatedByMouse;
        _onSelectionStarted = onSelectionStarted;
        _onCommand = onCommand;
        _onColorPicked = onColorPicked;
        _pickOnlyMode = pickOnlyMode;

        _textFontFamily = settings.TextFontFamily;
        _textFontSize = settings.TextFontSize;
        _textBold = settings.TextBold;
        _textItalic = settings.TextItalic;

        // Last-used loupe zoom (wheel while the magnifier is up — see OnPreviewMouseWheel); the
        // property setter clamps, so a hand-edited settings.json value can't break the render.
        MagnifierControl.SampleRadius = settings.MagnifierSampleRadius;
        // The loupe's value lines follow the same ColorFormatShow* toggles as the picker window.
        MagnifierControl.Formats = settings;
    }

    /// <summary>Event wiring shared by both constructors — extracted verbatim from the (formerly
    /// single) constructor so the parked-pool path wires the exact same handlers the full
    /// constructor always has, with no behavioral difference between the two beyond IsPooled/
    /// _initialized.</summary>
    private void WireInputHandlers()
    {
        PreviewMouseLeftButtonDown += OnPreviewMouseLeftButtonDown;
        PreviewMouseRightButtonDown += OnPreviewMouseRightButtonDown;
        PreviewMouseMove += OnPreviewMouseMove;
        PreviewMouseLeftButtonUp += OnPreviewMouseLeftButtonUp;
        PreviewMouseWheel += OnPreviewMouseWheel;
        PreviewKeyDown += OnPreviewKeyDown;
        // Pressing/releasing Shift or Ctrl must refresh the cursor immediately (the modifier-grab
        // Hand appears over outline interiors while held) — a mouse-move alone would leave the
        // cursor stale until the pointer jitters.
        PreviewKeyUp += (_, ke) =>
        {
            if (!_initialized) return;
            if (ke.Key is Key.LeftShift or Key.RightShift or Key.LeftCtrl or Key.RightCtrl)
            {
                UpdateToolCursor();
            }
        };
        MouseEnter += OnMouseEnter;
        MouseLeave += OnMouseLeave;
        Deactivated += OnDeactivated;

        // Track whether ANY text input in this window holds keyboard focus (see IsTextInputFocused)
        // — handledEventsToo because TextBoxBase marks these focus events handled internally.
        AddHandler(Keyboard.GotKeyboardFocusEvent,
            new KeyboardFocusChangedEventHandler(OnAnyKeyboardFocusChanged), handledEventsToo: true);
        AddHandler(Keyboard.LostKeyboardFocusEvent,
            new KeyboardFocusChangedEventHandler(OnAnyKeyboardFocusChanged), handledEventsToo: true);
        SizeChanged += (_, _) =>
        {
            SyncChromeSizes();
            UpdateDimGeometry();
        };
    }

    /// <summary>Hands a parked pool window (see IsPooled) its real per-session content — the D1/D2
    /// hot-path replacement for the full constructor, called by OverlayController.OverlaySession
    /// instead of `new OverlayWindow(...)` whenever OverlayWindowPool has a matching parked window
    /// already built, Show()n and first-rendered for this exact monitor. Deliberately does NOT
    /// touch anything OnSourceInitialized already computed off the (unchanging, since the pool is
    /// keyed by exact monitor) monitor — _scaleX/_scaleY, the Annotations/Adorner/MagnifierControl
    /// DeviceScale*, or ActualWidth/Height-derived chrome sizing all stay exactly as they were set
    /// during pool-build. Only the content that differs per-session is refreshed here: the preview
    /// bitmap (PreviewImage/Background/Annotations.PreviewSource), the dim geometry (a fresh window
    /// starts with no selection), and the callback/settings/text-style fields AssignSessionFields
    /// sets. No selection/annotation/toolbar state exists to reset — this window has never been
    /// shown to a user or received real input (see _initialized), so there is nothing to clear
    /// (D1's "single-use, no state-reset path" design).</summary>
    internal void Initialize(
        CapturedFrame frame,
        SdrImage preview,
        RoeSnipSettings settings,
        Action<OverlayWindow> onActivatedByMouse,
        Action<OverlayWindow> onSelectionStarted,
        Action<OverlayCommand> onCommand,
        Action<PickedColorInfo> onColorPicked,
        bool pickOnlyMode)
    {
        if (!IsPooled)
        {
            throw new InvalidOperationException("Initialize is only valid for a pooled (parked) overlay window.");
        }

        _ownedPlaceholderFrame?.Dispose();
        _ownedPlaceholderFrame = null;

        AssignSessionFields(
            frame, preview, settings, onActivatedByMouse, onSelectionStarted, onCommand, onColorPicked, pickOnlyMode);

        var previewBitmap = preview.ToBitmapSource();
        PreviewImage.Source = previewBitmap;
        Annotations.PreviewSource = previewBitmap;
        Background = new ImageBrush(previewBitmap) { Stretch = Stretch.Fill };
        UpdateDimGeometry();

        // Session-claimed pool windows should be behaviorally indistinguishable from a freshly
        // constructed one — clear the WS_EX_TOOLWINDOW style OverlayWindowPool applies to keep idle
        // parked windows out of Alt+Tab (see OnSourceInitialized), since a full-constructor window
        // never has it in the first place.
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd != IntPtr.Zero)
        {
            long exStyle = NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE).ToInt64();
            NativeMethods.SetWindowLongPtr(
                hwnd, NativeMethods.GWL_EXSTYLE, new IntPtr(exStyle & ~NativeMethods.WS_EX_TOOLWINDOW));
        }

        _initialized = true;
    }

    /// <summary>Moves an Initialize()'d pooled window onto its real monitor bounds — the pooled
    /// path's own equivalent of OnFirstContentRendered's on-screen move (see that method), called
    /// directly by OverlaySession once it has flushed Initialize's new content through a render
    /// pass, since ContentRendered itself already fired once for this window back at pool-build
    /// time (against the placeholder) and will never fire again.</summary>
    internal void MoveOnScreen() => MoveOnScreenCore();

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        var bounds = _frame.Monitor.BoundsPx;
        var hwnd = new WindowInteropHelper(this).Handle;

        var source = PresentationSource.FromVisual(this);
        if (source?.CompositionTarget is { } target)
        {
            _scaleX = target.TransformToDevice.M11;
            _scaleY = target.TransformToDevice.M22;
        }

        Annotations.DeviceScaleX = _scaleX;
        Annotations.DeviceScaleY = _scaleY;
        Adorner.DeviceScaleX = _scaleX;
        Adorner.DeviceScaleY = _scaleY;
        MagnifierControl.DeviceScaleX = _scaleX;
        MagnifierControl.DeviceScaleY = _scaleY;

        var previewBitmap = _preview.ToBitmapSource();
        PreviewImage.Source = previewBitmap;
        RenderOptions.SetBitmapScalingMode(PreviewImage, BitmapScalingMode.NearestNeighbor);
        Annotations.PreviewSource = previewBitmap; // Pixelate tool mosaics these pixels

        // Paint the frozen preview into the window Background too, so even a background-only first
        // compositor frame already shows the correct pixels (not the XAML black) — kills the black
        // first-frame blink the luma sampler caught on cold captures.
        Background = new ImageBrush(previewBitmap) { Stretch = Stretch.Fill };

        UpdateDimGeometry();

        // D6 (overlay latency pool): exclude every overlay window from screen capture — belt-and-
        // braces for parked pool windows (already invisible to a per-monitor-bounds capture purely
        // by being off the virtual desktop — see OffScreenX below), and applied to every overlay
        // window rather than just parked ones because no code path in this app ever captures the
        // screen while ANY overlay window (pooled or on-demand) is on it: the main capture flow
        // (Program.cs's RunCaptureFlowAsync), pick-mode's re-capture (OverlayController.
        // RunPickModeCaptureAsync), and the startup warmup capture (TrayApp.WarmupCaptureSessions)
        // all run CaptureService.CaptureAll to completion strictly BEFORE any overlay window is ever
        // constructed or shown — there is no "overlay up, then capture again" path anywhere in this
        // codebase. Given that invariant, excluding every overlay window permanently is simpler and
        // safer than toggling the affinity on/off per window kind (there's no case where clearing it
        // would ever matter). Same API and same ROESNIP_DIAG_NOEXCLUDE=1 escape hatch as
        // FlashDimmer's own parked windows (see that class's PrepareHidden) so the existing external
        // luma sampler harness keeps working unmodified. Failure is logged but non-fatal.
        if (Environment.GetEnvironmentVariable("ROESNIP_DIAG_NOEXCLUDE") != "1"
            && !NativeMethods.SetWindowDisplayAffinity(hwnd, NativeMethods.WDA_EXCLUDEFROMCAPTURE))
        {
            Console.Error.WriteLine(
                "RoeSnip: SetWindowDisplayAffinity(EXCLUDEFROMCAPTURE) failed on an overlay window (non-fatal).");
        }

        if (IsPooled)
        {
            // Parked pool windows sit around for the app's whole idle lifetime (D4) exactly like
            // FlashDimmer's flash windows — WS_EX_TOOLWINDOW keeps them out of Alt+Tab the same way
            // (ShowInTaskbar=False alone only hides the taskbar entry, not Alt+Tab). Cleared again in
            // Initialize() once a session actually claims this window, so a live overlay behaves
            // exactly like one built by the full constructor (which never sets this style).
            long exStyle = NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE).ToInt64();
            NativeMethods.SetWindowLongPtr(
                hwnd, NativeMethods.GWL_EXSTYLE, new IntPtr(exStyle | NativeMethods.WS_EX_TOOLWINDOW));
        }

        // Cold-start black-frame elimination (measured with a luma sampler): on the first capture
        // after launch, WPF clears the window's surface to black and DWM shows that for one frame
        // before the render thread produces the first composited frame — a visible black blink,
        // independent of Background or content-set ordering (both were tried and measured to not
        // help). Fix: show the window at its correct SIZE but positioned fully OFF the virtual
        // desktop, so that unavoidable cold black frame happens where nobody can see it, then move
        // it onto the target monitor in ContentRendered once it has genuinely painted. On warm
        // captures ContentRendered fires almost immediately, so the on-screen appearance is instant
        // and already-dimmed. For a pooled window this first ContentRendered fires against the
        // PLACEHOLDER content at pool-build time — OnFirstContentRendered below deliberately skips
        // its on-screen move in that case; the real move happens later, explicitly, via
        // MoveOnScreen() once a session has Initialize()'d this window with real content.
        ContentRendered += OnFirstContentRendered;
        NativeMethods.SetWindowPos(
            hwnd, NativeMethods.HWND_TOPMOST,
            OffScreenX, bounds.Top, bounds.Width, bounds.Height,
            NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_SHOWWINDOW);
    }

    // Far off the virtual desktop (monitors span roughly x:[-1440, 2560]); the window renders here
    // invisibly, then OnFirstContentRendered snaps it to its real monitor bounds already painted.
    private const int OffScreenX = 60000;

    private void OnFirstContentRendered(object? sender, EventArgs e)
    {
        ContentRendered -= OnFirstContentRendered;
        if (IsPooled && !_initialized)
        {
            // This is a parked pool window's OWN first render (see OverlayWindowPool.EnsureBuilt) —
            // its content right now is the throwaway placeholder, not anything a user should ever
            // see. Stay parked; the session that eventually claims this window (Initialize) moves it
            // on-screen explicitly via MoveOnScreen once it has real content, since this event —
            // having already fired once and unsubscribed itself above — will never fire again.
            return;
        }
        MoveOnScreenCore();
    }

    /// <summary>The actual on-screen SetWindowPos, shared by the fallback path's own first-render
    /// handoff (OnFirstContentRendered) and the pooled path's explicit MoveOnScreen — see both call
    /// sites' doc comments for why each is on-screen "for real" at the moment it calls this.</summary>
    private void MoveOnScreenCore()
    {
        var bounds = _frame.Monitor.BoundsPx;
        var hwnd = new WindowInteropHelper(this).Handle;
        NativeMethods.SetWindowPos(
            hwnd, NativeMethods.HWND_TOPMOST,
            bounds.Left, bounds.Top, bounds.Width, bounds.Height,
            NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_SHOWWINDOW);
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        _toolCursorCache.Dispose();
        // Only ever non-null for a pooled window that was closed (pool teardown/rebuild — see
        // OverlayWindowPool) before a session ever claimed it via Initialize(); a claimed window
        // already disposed and nulled this in Initialize itself. CapturedFrame.Dispose() is a plain
        // idempotent field-null, so there is no double-dispose hazard either way.
        _ownedPlaceholderFrame?.Dispose();
        _ownedPlaceholderFrame = null;

        // Deterministic release of the session's big pixel buffers (idle-memory audit, 2026-07):
        // per monitor this window references the ~14.75 MB SdrImage preview AND the ~14.75 MB
        // frozen BitmapSource copy WPF made of it (BitmapSource.Create copies, it does not wrap —
        // see SdrImage.ToBitmapSource), and the magnifier may still reference both the preview and
        // the ~28 MB FP16 frame. A closed WPF window can stay reachable well past Close() (pending
        // dispatcher work, input state), so drop every heavy reference here instead of riding the
        // window graph's own collection. The frame's pixel buffer itself is disposed by the flow
        // that owns it (Program.cs / pick-mode finally) — nulling _frame here just decouples this
        // window from that object graph. Nothing reads Frame/Monitor after OnClosed (verified: the
        // controller reads them strictly before CloseOverlay; the pool only touches parked, never
        // closed, windows), so the null!-suppressions cannot surface as NREs.
        MagnifierControl.Hide();
        PreviewImage.Source = null;
        Annotations.PreviewSource = null;
        Background = null;
        _frame = null!;
        _preview = null!;
        _toolbar = null;
        _activeTextEditor = null;
        _sizeIndicator = null;
        // A still-running indicator timer is rooted by the Dispatcher and its Tick closure roots
        // this whole window (with everything above) for up to its 600 ms interval — stop it now.
        _sizeIndicatorTimer?.Stop();
        _sizeIndicatorTimer = null;
    }

    private void SyncChromeSizes()
    {
        double w = ActualWidth, h = ActualHeight;
        if (w <= 0 || h <= 0)
        {
            return;
        }
        Annotations.Width = w;
        Annotations.Height = h;
        Adorner.Width = w;
        Adorner.Height = h;
        MagnifierControl.Width = w;
        MagnifierControl.Height = h;
    }

    // ---------- Mouse interaction ----------

    private Point ToPhysical(Point dip) => new(dip.X * _scaleX, dip.Y * _scaleY);

    private void OnMouseEnter(object sender, MouseEventArgs e)
    {
        if (!_initialized) return; // D2 belt-and-braces — see _initialized's doc comment
        _onActivatedByMouse(this);
    }

    /// <summary>Lifecycle fix (UX round 4, item 1b): without this, a window that last rendered the
    /// magnifier at some position never gets another PreviewMouseMove once the cursor crosses onto a
    /// different monitor's window — so its magnifier visual just sits there frozen ("stuck") at the
    /// last sampled point instead of disappearing. Unconditional (ignores IsMagnifierActive/
    /// _pickOnlyMode): the cursor leaving this window is reason enough to hide regardless of mode.</summary>
    private void OnMouseLeave(object sender, MouseEventArgs e) => MagnifierControl.Hide();

    /// <summary>Belt-and-suspenders alongside OnMouseLeave: if this window loses activation (e.g.
    /// another monitor's window steals it, or the OS takes focus away entirely) while some other
    /// codepath managed to dodge MouseLeave, the magnifier still gets torn down rather than risking a
    /// stuck frame on a monitor the cursor may no longer even be over.</summary>
    private void OnDeactivated(object? sender, EventArgs e) => MagnifierControl.Hide();

    private void OnPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (!_initialized) return; // D2 belt-and-braces — see _initialized's doc comment
        if (IsWithinToolbar(e.OriginalSource as DependencyObject))
        {
            // Clicking any toolbar control OTHER than the size box while the size box holds
            // keyboard focus hands focus back to the window (every other toolbar control is
            // Focusable=false and would never take it) — otherwise the box would keep focus
            // forever and Enter would keep committing the size instead of confirming the snip.
            if (_textInputFocused && _activeTextEditor is null
                && _toolbar?.IsWithinSizeInput(e.OriginalSource as DependencyObject) != true)
            {
                Keyboard.Focus(this);
            }
            return; // let the toolbar's own controls handle their own click
        }

        // Same hand-back for clicks on the overlay itself (starting a drag/annotation while the
        // size box was still focused).
        if (_textInputFocused && _activeTextEditor is null)
        {
            Keyboard.Focus(this);
        }

        var dip = e.GetPosition(this);
        var px = ToPhysical(dip);

        if (_pickOnlyMode)
        {
            // Pick-only mode (ColorPickerWindow's "Pick" button): no selection/toolbar/annotation
            // concept at all — every click immediately picks the color under the cursor and closes
            // this ad-hoc overlay session. No drag-threshold gating (unlike normal mode's plain
            // click), since there's nothing else a click could mean here.
            _onActivatedByMouse(this);
            TriggerColorPick(px, dip);
            e.Handled = true;
            return;
        }

        if (_activeTextEditor is not null && !ReferenceEquals(e.OriginalSource, _activeTextEditor))
        {
            CommitActiveTextEditor();
        }

        _onActivatedByMouse(this);

        // Feature B item 4 (extended twice since: to the Text tool, then to EVERY tool once placed
        // shapes became always-clickable): double-clicking a placed TEXT annotation reopens its
        // inline editor, prefilled — and this must win over the confirm-snip double-click check
        // right below, or a double-click meant to re-edit text would just confirm the whole snip
        // instead. A SINGLE click on the same text is handled further down (selects + starts a
        // move drag); this ClickCount>=2 branch only ever fires on the second click of a
        // double-click, so it doesn't fight that gesture.
        if (e.ClickCount >= 2
            && Annotations.HitTestEditable(px) is { Tool: AnnotationTool.Text } textHit)
        {
            Annotations.Select(textHit);
            BeginTextReEdit(textHit, dip);
            e.Handled = true;
            return;
        }

        if (e.ClickCount >= 2 && _selectionPx is { } existing && RectContains(existing, px))
        {
            _onCommand(OverlayCommand.ConfirmPlain);
            e.Handled = true;
            return;
        }

        // Modifier-grab: Shift/Ctrl + click is an explicit "grab whatever drawn object is under
        // the cursor" that beats every tool behavior. Crucially it hit-tests GENEROUSLY
        // (interiorGrab): the middle of a rectangle/ellipse counts, not just the stroke ring the
        // plain click rules use — so a box drawn around content can be repositioned from anywhere
        // inside it without hunting for its outline, while plain clicks keep falling through to
        // draw/select/color-pick as before. The grab is always a move; handles/endpoints still
        // resize via plain clicks once the shape is selected.
        if (IsGrabModifierHeld
            && Annotations.HitTestEditable(px, interiorGrab: true) is { } grabbed)
        {
            Annotations.Select(grabbed);
            _dragMode = DragMode.SelectedMove;
            _dragAnchorPx = px;
            Annotations.BeginDragSelected();
            CaptureMouse();
            MagnifierControl.Hide();
            UpdateToolCursor();
            e.Handled = true;
            return;
        }

        // With ANY drawing tool active, a click that lands on an ALREADY-PLACED editable shape (the
        // selected one's resize handles or endpoints, or any shape's body — for the outline/segment
        // kinds "body" means near the stroke, see AnnotationLayer.HitsShapeBody) edits that shape
        // instead of starting a new one on top of it. Originally this was gated to the SAME kind as
        // the active tool, but that read as "my object became uneditable" the moment another tool
        // was chosen (user-reported) — a placed object must always be grabbable by clicking it,
        // regardless of the active tool; tool-scoping only governs which STALE selection survives a
        // tool switch (the ToolSelected deselect), never whether a deliberate click can pick a
        // shape up. A press anywhere else still draws as before, so overlapping shapes stay
        // reachable (outline interiors / off-segment space don't hit-test at all; filled kinds by
        // starting the drag outside them).
        bool editsExistingShape =
            Annotations.HitTestSelectedHandle(px) != SelectionHandle.None
            || Annotations.HitTestSelectedEndpoint(px) >= 0
            || Annotations.HitTestEditable(px) is not null;

        if (_currentTool != AnnotationTool.None && !editsExistingShape)
        {
            // Starting a drawing/text gesture is one of the "any other gesture" cases (Feature B
            // item 6) that deselects a leftover annotation selection.
            Annotations.Deselect();
            if (_currentTool == AnnotationTool.Text)
            {
                BeginTextEditor(px, dip);
            }
            else
            {
                _dragMode = DragMode.Annotation;
                Annotations.BeginShape(_currentTool, px, _currentColor, _currentStrokeWidth);
                CaptureMouse();
                MagnifierControl.Hide(); // item 1a: hide the instant this drag starts, not once it ends
            }
            e.Handled = true;
            return;
        }

        // Select tool (Feature B item 7's click priority): a selected annotation's own resize
        // handles/endpoints beat the crop selection's resize handles, which beat clicking an
        // (unselected or already-selected) annotation's body, which beats the crop selection's move
        // body, which beats starting a brand-new crop-selection drag.
        var selectedHandle = Annotations.SelectedShape is not null
            ? Annotations.HitTestSelectedHandle(px)
            : SelectionHandle.None;
        // Item 2 (second editing round): Line/Arrow's endpoint handles are this predicate's
        // Line/Arrow analogue — mutually exclusive with selectedHandle in practice (a selected
        // shape is one Tool, and only rect-resizable kinds ever produce a non-None selectedHandle),
        // but checked as its own branch, ABOVE the plain body-move hit test below, so grabbing an
        // endpoint always resizes rather than moving the whole segment.
        int selectedEndpoint = Annotations.SelectedShape is not null
            ? Annotations.HitTestSelectedEndpoint(px)
            : -1;

        if (selectedHandle != SelectionHandle.None)
        {
            var selectedRectPx = Annotations.SelectedShape!.PointsPx;
            _dragStartShapeRect = new RectPhysical(
                (int)selectedRectPx[0].X, (int)selectedRectPx[0].Y,
                (int)selectedRectPx[1].X, (int)selectedRectPx[1].Y).Normalized();
            _dragMode = DragMode.SelectedResize;
            _activeHandle = selectedHandle;
            _dragAnchorPx = px;
            Annotations.BeginDragSelected();
        }
        else if (selectedEndpoint >= 0)
        {
            _dragMode = DragMode.SelectedEndpoint;
            _dragEndpointIndex = selectedEndpoint;
            _dragAnchorPx = px;
            Annotations.BeginDragSelected();
        }
        else
        {
            var handle = Adorner.HitTestHandle(px);
            if (handle != SelectionHandle.None && handle != SelectionHandle.Body)
            {
                Annotations.Deselect();
                _dragMode = DragMode.Resize;
                _activeHandle = handle;
                _dragAnchorPx = px;
                _dragStartRect = _selectionPx!.Value;
            }
            else
            {
                var hitShape = Annotations.HitTestEditable(px);
                if (hitShape is not null)
                {
                    // Click-selects AND immediately starts a drag in the same gesture — the common
                    // "click and drag a shape" UX, rather than requiring a separate click to select
                    // before a drag can act on it. After selecting, re-run the handle/endpoint hit
                    // tests (they only answer for the CURRENT selection, which just changed): a
                    // first click landing exactly on what is now a corner handle or a segment
                    // endpoint starts that resize directly instead of degrading to a whole-shape
                    // move — without this, resizing a deselected shape always cost a throwaway
                    // select-click first.
                    Annotations.Select(hitShape);
                    var freshHandle = Annotations.HitTestSelectedHandle(px);
                    int freshEndpoint = freshHandle == SelectionHandle.None
                        ? Annotations.HitTestSelectedEndpoint(px)
                        : -1;
                    if (freshHandle != SelectionHandle.None)
                    {
                        var freshRectPx = hitShape.PointsPx;
                        _dragStartShapeRect = new RectPhysical(
                            (int)freshRectPx[0].X, (int)freshRectPx[0].Y,
                            (int)freshRectPx[1].X, (int)freshRectPx[1].Y).Normalized();
                        _dragMode = DragMode.SelectedResize;
                        _activeHandle = freshHandle;
                    }
                    else if (freshEndpoint >= 0)
                    {
                        _dragMode = DragMode.SelectedEndpoint;
                        _dragEndpointIndex = freshEndpoint;
                    }
                    else
                    {
                        _dragMode = DragMode.SelectedMove;
                    }
                    _dragAnchorPx = px;
                    Annotations.BeginDragSelected();
                }
                else if (handle == SelectionHandle.Body)
                {
                    Annotations.Deselect();
                    _dragMode = DragMode.Move;
                    _activeHandle = handle;
                    _dragAnchorPx = px;
                    _dragStartRect = _selectionPx!.Value;
                }
                else
                {
                    Annotations.Deselect();
                    // Don't start (or clear) any selection yet: below ClickDragThresholdPx of
                    // travel this is a color-inspection click, not a drag. The selection work
                    // happens in OnPreviewMouseMove once the threshold is crossed.
                    _dragMode = DragMode.NewSelection;
                    _newSelectionPending = true;
                    _dragAnchorPx = px;
                    _dragAnchorDip = dip;
                }
            }
        }

        CaptureMouse();
        // Item 1a: hide immediately on drag-start state entry — covers Move/Resize (which always
        // already own a selection, so IsMagnifierActive was already false) and, crucially,
        // NewSelection: previously the magnifier stayed visible for the entire drag and only hid
        // once the mouse was released. It still doesn't know yet whether this will resolve into a
        // real drag or a plain click-to-pick (see _newSelectionPending), but either way the preview
        // should disappear the moment the mouse goes down to start it.
        MagnifierControl.Hide();
        UpdateToolCursor();
        e.Handled = true;
    }

    /// <summary>Right-click exits (item 5, UX round 3): outside the current selection (or when
    /// there is no selection anywhere) it cancels the whole overlay session, exactly like Esc's
    /// final stage — never the staged CancelStage semantics, since a right-click is an explicit
    /// "get me out of here" gesture rather than "undo one step". Right-click *inside* the current
    /// selection is reserved (a no-op) rather than exiting. Suppressed while a text edit is active,
    /// where it instead cancels just the edit (mirroring Esc's innermost stage) — and it never opens
    /// the color-picker window, which is exclusively a left-click concern (TriggerColorPick), so
    /// there's nothing here that could conflict with it.</summary>
    private void OnPreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (!_initialized) return; // D2 belt-and-braces — see _initialized's doc comment
        if (IsWithinToolbar(e.OriginalSource as DependencyObject))
        {
            return; // right-clicking a toolbar button is not an "exit the overlay" gesture
        }
        e.Handled = true;

        if (_activeTextEditor is not null)
        {
            CancelActiveTextEditor();
            return;
        }

        if (_pickOnlyMode)
        {
            // Deferred like TriggerColorPick's own Cancel dispatch — Cancel synchronously closes
            // every window in the session (including this one, from inside its own mouse-event
            // handler); BeginInvoke avoids touching a window that's already mid-teardown.
            Dispatcher.BeginInvoke(new Action(() => _onCommand(OverlayCommand.Cancel)));
            return;
        }

        var px = ToPhysical(e.GetPosition(this));
        if (_selectionPx is { } sel && RectContains(sel, px))
        {
            return; // inside the current selection — reserved, no-op
        }

        Dispatcher.BeginInvoke(new Action(() => _onCommand(OverlayCommand.Cancel)));
    }

    private void OnPreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (!_initialized) return; // D2 belt-and-braces — see _initialized's doc comment
        var dip = e.GetPosition(this);
        var px = ToPhysical(dip);

        if (IsMagnifierActive)
        {
            MagnifierControl.Update(_preview, _frame, dip, px);
        }

        // Select-tool cursor tracks hover (body/handle/elsewhere) and, while a Move/Resize/
        // NewSelection drag is in progress, the drag kind itself — see UpdateToolCursor. Every
        // drawing tool now needs the same per-move tracking too: any placed editable shape is
        // click-editable regardless of the active tool, so the cursor flips between the tool
        // brush and hand/arrows/SizeAll as it crosses shapes.
        UpdateToolCursor();

        switch (_dragMode)
        {
            case DragMode.NewSelection:
                if (_newSelectionPending)
                {
                    if ((px - _dragAnchorPx).LengthSquared < ClickDragThresholdPx * ClickDragThresholdPx)
                    {
                        break; // still a click candidate, not a drag — don't disturb any selection
                    }
                    _newSelectionPending = false;
                    _onSelectionStarted(this); // clears other monitors' selections, per DESIGN.md
                }
                SetSelection(RectPhysical.FromSize(
                    (int)Math.Min(_dragAnchorPx.X, px.X),
                    (int)Math.Min(_dragAnchorPx.Y, px.Y),
                    (int)Math.Abs(px.X - _dragAnchorPx.X),
                    (int)Math.Abs(px.Y - _dragAnchorPx.Y)));
                break;

            case DragMode.Move:
            {
                var delta = px - _dragAnchorPx;
                var moved = new RectPhysical(
                    (int)(_dragStartRect.Left + delta.X), (int)(_dragStartRect.Top + delta.Y),
                    (int)(_dragStartRect.Right + delta.X), (int)(_dragStartRect.Bottom + delta.Y));
                SetSelection(ClampToFrame(moved));
                break;
            }

            case DragMode.Resize:
                SetSelection(ApplyResize(_dragStartRect, _activeHandle, px));
                break;

            case DragMode.Annotation:
                Annotations.UpdateShape(px);
                break;

            case DragMode.SelectedMove:
                Annotations.TranslateSelected(px - _dragAnchorPx, new Size(_frame.Width, _frame.Height));
                break;

            case DragMode.SelectedResize:
            {
                // Byte-for-byte the same corner/edge math the crop selection's own resize uses.
                var resized = ClampToFrame(ApplyResize(_dragStartShapeRect, _activeHandle, px));
                Annotations.SetSelectedRect(
                    new Point(resized.Left, resized.Top), new Point(resized.Right, resized.Bottom));
                break;
            }

            case DragMode.SelectedEndpoint:
            {
                // Item 2: reposition just the dragged endpoint, clamped into the frame the same way
                // every other selected-shape drag is (SelectedMove/SelectedResize both clamp too).
                var clamped = new Point(
                    Math.Clamp(px.X, 0, _frame.Width), Math.Clamp(px.Y, 0, _frame.Height));
                Annotations.SetSelectedEndpoint(_dragEndpointIndex, clamped);
                break;
            }
        }
    }

    private void OnPreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_initialized) return; // D2 belt-and-braces — see _initialized's doc comment
        switch (_dragMode)
        {
            case DragMode.NewSelection:
                if (_newSelectionPending)
                {
                    // Mouse never travelled far enough to become a drag: this is a plain click —
                    // per the round-2 spec this now cancels the whole snip (like Esc) and opens the
                    // standalone ColorPickerWindow with the clicked pixel's color, replacing the old
                    // inline click panel.
                    _newSelectionPending = false;
                    TriggerColorPick(_dragAnchorPx, _dragAnchorDip);
                }
                else if (_selectionPx is { } sel)
                {
                    SetSelection(sel.Width < 2 || sel.Height < 2 ? null : ClampToFrame(sel));
                }
                break;

            case DragMode.Resize:
            case DragMode.Move:
                if (_selectionPx is { } sel2)
                {
                    SetSelection(ClampToFrame(sel2.Normalized()));
                }
                break;

            case DragMode.Annotation:
                // A just-placed click-editable shape (Pixelate/Rectangle/Ellipse/Line/Arrow) is
                // auto-selected: its chrome (and handles, where applicable) appears right away
                // (so move/resize/wheel-adjust are discoverable without the Select tool), and the
                // wheel immediately adjusts THIS shape (block size / stroke width) rather than the
                // next one's.
                var committed = Annotations.EndShape();
                if (committed is not null && AnnotationLayer.IsClickEditableTool(committed.Tool))
                {
                    Annotations.Select(committed);
                }
                break;

            case DragMode.SelectedMove:
            case DragMode.SelectedResize:
            case DragMode.SelectedEndpoint:
                Annotations.EndDragSelected();
                break;
        }

        bool wasAreaDrag = _dragMode is DragMode.NewSelection or DragMode.Move or DragMode.Resize;
        bool wasSelectedDrag = _dragMode is DragMode.SelectedMove or DragMode.SelectedResize or DragMode.SelectedEndpoint;
        _dragMode = DragMode.None;
        _activeHandle = SelectionHandle.None;
        if (wasSelectedDrag)
        {
            // Mirrors the NewSelection-drag re-evaluation below: the release point may now sit over
            // a different handle (or the plain body) than the one that was being dragged.
            UpdateToolCursor();
        }
        if (wasAreaDrag)
        {
            // The NewSelection/Move/Resize finalize branches above call SetSelection() while _dragMode
            // still holds the drag value (so UpdateToolbarPlacement's suppression check fired and kept
            // the toolbar hidden) — now that the drag has genuinely ended, re-run placement so the
            // toolbar (re)appears at the final rect. If the selection was cleared/invalid instead (e.g.
            // the click-to-pick path, or a too-small drag), _selectionPx is null and this just routes to
            // HideToolbar() as usual — never a spurious flash. Gated to area drags: an Annotation
            // mouse-up never hid the toolbar, and re-running ShowToolbar() there would rebuild the
            // palette panel after every single stroke for nothing.
            UpdateToolbarPlacement();
            // The release point of a NewSelection drag is, by construction, a corner of the fresh
            // selection — re-evaluate now so the cursor shows that handle's resize arrow immediately
            // instead of keeping the drag's crosshair until the next pointer jitter.
            UpdateToolCursor();
        }
        if (IsMouseCaptured)
        {
            ReleaseMouseCapture();
        }
        if (!IsMagnifierActive)
        {
            // The NewSelection-finalize path above calls SetSelection while _dragMode is still
            // NewSelection (so its own IsMagnifierActive check didn't hide it) — re-check now that
            // _dragMode has actually settled to None.
            MagnifierControl.Hide();
        }
    }

    private static bool RectContains(RectPhysical r, Point p)
    {
        var n = r.Normalized();
        return p.X >= n.Left && p.X <= n.Right && p.Y >= n.Top && p.Y <= n.Bottom;
    }

    private RectPhysical ClampToFrame(RectPhysical r)
    {
        var n = r.Normalized();
        int w = Math.Min(n.Width, _frame.Width);
        int h = Math.Min(n.Height, _frame.Height);
        int left = Math.Clamp(n.Left, 0, Math.Max(0, _frame.Width - w));
        int top = Math.Clamp(n.Top, 0, Math.Max(0, _frame.Height - h));
        return RectPhysical.FromSize(left, top, w, h);
    }

    private static RectPhysical ApplyResize(RectPhysical start, SelectionHandle handle, Point px)
    {
        int left = start.Left, top = start.Top, right = start.Right, bottom = start.Bottom;
        int x = (int)px.X, y = (int)px.Y;

        switch (handle)
        {
            case SelectionHandle.TopLeft: left = x; top = y; break;
            case SelectionHandle.Top: top = y; break;
            case SelectionHandle.TopRight: right = x; top = y; break;
            case SelectionHandle.Right: right = x; break;
            case SelectionHandle.BottomRight: right = x; bottom = y; break;
            case SelectionHandle.Bottom: bottom = y; break;
            case SelectionHandle.BottomLeft: left = x; bottom = y; break;
            case SelectionHandle.Left: left = x; break;
        }

        return new RectPhysical(left, top, right, bottom).Normalized();
    }

    /// <summary>True when the event source belongs to the toolbar's UI — including content hosted
    /// in a Popup (the size/font ComboBox dropdowns, right-click context menus). Popup content
    /// renders in its own PopupRoot visual tree, so a pure visual-parent walk never reaches the
    /// toolbar from a dropdown row and its clicks/scrolls fell through to the overlay's own
    /// select/draw/resize handlers. Each step prefers the LOGICAL parent (the link that bridges a
    /// popup's subtree back to the ComboBox/element that owns it) and falls back to the visual
    /// parent for template-generated elements that have no logical parent. A walk that dead-ends
    /// without reaching this window is popup chrome by definition (nothing inside the overlay's
    /// own tree dead-ends before the window), so it counts as toolbar UI too — that covers
    /// popup-rooted elements with no logical bridge, e.g. context-menu internals.</summary>
    private bool IsWithinToolbar(DependencyObject? source)
    {
        var current = source;
        while (current is not null)
        {
            if (_toolbar is not null && ReferenceEquals(current, _toolbar))
            {
                return true;
            }
            if (ReferenceEquals(current, this))
            {
                return false; // reached the overlay window itself — a genuine overlay-surface event
            }
            current = (current as FrameworkElement)?.Parent
                ?? (current is Visual ? VisualTreeHelper.GetParent(current) : LogicalTreeHelper.GetParent(current));
        }
        return source is not null; // dead-ended outside the window's tree: popup chrome
    }

    // ---------- Keyboard ----------

    /// <summary>Single handler for both Got/LostKeyboardFocus at the window level: the flag simply
    /// mirrors "the element gaining focus is a text input". Focus leaving the window entirely
    /// (NewFocus null / another HWND) lands here via LostKeyboardFocus and clears the flag.</summary>
    private void OnAnyKeyboardFocusChanged(object sender, KeyboardFocusChangedEventArgs e) =>
        _textInputFocused = e.NewFocus is System.Windows.Controls.Primitives.TextBoxBase;

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (!_initialized) return; // D2 belt-and-braces — see _initialized's doc comment
        if (e.Key is Key.LeftShift or Key.RightShift or Key.LeftCtrl or Key.RightCtrl)
        {
            UpdateToolCursor(); // modifier-grab affordance — see the PreviewKeyUp twin
        }
        if (_textInputFocused && _activeTextEditor is null)
        {
            // A toolbar text input (the size ComboBox) holds focus: every key — Enter included —
            // must tunnel on to it rather than be treated as a session command; the ComboBox's own
            // Enter/Esc handling commits/reverts and hands focus back (ToolbarControl). The
            // annotation editor case stays with ProcessKeyCommand, whose first branch implements
            // its Esc/Enter semantics.
            return;
        }
        if (ProcessKeyCommand(e.Key, Keyboard.Modifiers))
        {
            e.Handled = true;
        }
    }

    /// <summary>The single implementation of what each session key means, shared by two callers:
    /// this window's own WPF PreviewKeyDown (used whenever the overlay genuinely has OS focus) and
    /// SessionKeyboardHook's low-level keyboard hook (used unconditionally, since a background tray
    /// process cannot reliably steal foreground focus — see OverlayInputInterop.cs for the full
    /// root-cause writeup). Keeping one implementation guarantees the two paths can never disagree.
    /// Returns whether the key was consumed.</summary>
    internal bool ProcessKeyCommand(Key key, ModifierKeys modifiers)
    {
        if (_activeTextEditor is not null)
        {
            if (key == Key.Escape)
            {
                CancelActiveTextEditor();
                return true;
            }
            if (key == Key.Enter)
            {
                CommitActiveTextEditor();
                return true;
            }
            // Everything else (typing, Ctrl+C/paste within the box, etc.) must reach the real
            // focused TextBox through the normal WPF pipeline — not handled here, and the
            // low-level hook deliberately doesn't swallow these while text-editing either.
            return false;
        }

        // Feature B items 5/6: Delete/Back removes a selected annotation; Esc deselects it. Both are
        // a new stage strictly INNER than the session's CancelStage escalation below — an active
        // text edit (the only other local stage) was already consumed above, and this must run
        // before it so Esc never eats a whole snip-clear just because a leftover selection happened
        // to still be checked.
        if ((key == Key.Delete || key == Key.Back) && Annotations.SelectedShape is not null)
        {
            Annotations.DeleteSelected();
            return true;
        }
        if (key == Key.Escape && Annotations.SelectedShape is not null)
        {
            Annotations.Deselect();
            return true;
        }

        bool ctrl = (modifiers & ModifierKeys.Control) != 0;

        if (key == Key.Escape)
        {
            // The session's cross-monitor two-stage Esc (clear an in-progress snip on whichever
            // monitor holds it, else close the whole overlay) needs the session's view, so it
            // always escalates to CancelStage — an active text edit and a selected annotation (the
            // only other local Esc stages) were already consumed above.
            _onCommand(OverlayCommand.CancelStage);
            return true;
        }
        if (key == Key.Enter)
        {
            _onCommand(OverlayCommand.ConfirmPlain);
            return true;
        }
        if (ctrl && key == Key.C)
        {
            _onCommand(OverlayCommand.Copy);
            return true;
        }
        if (ctrl && key == Key.S)
        {
            _onCommand(OverlayCommand.Save);
            return true;
        }
        bool shift = (modifiers & ModifierKeys.Shift) != 0;

        if (ctrl && key == Key.Z)
        {
            if (shift)
            {
                Annotations.Redo(); // Ctrl+Shift+Z, the other common redo binding
            }
            else
            {
                Annotations.Undo();
            }
            return true;
        }
        if (ctrl && key == Key.Y)
        {
            Annotations.Redo();
            return true;
        }
        if (ctrl && key == Key.A)
        {
            // Select the whole monitor this window covers (like Snipping Tool's fullscreen mode,
            // but staying in the session so it can still be annotated/adjusted before Copy/Save).
            // Starting a new selection deselects any selected annotation, same as every other
            // new-gesture entry point (mouse-drag selection, switching to a drawing tool).
            Annotations.Deselect();
            _onSelectionStarted(this); // selection lives on exactly one monitor — clear the others
            SetSelection(RectPhysical.FromSize(0, 0, _frame.Width, _frame.Height));
            return true;
        }

        // Arrow keys fine-tune an existing selection without touching the mouse: move by 1 px
        // (Shift: 10 px), or with Ctrl held resize by moving the right/bottom edge instead.
        // Only when a selection exists and no drag owns the rect — mid-drag arrows would fight
        // the mouse's SetSelection stream.
        if (key is Key.Left or Key.Right or Key.Up or Key.Down
            && _selectionPx is { } arrowSel && _dragMode == DragMode.None)
        {
            int step = shift ? 10 : 1;
            int dx = key switch { Key.Left => -step, Key.Right => step, _ => 0 };
            int dy = key switch { Key.Up => -step, Key.Down => step, _ => 0 };
            var n = arrowSel.Normalized();
            var adjusted = ctrl
                ? new RectPhysical(n.Left, n.Top,
                    Math.Max(n.Left + 2, n.Right + dx), Math.Max(n.Top + 2, n.Bottom + dy))
                : new RectPhysical(n.Left + dx, n.Top + dy, n.Right + dx, n.Bottom + dy);
            SetSelection(ClampToFrame(adjusted));
            return true;
        }

        return false;
    }

    // ---------- Selection / toolbar / dim mask ----------

    private void SetSelection(RectPhysical? rect)
    {
        _selectionPx = rect;
        Adorner.SelectionPx = rect;
        Adorner.InvalidateVisual();
        UpdateDimGeometry();
        UpdateToolbarPlacement();
        if (!IsMagnifierActive)
        {
            MagnifierControl.Hide();
        }
    }

    /// <summary>Clears this window's selection/annotations — called by OverlayController when a
    /// new drag starts on a different monitor (selection lives on exactly one monitor at a time).</summary>
    internal void ClearSelection()
    {
        if (_dragMode != DragMode.None)
        {
            _dragMode = DragMode.None;
            _newSelectionPending = false;
            if (IsMouseCaptured)
            {
                ReleaseMouseCapture();
            }
        }
        CancelActiveTextEditor();
        Annotations.Clear();
        SetSelection(null);
    }


    private void UpdateDimGeometry()
    {
        double w = ActualWidth > 0 ? ActualWidth : _frame.Width / Math.Max(_scaleX, 1e-6);
        double h = ActualHeight > 0 ? ActualHeight : _frame.Height / Math.Max(_scaleY, 1e-6);
        var outer = new RectangleGeometry(new Rect(0, 0, w, h));

        if (_selectionPx is { } sel)
        {
            var n = sel.Normalized();
            var holeDip = new Rect(n.Left / _scaleX, n.Top / _scaleY, n.Width / _scaleX, n.Height / _scaleY);
            DimPath.Data = new CombinedGeometry(GeometryCombineMode.Exclude, outer, new RectangleGeometry(holeDip));
        }
        else
        {
            DimPath.Data = outer;
        }
    }

    private void UpdateToolbarPlacement()
    {
        if (_selectionPx is not { } sel)
        {
            HideToolbar();
            return;
        }

        // Suppress the toolbar for the duration of a drag that creates/moves/resizes the selection
        // area — SetSelection() runs on every mouse-move of such a drag, and showing the toolbar mid
        // -drag (then having it slide around under the cursor as the rect changes) is the bug this
        // guards against. Deliberately NOT HideToolbar(): that's the "selection cleared" teardown
        // (resets _currentTool + the toolbar's checked-tool state) — here the toolbar instance and
        // _currentTool must survive untouched, just visually collapsed until the drag ends (see the
        // extra UpdateToolbarPlacement() call in OnPreviewMouseLeftButtonUp once _dragMode settles
        // back to None). Annotation drags are deliberately excluded: they draw inside the selection
        // rather than changing it, so the toolbar should stay put.
        if (_dragMode is DragMode.NewSelection or DragMode.Move or DragMode.Resize)
        {
            if (_toolbar is not null)
            {
                _toolbar.Visibility = Visibility.Collapsed;
            }
            return;
        }

        ShowToolbar();
        if (_toolbar is not { } toolbar)
        {
            return;
        }

        var n = sel.Normalized();
        double x = n.Left / _scaleX;
        double y = n.Bottom / _scaleY + 8.0;

        double toolbarWidth = toolbar.ActualWidth > 0 ? toolbar.ActualWidth : 500.0;
        double toolbarHeight = toolbar.ActualHeight > 0 ? toolbar.ActualHeight : 84.0;

        if (ActualHeight > 0 && y + toolbarHeight > ActualHeight)
        {
            y = n.Top / _scaleY - toolbarHeight - 8.0; // flip above if it would go off the bottom
            if (y < 0)
            {
                y = 0;
            }
        }
        if (ActualWidth > 0 && x + toolbarWidth > ActualWidth)
        {
            x = ActualWidth - toolbarWidth;
        }
        x = Math.Max(0, x);

        Canvas.SetLeft(toolbar, x);
        Canvas.SetTop(toolbar, y);
    }

    private void ShowToolbar()
    {
        if (_toolbar is null)
        {
            _toolbar = new ToolbarControl();
            _toolbar.ToolSelected += tool =>
            {
                // Commit any still-open inline text editor FIRST, before evaluating the tool-scoped
                // deselect check below. A toolbar tool click never routes through the canvas's own
                // commit-on-click (OnPreviewMouseLeftButtonDown), so without this an editor left open
                // across a tool switch has nothing selected yet at switch time (the mismatch check is
                // a no-op against a null SelectedShape) and only gets auto-selected LATER when it
                // finally commits (Enter, or a canvas click) — by which point this handler has already
                // run and can't re-check it, leaking a tool-mismatched selection (S4 violation; e.g.
                // Delete would silently remove the text with a totally different tool's icon showing
                // as active). Committing here means the mismatch check that follows always sees the
                // FINAL post-commit selection.
                CommitActiveTextEditor();
                _currentTool = tool;
                // Item 4 (second editing round): selection is tool-scoped — switching to a
                // DIFFERENT drawing tool than the currently selected shape's own kind must drop
                // that selection (chrome/handles disappear, nothing stays grabbable), e.g. picking
                // Rectangle must not leave a selected Pixelate's handles live. Switching TO the
                // Select tool (None) is exempt — that's the tool selections are FOR, so it always
                // keeps whatever is selected. Deselect() commits any pending wheel gesture first
                // (CommitPendingDrag), so a mid-resize wheel tweak is never silently dropped by a
                // tool switch.
                if (tool != AnnotationTool.None
                    && Annotations.SelectedShape is { } selected && selected.Tool != tool)
                {
                    Annotations.Deselect();
                }
                UpdateToolCursor();
                // The text-style group appears/disappears with the Text tool, changing the
                // toolbar's width — re-clamp its placement once the new layout has measured
                // (ActualWidth is stale until then), so it can't hang off the screen edge.
                Dispatcher.BeginInvoke(new Action(UpdateToolbarPlacement),
                    System.Windows.Threading.DispatcherPriority.Loaded);
            };
            _toolbar.ColorSelected += color => { _currentColor = color; UpdateToolCursor(); };
            _toolbar.StrokeWidthSelected += width => { _currentStrokeWidth = width; UpdateToolCursor(); };
            _toolbar.UndoClicked += () => Annotations.Undo();
            _toolbar.RedoClicked += () => Annotations.Redo();
            // Keeps Undo/Redo grayed exactly when they'd be no-ops, whatever mutated the history
            // (toolbar clicks, Ctrl+Z/Y via either keyboard path, ClearSelection).
            Annotations.HistoryChanged += () =>
                _toolbar?.SetHistoryState(Annotations.CanUndo, Annotations.CanRedo);
            _toolbar.CopyClicked += () => _onCommand(OverlayCommand.Copy);
            _toolbar.SaveClicked += () => _onCommand(OverlayCommand.Save);
            _toolbar.SaveHdrClicked += () => _onCommand(OverlayCommand.SaveHdr);
            _toolbar.RecordMp4Clicked += () => _onCommand(OverlayCommand.RecordMp4);
            _toolbar.RecordGifClicked += () => _onCommand(OverlayCommand.RecordGif);
            // The toolbar's X button always closes the whole overlay outright — deliberately NOT
            // the staged CancelStage semantics Esc has.
            _toolbar.CancelClicked += () => _onCommand(OverlayCommand.Cancel);

            // Text-style group (item 4): live-applies to new annotations and to an in-progress edit.
            _toolbar.FontSizeSelected += size => SetTextFontSize(size, showIndicator: false);
            _toolbar.BoldToggled += bold =>
            {
                _textBold = bold;
                if (_activeTextEditor is not null)
                {
                    _activeTextEditor.FontWeight = bold ? FontWeights.Bold : FontWeights.Normal;
                }
            };
            _toolbar.ItalicToggled += italic =>
            {
                _textItalic = italic;
                if (_activeTextEditor is not null)
                {
                    _activeTextEditor.FontStyle = italic ? FontStyles.Italic : FontStyles.Normal;
                }
            };
            _toolbar.FontFamilySelected += family =>
            {
                _textFontFamily = family;
                if (_activeTextEditor is not null)
                {
                    _activeTextEditor.FontFamily = new FontFamily(family);
                }
            };

            // Editable palette (item 3, UX round 5): "+" appends via ColorDialog; each swatch's
            // right-click menu edits in place. All mutations persist immediately via SettingsStore.
            _toolbar.PaletteReplaceRequested += OnPaletteReplaceRequested;

            OverlayCanvas.Children.Add(_toolbar);
        }

        _toolbar.InitializeTextStyle(_textFontFamily, _textFontSize, _textBold, _textItalic);
        RefreshToolbarPalette();
        _toolbar.SetStrokeWidth(_currentStrokeWidth);
        _toolbar.SetHistoryState(Annotations.CanUndo, Annotations.CanRedo);
        _toolbar.Visibility = Visibility.Visible;
    }

    /// <summary>Pushes the current effective palette (persisted PaletteColors, or the migration
    /// seed of defaults + legacy CustomColors on a pre-round-5 settings file) into the toolbar,
    /// first snapping <see cref="_currentColor"/> onto the palette if it isn't in it — the checked
    /// swatch must never lie about the actual draw color.</summary>
    private void RefreshToolbarPalette()
    {
        var palette = SwatchPalette.EffectivePalette(_liveSettings.PaletteColors, _liveSettings.CustomColors);
        if (palette.Count > 0 && !PaletteContains(palette, _currentColor) && TryParseHex(palette[0]) is { } first)
        {
            _currentColor = first;
            UpdateToolCursor();
        }
        _toolbar?.SetPaletteColors(palette, _currentColor);
    }

    private static bool PaletteContains(IReadOnlyList<string> palette, Color color)
    {
        string hex = ColorFormatting.HexWithHash(color.R, color.G, color.B);
        foreach (var entry in palette)
        {
            if (string.Equals(entry, hex, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    private void HideToolbar()
    {
        if (_toolbar is not null)
        {
            _toolbar.Visibility = Visibility.Collapsed;
            // Keep the visible checked state in sync with _currentTool being reset below — the
            // toolbar instance is reused the next time a selection is made.
            _toolbar.ResetToolSelection();
        }
        _currentTool = AnnotationTool.None;
        UpdateToolCursor();
    }

    /// <summary>Tool-indicating cursor (item 1, UX round 3): every drawing tool gets a small custom
    /// cursor rendered from the current tool/color/strokeWidth via <see cref="ToolCursorCache"/>.
    /// Called on every change to tool, color, or stroke width — including scroll-wheel width changes
    /// — so a toolbar click back to the Select tool lands here too.
    /// For the Select tool (AnnotationTool.None), the cursor instead reflects what's currently under
    /// (or being dragged from) the cursor — grab hand over the selection body, a directional resize
    /// cursor over a handle, crosshair everywhere else including a NewSelection drag — via
    /// <see cref="CursorForHandle"/>. Re-hit-tests the live mouse position (via Mouse.GetPosition)
    /// rather than assuming nothing is hovered, since this can run right after a toolbar click with
    /// no intervening mouse-move. Pick-only mode has no selection/handle concept at all, so it always
    /// keeps the plain crosshair.</summary>
    private void UpdateToolCursor()
    {
        if (_currentTool != AnnotationTool.None)
        {
            // Every drawing tool: ANY already-placed editable shape is click-editable regardless
            // of the active tool (see the editsExistingShape branch in
            // OnPreviewMouseLeftButtonDown), so the cursor must advertise that instead of showing
            // the draw-a-new-one brush over it — hidden affordances aren't affordances. Same
            // hand/arrow/SizeAll vocabulary as the Select tool.
            if (!_pickOnlyMode)
            {
                if (_dragMode == DragMode.SelectedMove)
                {
                    Cursor = Cursors.Hand;
                    return;
                }
                if (_dragMode == DragMode.SelectedResize)
                {
                    Cursor = CursorForHandle(_activeHandle);
                    return;
                }
                if (_dragMode == DragMode.SelectedEndpoint)
                {
                    Cursor = CursorForSegmentEndpoint(Annotations.SelectedShape);
                    return;
                }
                if (_dragMode == DragMode.None)
                {
                    var hoverPx = ToPhysical(Mouse.GetPosition(this));
                    var hoverHandle = Annotations.HitTestSelectedHandle(hoverPx);
                    if (hoverHandle != SelectionHandle.None)
                    {
                        Cursor = CursorForHandle(hoverHandle);
                        return;
                    }
                    if (Annotations.HitTestSelectedEndpoint(hoverPx) >= 0)
                    {
                        Cursor = CursorForSegmentEndpoint(Annotations.SelectedShape);
                        return;
                    }
                    if (Annotations.HitTestEditable(hoverPx, interiorGrab: IsGrabModifierHeld) is not null)
                    {
                        Cursor = Cursors.Hand;
                        return;
                    }
                }
            }
            Cursor = _toolCursorCache.GetOrCreate(_currentTool, _currentColor, _currentStrokeWidth);
            return;
        }

        if (_pickOnlyMode)
        {
            Cursor = Cursors.Cross;
            return;
        }

        Cursor = _dragMode switch
        {
            DragMode.Move => Cursors.Hand,
            DragMode.Resize => CursorForHandle(_activeHandle),
            DragMode.NewSelection => Cursors.Cross,
            DragMode.SelectedMove => Cursors.Hand,
            DragMode.SelectedResize => CursorForHandle(_activeHandle),
            DragMode.SelectedEndpoint => CursorForSegmentEndpoint(Annotations.SelectedShape),
            _ => CursorForHover(ToPhysical(Mouse.GetPosition(this))),
        };
    }

    /// <summary>Directional resize cursor for a Line/Arrow endpoint, chosen by the segment's own
    /// angle — dragging an endpoint stretches ALONG the segment, so the pointer should say
    /// "resize this way" (a diagonal double-arrow on a diagonal line, WE/NS near the axes), not
    /// SizeAll's four-way move arrows, which read as "move the whole thing" — the grabbing hand
    /// already means that. Buckets at ~22.5° off each axis (tan 67.5° ≈ 2.414); screen Y grows
    /// downward, so same-sign dx/dy is the NW–SE diagonal.</summary>
    private static Cursor CursorForSegmentEndpoint(AnnotationShape? shape)
    {
        if (shape is null || shape.PointsPx.Count < 2)
        {
            return Cursors.SizeNWSE;
        }
        var d = shape.PointsPx[1] - shape.PointsPx[0];
        double adx = Math.Abs(d.X), ady = Math.Abs(d.Y);
        if (adx >= ady * 2.414)
        {
            return Cursors.SizeWE;
        }
        if (ady >= adx * 2.414)
        {
            return Cursors.SizeNS;
        }
        return (d.X > 0) == (d.Y > 0) ? Cursors.SizeNWSE : Cursors.SizeNESW;
    }

    /// <summary>Maps a selection hit-test result to the matching WPF cursor for the Select tool:
    /// Hand over the body (grabbable/movable), the directional resize cursor over each handle, and
    /// Cross for None (no handle under the point, or nothing to hit-test at all).</summary>
    private static Cursor CursorForHandle(SelectionHandle handle) => handle switch
    {
        SelectionHandle.Body => Cursors.Hand,
        SelectionHandle.TopLeft or SelectionHandle.BottomRight => Cursors.SizeNWSE,
        SelectionHandle.TopRight or SelectionHandle.BottomLeft => Cursors.SizeNESW,
        SelectionHandle.Top or SelectionHandle.Bottom => Cursors.SizeNS,
        SelectionHandle.Left or SelectionHandle.Right => Cursors.SizeWE,
        _ => Cursors.Cross,
    };

    /// <summary>Static (non-drag) Select-tool hover cursor — mirrors the click priority in
    /// OnPreviewMouseLeftButtonDown (item 8): the selected annotation's own handles/endpoints beat
    /// the crop selection's handles, which beat hovering an editable annotation body (Hand — item
    /// "hovering an editable annotation body -> Hand"), which beat the crop's own body/handle
    /// mapping.</summary>
    private Cursor CursorForHover(Point px)
    {
        if (Annotations.SelectedShape is not null)
        {
            var selectedHandle = Annotations.HitTestSelectedHandle(px);
            if (selectedHandle != SelectionHandle.None)
            {
                return CursorForHandle(selectedHandle);
            }
            // Item 2: Line/Arrow's endpoint handles — a directional resize pointer along the
            // segment's own angle, distinct from the plain Hand a body hover gets a few lines
            // below (an endpoint drag stretches the segment; the hand means "move the whole thing").
            if (Annotations.HitTestSelectedEndpoint(px) >= 0)
            {
                return CursorForSegmentEndpoint(Annotations.SelectedShape);
            }
        }

        var cropHandle = Adorner.HitTestHandle(px);
        if (cropHandle != SelectionHandle.None && cropHandle != SelectionHandle.Body)
        {
            return CursorForHandle(cropHandle);
        }

        if (Annotations.HitTestEditable(px, interiorGrab: IsGrabModifierHeld) is not null)
        {
            return Cursors.Hand;
        }

        return CursorForHandle(cropHandle); // Body -> Hand, None -> Cross
    }

    /// <summary>True while Shift or Ctrl is down — the modifier-grab chord (see the mouse-down
    /// branch): a click grabs the drawn object under the cursor, hit-testing outline interiors
    /// too, regardless of the active tool.</summary>
    private static bool IsGrabModifierHeld =>
        (Keyboard.Modifiers & (ModifierKeys.Shift | ModifierKeys.Control)) != 0;

    // ---------- Click-to-pick: standalone ColorPickerWindow (replaces the old inline click panel) ----------

    /// <summary>A plain click (normal mode) or any click (pick-only mode) samples the clicked
    /// pixel's color from the tone-mapped preview (what the user sees) and its HDR nits value from
    /// the raw FP16 CapturedFrame, auto-copies the hex to the clipboard (existing behavior, kept),
    /// hands the snapshot up to OverlayController's standalone ColorPickerWindow singleton, and
    /// cancels this session — the whole overlay closes, exactly like Esc. The Cancel is dispatched
    /// via BeginInvoke rather than called inline: Cancel synchronously closes every window in the
    /// session (including this one, from inside its own mouse-event handler), and deferring it to
    /// the next dispatcher cycle avoids touching a window that's already mid-teardown.</summary>
    private void TriggerColorPick(Point clickPx, Point clickDip)
    {
        int sx = Math.Clamp((int)clickPx.X, 0, _preview.Width - 1);
        int sy = Math.Clamp((int)clickPx.Y, 0, _preview.Height - 1);
        int o = sy * _preview.Stride + sx * 4;
        byte b = _preview.Pixels[o];
        byte g = _preview.Pixels[o + 1];
        byte r = _preview.Pixels[o + 2];
        double nits = _frame.ReadPixelNits(
            Math.Clamp(sx, 0, _frame.Width - 1),
            Math.Clamp(sy, 0, _frame.Height - 1));

        TryCopyTextToClipboard(string.Create(CultureInfo.InvariantCulture, $"#{r:X2}{g:X2}{b:X2}"));

        var screenPx = new Point(_frame.Monitor.BoundsPx.Left + clickPx.X, _frame.Monitor.BoundsPx.Top + clickPx.Y);
        var info = new PickedColorInfo(r, g, b, nits, screenPx, _frame.Monitor.DpiX, _frame.Monitor.DpiY);
        _onColorPicked(info);

        Dispatcher.BeginInvoke(new Action(() => _onCommand(OverlayCommand.Cancel)));
    }

    private static bool TryCopyTextToClipboard(string text)
    {
        try
        {
            System.Windows.Clipboard.SetText(text);
            return true;
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            // Clipboard transiently locked by another process — the panel reports the failure;
            // color inspection is a convenience, never worth crashing the overlay over.
            return false;
        }
    }

    // ---------- Inline text annotation (cuttable per PLAN.md §3.2 if it endangers the milestone) ----------

    /// <summary>Feature B item 4: double-click on a placed Text annotation (Select tool) reopens
    /// this SAME inline editor at the shape's own position, prefilled with its text/styling — reuses
    /// BeginTextEditor's machinery wholesale rather than a second editor implementation. The original
    /// shape is suppressed from AnnotationLayer's normal render for the duration (see
    /// SuppressedFromRender) so the floating editor doesn't show doubled text.</summary>
    private void BeginTextReEdit(AnnotationShape shape, Point clickDip)
    {
        var originPx = shape.PointsPx[0];
        var originDip = new Point(originPx.X / _scaleX, originPx.Y / _scaleY);
        BeginTextEditor(originPx, originDip, shape);
    }

    private void BeginTextEditor(Point originPx, Point originDip, AnnotationShape? editingExisting = null)
    {
        CommitActiveTextEditor();
        MagnifierControl.Hide(); // item 1a: text placement is a drag-start-equivalent state entry

        _textEditReplacing = editingExisting;
        if (editingExisting is not null)
        {
            Annotations.SuppressedFromRender = editingExisting;
            Annotations.InvalidateVisual();
        }

        var editColor = editingExisting?.StrokeColor ?? _currentColor;
        var editor = new TextBox
        {
            MinWidth = 140,
            FontSize = editingExisting?.StrokeWidthPx ?? _textFontSize,
            FontFamily = new FontFamily(editingExisting?.TextFontFamily ?? _textFontFamily),
            FontWeight = (editingExisting?.TextBold ?? _textBold) ? FontWeights.Bold : FontWeights.Normal,
            FontStyle = (editingExisting?.TextItalic ?? _textItalic) ? FontStyles.Italic : FontStyles.Normal,
            Foreground = new SolidColorBrush(editColor),
            Background = new SolidColorBrush(Color.FromArgb(0x70, 0, 0, 0)),
            BorderBrush = new SolidColorBrush(editColor),
            BorderThickness = new Thickness(1),
            AcceptsReturn = false,
            AcceptsTab = false,
            Text = editingExisting?.Text ?? string.Empty,
        };
        if (editingExisting is not null)
        {
            editor.CaretIndex = editor.Text.Length; // reopen with the caret at the end, not a full re-type
        }

        _activeTextEditor = editor;
        _activeTextEditorOriginPx = originPx;
        Canvas.SetLeft(editor, originDip.X);
        Canvas.SetTop(editor, originDip.Y);
        OverlayCanvas.Children.Add(editor);

        // The session hook consults this from its own (non-dispatcher) thread to decide whether
        // the normal WPF pipeline can be trusted with keys, so track it via focus events into a
        // volatile flag rather than reading Keyboard.FocusedElement cross-thread.
        editor.GotKeyboardFocus += (_, _) => _textEditorHasKeyboardFocus = true;
        editor.LostKeyboardFocus += (_, _) => _textEditorHasKeyboardFocus = false;

        editor.Focus();
        Keyboard.Focus(editor);

        // Reliability layer (b, item 3a of UX round 3): best-effort — a background tray process may
        // not hold the actual OS foreground grant (see OverlayInputInterop.cs), which normally
        // doesn't matter because the session-scoped WH_KEYBOARD_LL hook (layer a) handles
        // Esc/Enter/Ctrl+C/Ctrl+S/Ctrl+Z regardless of focus. Typing into this TextBox (and IME
        // composition) is different: it needs *real* keyboard/text-services focus, which the hook
        // deliberately does not fake there. So try hard via the full three-tier activation ladder —
        // but never let a failure be fatal: if every tier fails, TextEditKeyForwarder (item 3b, the
        // session hook's guaranteed fallback tier) still guarantees typing is never dead.
        ForegroundActivator.Activate(this, "text-edit");

        // Re-assert WPF keyboard focus AFTER the activation ladder settles: focus set before a
        // window becomes active can be lost during activation, which leaves keys routing to the
        // window instead of the editor even though everything "succeeded".
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Input, new Action(() =>
        {
            if (ReferenceEquals(_activeTextEditor, editor))
            {
                editor.Focus();
                Keyboard.Focus(editor);
            }
        }));
    }

    private void CommitActiveTextEditor()
    {
        if (_activeTextEditor is not { } editor)
        {
            return;
        }

        string text = editor.Text;
        double fontSize = editor.FontSize;
        var replacing = _textEditReplacing;
        OverlayCanvas.Children.Remove(editor);
        _activeTextEditor = null;
        _textEditReplacing = null;
        _textEditorHasKeyboardFocus = false;
        Annotations.SuppressedFromRender = null;

        if (replacing is not null)
        {
            // Feature B item 4: a re-edit commit REPLACES the original shape, never adds a new one —
            // an empty result is treated exactly like the new-text path's degenerate-text guard
            // below (silently no-op, leaving the original untouched) rather than deleting it; the
            // user asked to edit the text, not to remove the annotation.
            if (!string.IsNullOrWhiteSpace(text))
            {
                var replacement = new AnnotationShape
                {
                    Tool = AnnotationTool.Text,
                    StrokeColor = ((SolidColorBrush)editor.Foreground).Color,
                    StrokeWidthPx = fontSize,
                    Text = text,
                    TextFontFamily = editor.FontFamily.Source,
                    TextBold = editor.FontWeight == FontWeights.Bold,
                    TextItalic = editor.FontStyle == FontStyles.Italic,
                };
                replacement.PointsPx.Add(_activeTextEditorOriginPx);
                Annotations.ReplaceShape(replacing, replacement);
            }
            else
            {
                Annotations.InvalidateVisual(); // un-suppress the original so it renders again
            }
            return;
        }

        if (!string.IsNullOrWhiteSpace(text))
        {
            // Item 1 (second editing round): auto-select the newly committed text exactly like a
            // just-placed Pixelate/Rectangle/etc — its chrome shows up immediately so it reads as
            // grabbable/editable like every other kind, without a trip through the Select tool.
            var committed = Annotations.CommitText(
                _activeTextEditorOriginPx, text, _currentColor, fontSize, _textFontFamily, _textBold, _textItalic);
            if (committed is not null)
            {
                Annotations.Select(committed);
            }
        }
    }

    private void CancelActiveTextEditor()
    {
        if (_activeTextEditor is not { } editor)
        {
            return;
        }
        OverlayCanvas.Children.Remove(editor);
        _activeTextEditor = null;
        _textEditReplacing = null;
        _textEditorHasKeyboardFocus = false;
        if (Annotations.SuppressedFromRender is not null)
        {
            Annotations.SuppressedFromRender = null;
            Annotations.InvalidateVisual();
        }
    }

    // ---------- Scroll-wheel sizing (item 3) ----------

    /// <summary>A live text editor (new placement OR a re-edit) owns the wheel first, ahead of
    /// everything else: it's the visible source of truth for the font the user sees, so the wheel
    /// must drive IT directly (SetTextFontSize, which live-updates the TextBox) rather than the
    /// selected shape underneath it — which, during a re-edit, is render-suppressed (see
    /// AnnotationLayer.SuppressedFromRender), so mutating its font instead would silently desync it
    /// from what's actually on screen. Otherwise: a selected rect-resizable/segment/text shape owns
    /// it — under the Select tool AND under the shape's own drawing tool (a just-placed shape is
    /// auto-selected, so "place, then scroll to tune it" works in one flow): Pixelate scrolls its
    /// mosaic block size, Rectangle/Ellipse/Line/Arrow their stroke width, Text its font size. This
    /// deliberately beats the drawing tool's default wheel action (next-shape stroke/font) while a
    /// shape is selected; deselect (click empty space / draw elsewhere) to get that back. With
    /// nothing selected: the Select tool does nothing (no zoom), and every drawing tool (Text
    /// included) resizes the NEXT shape's stroke/font. Either way, a transient cursor-follower
    /// indicator shows the new size for ~600ms.</summary>
    private void OnPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (!_initialized) return; // D2 belt-and-braces — see _initialized's doc comment
        if (IsWithinToolbar(e.OriginalSource as DependencyObject))
        {
            return; // scrolling the toolbar or one of its dropdowns (size presets, font list) must
                    // scroll THAT list, never resize the active tool out from under the user
        }

        int notches = e.Delta / 120;
        if (notches == 0)
        {
            return;
        }

        var cursorDip = e.GetPosition(this);

        if (_activeTextEditor is not null)
        {
            double editorSize = SizeInput.ClampFont(_activeTextEditor.FontSize + notches * 2.0);
            SetTextFontSize(editorSize, showIndicator: true, cursorDip);
            e.Handled = true;
            return;
        }

        // A selected rect-resizable/segment/text shape owns the wheel — under the Select tool AND
        // under the shape's own drawing tool (a just-placed shape is auto-selected, so "place, then
        // scroll to tune it" works in one flow): Pixelate scrolls its mosaic block size,
        // Rectangle/Ellipse/Line/Arrow their stroke width, Text its own font size (item 1 of the
        // second editing round moved Text in here from its own separate Select-tool-only branch —
        // it now works the same way under either the Select tool OR the Text tool, matching every
        // other click-editable kind).
        if (Annotations.SelectedShape is { } selectedShape
            && (_currentTool == AnnotationTool.None || _currentTool == selectedShape.Tool))
        {
            // Each branch ALSO adopts the dialed size as the CURRENT tool size (SetStrokeWidth /
            // SetTextFontSize instead of a bare indicator): the tool's brush cursor visibly grows
            // and shrinks with the wheel (it renders from _currentStrokeWidth, which a
            // selected-shape-only mutation left stale — user-reported), the toolbar's size box
            // stays honest, and the next shape drawn inherits the size just dialed in.
            if (selectedShape.Tool == AnnotationTool.Pixelate)
            {
                Annotations.BeginDragSelected(); // no-op if a gesture from an earlier notch is still open
                double newBlock = Math.Clamp(selectedShape.StrokeWidthPx + notches * 2.0, 3.0, SizeInput.MaxStrokePx);
                Annotations.SetSelectedPixelateBlock(newBlock);
                SetStrokeWidth(newBlock, cursorDip);
                e.Handled = true;
                return;
            }
            if (selectedShape.Tool is AnnotationTool.Rectangle or AnnotationTool.Ellipse
                                   or AnnotationTool.Line or AnnotationTool.Arrow)
            {
                Annotations.BeginDragSelected();
                double newWidth = SizeInput.ClampStroke(selectedShape.StrokeWidthPx + notches);
                Annotations.SetSelectedStrokeWidth(newWidth);
                SetStrokeWidth(newWidth, cursorDip);
                e.Handled = true;
                return;
            }
            if (selectedShape.Tool == AnnotationTool.Text)
            {
                // _activeTextEditor is null here (handled and returned above already), so the
                // selected shape is the live, on-screen text — safe to mutate directly.
                Annotations.BeginDragSelected(); // no-op if a gesture from an earlier notch is still open
                double resizedFont = SizeInput.ClampFont(selectedShape.StrokeWidthPx + notches * 2.0);
                Annotations.SetSelectedFontSize(resizedFont);
                SetTextFontSize(resizedFont, showIndicator: true, cursorDip);
                e.Handled = true;
                return;
            }
        }

        if (_currentTool == AnnotationTool.None)
        {
            // Select tool, nothing selected: if the magnifier loupe is up (dimmed screen, no
            // selection/drag), the wheel zooms it — up = smaller sampled area = more zoom. The
            // dialed level persists immediately (same pattern as palette edits) so it is the
            // default for every later session.
            if (IsMagnifierActive)
            {
                int newRadius = Math.Clamp(
                    MagnifierControl.SampleRadius - notches, Magnifier.MinSampleRadius, Magnifier.MaxSampleRadius);
                if (newRadius != MagnifierControl.SampleRadius)
                {
                    MagnifierControl.SampleRadius = newRadius;
                    _liveSettings = _liveSettings with { MagnifierSampleRadius = newRadius };
                    TrySaveLiveSettings();
                }
                e.Handled = true;
            }
            return; // otherwise: nothing selected (a selected shape was already handled+returned above)
        }

        if (_currentTool == AnnotationTool.Text)
        {
            double newSize = SizeInput.ClampFont(_textFontSize + notches * 2.0);
            SetTextFontSize(newSize, showIndicator: true, cursorDip);
        }
        else
        {
            double newWidth = SizeInput.ClampStroke(_currentStrokeWidth + notches);
            SetStrokeWidth(newWidth, cursorDip);
        }

        e.Handled = true;
    }

    private void SetTextFontSize(double size, bool showIndicator, Point cursorDip = default)
    {
        _textFontSize = size;
        _toolbar?.SetFontSize(size);
        if (_activeTextEditor is not null)
        {
            _activeTextEditor.FontSize = size;
        }
        if (showIndicator)
        {
            ShowSizeIndicator(cursorDip, string.Create(CultureInfo.InvariantCulture, $"{size:0}pt"), circleDiameterDip: null);
        }
    }

    private void SetStrokeWidth(double width, Point cursorDip)
    {
        _currentStrokeWidth = width;
        _toolbar?.SetStrokeWidth(width);
        UpdateToolCursor();
        ShowSizeIndicator(cursorDip, string.Create(CultureInfo.InvariantCulture, $"{width:0}px"), circleDiameterDip: width);
    }

    /// <summary>A single reusable indicator element, re-triggered (not stacked) on each wheel tick:
    /// updates content/position and restarts the same ~600ms fade-out timer rather than layering a
    /// new fading element per tick, which would otherwise flicker under a fast wheel.</summary>
    private void ShowSizeIndicator(Point cursorDip, string label, double? circleDiameterDip)
    {
        if (_sizeIndicator is null)
        {
            _sizeIndicator = new System.Windows.Controls.Border
            {
                Background = new SolidColorBrush(Color.FromArgb(0xCC, 0x14, 0x14, 0x16)),
                BorderBrush = Brushes.White,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                IsHitTestVisible = false,
            };
            OverlayCanvas.Children.Add(_sizeIndicator);
        }
        else
        {
            _sizeIndicator.BeginAnimation(OpacityProperty, null); // cancel any in-flight fade
        }

        var content = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(8, 5, 8, 5) };
        if (circleDiameterDip is { } d)
        {
            // Exact 1:1 with the brush at the low end — the dot IS the brush size preview (user
            // feedback: "it should always be the same size as whatever we'll be drawing"). Only
            // capped at 40 to keep the popup compact; the numeric label carries the truth there.
            double clamped = Math.Clamp(d, 1.0, 40.0);
            content.Children.Add(new System.Windows.Shapes.Ellipse
            {
                Width = clamped,
                Height = clamped,
                Fill = Brushes.White,
                Margin = new Thickness(0, 0, 8, 0),
                VerticalAlignment = VerticalAlignment.Center,
            });
        }
        content.Children.Add(new TextBlock
        {
            Text = label,
            Foreground = Brushes.White,
            FontFamily = new FontFamily("Consolas"),
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
        });

        _sizeIndicator.Child = content;
        _sizeIndicator.Opacity = 1.0;
        _sizeIndicator.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));

        double x = cursorDip.X + 20.0;
        double y = cursorDip.Y + 20.0;
        Canvas.SetLeft(_sizeIndicator, x);
        Canvas.SetTop(_sizeIndicator, y);

        _sizeIndicatorTimer?.Stop();
        _sizeIndicatorTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
        _sizeIndicatorTimer.Tick += (_, _) =>
        {
            _sizeIndicatorTimer?.Stop();
            var fade = new DoubleAnimation(1.0, 0.0, TimeSpan.FromMilliseconds(250))
            {
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
            };
            _sizeIndicator?.BeginAnimation(OpacityProperty, fade);
        };
        _sizeIndicatorTimer.Start();
    }

    // ---------- Editable palette (item 3, UX round 5; replaces the old CustomColors row) ----------

    /// <summary>Opens System.Windows.Forms.ColorDialog (FullOpen so the full custom-color editor is
    /// visible immediately) owned by this window's HWND so it stacks correctly above the topmost
    /// overlay — shared by the "+" swatch and every swatch's right-click "Replace...".</summary>
    private bool TryPickColorFromDialog(out Color color)
    {
        color = default;

        using var dialog = new System.Windows.Forms.ColorDialog
        {
            FullOpen = true,
            AnyColor = true,
        };

        IntPtr hwnd = new WindowInteropHelper(this).Handle;
        var owner = new Win32WindowHandle(hwnd);

        if (dialog.ShowDialog(owner) != System.Windows.Forms.DialogResult.OK)
        {
            return false;
        }

        color = Color.FromRgb(dialog.Color.R, dialog.Color.G, dialog.Color.B);
        return true;
    }

    /// <summary>The current palette as displayed: the persisted list, or (pre-round-5 settings
    /// file) the migration seed — every mutation below starts from this so the first edit
    /// materializes the migrated list into PaletteColors.</summary>
    private List<string> CurrentEffectivePalette() =>
        SwatchPalette.EffectivePalette(_liveSettings.PaletteColors, _liveSettings.CustomColors);

    /// <summary>Persists a palette mutation immediately (like the old custom-color flow) and
    /// refreshes the toolbar's swatch row from it.</summary>
    private void UpdatePalette(List<string> palette)
    {
        _liveSettings = _liveSettings with { PaletteColors = palette };
        TrySaveLiveSettings();
        RefreshToolbarPalette();
    }

    private static Color? TryParseHex(string hex)
    {
        try
        {
            return System.Windows.Media.ColorConverter.ConvertFromString(hex) as Color?;
        }
        catch (FormatException)
        {
            return null; // a hand-edited settings file can hold garbage; never crash over it
        }
    }

    /// <summary>Right-click "Replace...": a ColorDialog pick swaps that palette entry in place.
    /// If the replaced swatch was the active color, the replacement becomes active.</summary>
    private void OnPaletteReplaceRequested(int index)
    {
        var palette = CurrentEffectivePalette();
        if (index < 0 || index >= palette.Count || !TryPickColorFromDialog(out var picked))
        {
            return;
        }

        bool wasActive = string.Equals(
            palette[index], ColorFormatting.HexWithHash(_currentColor.R, _currentColor.G, _currentColor.B),
            StringComparison.OrdinalIgnoreCase);
        if (wasActive)
        {
            _currentColor = picked;
            UpdateToolCursor();
        }

        string hex = ColorFormatting.HexWithHash(picked.R, picked.G, picked.B);
        UpdatePalette(SwatchPalette.ReplaceAt(palette, index, hex));
    }

    private void TrySaveLiveSettings()
    {
        try
        {
            SettingsStore.Save(_liveSettings);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"RoeSnip: failed to save live overlay settings: {ex.Message}");
        }
    }

    /// <summary>Minimal System.Windows.Forms.IWin32Window adapter so ColorDialog.ShowDialog can be
    /// owned by this WPF window's HWND (WindowInteropHelper) instead of popping up unowned behind
    /// the topmost overlay.</summary>
    private sealed class Win32WindowHandle : System.Windows.Forms.IWin32Window
    {
        public Win32WindowHandle(IntPtr handle) => Handle = handle;
        public IntPtr Handle { get; }
    }

    // ---------- Export / feedback ----------

    /// <summary>Renders the current selection's crop with all committed annotations burned in, at
    /// 1:1 physical-pixel scale (96 DPI both axes — see SdrImage.ToBitmapSource's contract).</summary>
    internal SdrImage RenderSelectionWithAnnotations()
    {
        if (_selectionPx is not { } sel)
        {
            throw new InvalidOperationException("OverlayWindow has no active selection to render.");
        }

        var n = sel.Normalized();
        var cropped = _preview.Crop(n);

        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawImage(cropped.ToBitmapSource(), new Rect(0, 0, n.Width, n.Height));
            Annotations.RenderForExport(dc, new Point(n.Left, n.Top));
        }

        var rtb = new RenderTargetBitmap(Math.Max(1, n.Width), Math.Max(1, n.Height), 96, 96, PixelFormats.Pbgra32);
        rtb.Render(visual);

        var converted = new FormatConvertedBitmap(rtb, PixelFormats.Bgra32, null, 0);
        int stride = n.Width * 4;
        var pixels = new byte[stride * n.Height];
        converted.CopyPixels(pixels, stride, 0);

        return new SdrImage(n.Width, n.Height, pixels);
    }

    internal void ShowShutterFlash()
    {
        var flash = new System.Windows.Shapes.Rectangle
        {
            Fill = Brushes.White,
            IsHitTestVisible = false,
            Width = Math.Max(1.0, ActualWidth),
            Height = Math.Max(1.0, ActualHeight),
            Opacity = 0.85,
        };
        OverlayCanvas.Children.Add(flash);

        var animation = new DoubleAnimation(0.85, 0.0, TimeSpan.FromMilliseconds(220))
        {
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
        };
        animation.Completed += (_, _) => OverlayCanvas.Children.Remove(flash);
        flash.BeginAnimation(OpacityProperty, animation);
    }

    internal void CloseOverlay() => Close();
}
