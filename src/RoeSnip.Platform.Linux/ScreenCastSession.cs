using System.Diagnostics;
using RoeSnip.Core.Capture;
using RoeSnip.Core.Diagnostics;
using Tmds.DBus;

namespace RoeSnip.Platform.Linux;

/// <summary>A kept-open org.freedesktop.portal.ScreenCast session (MONITOR sources, multiple,
/// persist_mode=2) with one GStreamer pipeline per shared monitor. One per process:
/// <see cref="Snapshot"/> (cursor not in the pixels: cursor_mode METADATA when offered, read by
/// <see cref="PipeWireCursorReader"/>, else HIDDEN; every stream started at open, kept for the process life),
/// used by captures and recordings alike so the share dialog appears once ever (owner rule: a
/// second session with its own token meant a second dialog). Its restore token is saved; a restore
/// that still shows a dialog means the token was rejected (GNOME then saves a new one).
///
/// Opening runs on a background task. Callers wait for it with a bound. The Screenshot portal is
/// used only when the desktop cannot screencast at all (<see cref="State"/> Unsupported) and never
/// on GNOME (shutter sound and flash), see <see cref="LinuxCapturePolicy"/>.</summary>
internal sealed class ScreenCastSession
{
    public static readonly ScreenCastSession Snapshot = new("snapshot", ScreenCastPortal.CursorMetadata, startAllStreams: true, needsPermission: true);

    private static readonly TimeSpan CursorMetaCheckAfter = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan IdleAfterOpen = TimeSpan.FromSeconds(5);

    /// <summary>Why the snapshot session does not open: no saved token and the user has not pressed
    /// asked (a capture or Settings' "Allow instant capture"); the dialog never opens on its own.</summary>
    public const string NotAllowedReason = "instant capture not allowed yet (press PrintScreen or Settings > Allow instant capture)";

    private static readonly TimeSpan DialogTimeout = TimeSpan.FromSeconds(180);
    private static readonly TimeSpan StepTimeout = TimeSpan.FromSeconds(20);
    private const long ReopenMinIntervalMs = 10_000;
    private const long RetryAfterFailureMs = 30_000;
    private static int s_indicatorLogged;

    private readonly string _purpose;
    private readonly uint _cursorMode;
    private readonly bool _startAllStreams;
    private readonly ScreenCastTokenStore _tokens;
    private readonly object _gate = new();
    private Task<Live?>? _opening;
    private Live? _live;
    private string? _deniedReason;     // the user cancelled the dialog: never re-ask this process
    private string? _lastFailure;      // last open failed (retried after RetryAfterFailureMs)
    private string? _unsupportedReason; // the desktop cannot screencast: never retried this process
    private long _retryAfterTick;
    private long _lastOpenStartTick;
    private bool _forceDialogNext;
    private bool _dialogAttemptedForTopology;
    private readonly bool _needsPermission;
    private bool _dialogRequested;     // Settings' "Allow instant capture" was pressed this process
    private int _notAllowedLogged;
    private int _generation; // bumped by Close(); an open finishing under an older one is dropped

    private ScreenCastSession(string purpose, uint cursorMode, bool startAllStreams, bool needsPermission)
    {
        _needsPermission = needsPermission;
        _purpose = purpose;
        _cursorMode = cursorMode;
        _startAllStreams = startAllStreams;
        _tokens = ScreenCastTokenStore.ForPurpose(purpose);
        ScreenCastTokenStore.ForPurpose("recording").Save(null); // unused token file; deleting it never prompts
    }

    public static bool IsWaylandSession =>
        string.Equals(Environment.GetEnvironmentVariable("XDG_SESSION_TYPE"), "wayland", StringComparison.OrdinalIgnoreCase)
        || !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY"));

    public bool HasSavedToken => _tokens.Load() is not null;

    /// <summary>True while a session is open and alive.</summary>
    public bool IsLive
    {
        get { lock (_gate) { return _live is { IsDead: false }; } }
    }

    /// <summary>The live session, or null; never starts an open.</summary>
    public Live? CurrentLive
    {
        get { lock (_gate) { return _live is { IsDead: false } live ? live : null; } }
    }

    /// <summary>The snapshot screencast state for <see cref="LinuxCapturePolicy"/>; X11 is Unsupported.</summary>
    public ScreenCastState State
    {
        get
        {
            lock (_gate)
            {
                if (!IsWaylandSession || _unsupportedReason is not null) return ScreenCastState.Unsupported;
                if (_live is { IsDead: false }) return ScreenCastState.Live;
                if (_opening is { IsCompleted: false }) return ScreenCastState.Opening;
                if (_deniedReason is not null) return ScreenCastState.Denied;
                if (_lastFailure is not null) return ScreenCastState.Failed;
                return ScreenCastState.Idle;
            }
        }
    }

    /// <summary>Why the screencast cannot be used right now (for logs); empty when live.</summary>
    public string UnavailableReason
    {
        get
        {
            lock (_gate)
            {
                if (!IsWaylandSession) return "X11 session";
                if (_unsupportedReason is not null) return _unsupportedReason;
                if (_live is { IsDead: false }) return "";
                if (_opening is { IsCompleted: false }) return "screencast is still opening";
                return _deniedReason ?? _lastFailure ?? (_needsPermission && !_dialogRequested && !HasSavedToken
                    ? NotAllowedReason : "screencast not opened yet");
            }
        }
    }

    /// <summary>The share dialog is allowed only with a saved token (silent restore) or after the
    /// user asked for it (a capture or Settings). Logs the refusal once per process.</summary>
    private bool NotAllowedLocked()
    {
        if (!_needsPermission || _dialogRequested || HasSavedToken)
        {
            return false;
        }
        if (Interlocked.Exchange(ref _notAllowedLogged, 1) == 0)
        {
            FileLog.Write($"RoeSnip: screencast {_purpose} not opened: {NotAllowedReason}; the share dialog never opens on its own");
        }
        return true;
    }

    /// <summary>Opens the session with the share dialog (Settings' "Allow instant capture", or a
    /// user's capture before it was allowed). Clears an earlier refusal, since the user asked again.</summary>
    public Task<ScreenShareRequestResult> RequestPermissionAsync(string source)
    {
        Task<Live?> task;
        lock (_gate)
        {
            if (!IsWaylandSession || _unsupportedReason is not null)
            {
                return Task.FromResult(ScreenShareRequestResult.Unsupported);
            }
            _dialogRequested = true;
            _deniedReason = null;
            _lastFailure = null;
            _retryAfterTick = 0;
            if (_live is { IsDead: false }) return Task.FromResult(ScreenShareRequestResult.Allowed);
            if (_live is not null) DropLiveLocked("a stream died");
            if (_opening is null || _opening.IsCompleted) _opening = StartOpenLocked();
            task = _opening;
        }
        FileLog.Write($"RoeSnip: screencast {_purpose} share dialog requested from {source}");
        return task.ContinueWith(t =>
        {
            if (t.Result is not null) return ScreenShareRequestResult.Allowed;
            lock (_gate)
            {
                return _unsupportedReason is not null ? ScreenShareRequestResult.Unsupported : ScreenShareRequestResult.Declined;
            }
        }, TaskScheduler.Default);
    }

    /// <summary>Returns the open session, starting an open if none is live, and waits up to
    /// <paramref name="wait"/> for it. Null with <paramref name="reason"/> when not ready.</summary>
    public Live? Acquire(TimeSpan wait, out string reason)
    {
        Task<Live?> task;
        lock (_gate)
        {
            if (_live is { IsDead: false } live) { reason = ""; return live; }
            if (_live is not null) DropLiveLocked("a stream died");
            if (_unsupportedReason is not null) { reason = _unsupportedReason; return null; }
            if (_deniedReason is not null) { reason = _deniedReason; return null; }
            if (_opening is null || _opening.IsCompleted)
            {
                if (NotAllowedLocked()) { reason = NotAllowedReason; return null; }
                if (_lastFailure is not null && Environment.TickCount64 < _retryAfterTick)
                {
                    reason = _lastFailure;
                    return null;
                }
                _opening = StartOpenLocked();
            }
            task = _opening;
        }

        if (!task.Wait(wait))
        {
            reason = "screencast is still opening (share dialog waiting for an answer?)";
            return null;
        }
        Live? result = task.Result;
        lock (_gate)
        {
            reason = result is null ? (_unsupportedReason ?? _deniedReason ?? _lastFailure ?? "screencast open failed") : "";
        }
        return result;
    }

    /// <summary>Starts an open in the background if nothing is live or opening (warmup, or after a
    /// stream died). Never shows the dialog unless no token is saved.</summary>
    public void OpenInBackground()
    {
        lock (_gate)
        {
            if (_live is { IsDead: false } || _deniedReason is not null || _unsupportedReason is not null) return;
            if (_opening is { IsCompleted: false }) return;
            if (_lastFailure is not null && Environment.TickCount64 < _retryAfterTick) return;
            if (NotAllowedLocked()) return;
            _opening = StartOpenLocked();
        }
    }

    /// <summary>A capture found no stream for a monitor (hotplug, or the user shared fewer
    /// monitors). Reopens with the token once; if that still misses a monitor the next reopen
    /// drops the token so the dialog can offer every monitor, once per topology.</summary>
    public void ReportUnmappedMonitor(string monitorsSignature)
    {
        lock (_gate)
        {
            if (Environment.TickCount64 - _lastOpenStartTick < ReopenMinIntervalMs) return;
            if (_live is null || _opening is { IsCompleted: false }) return;
            if (_live.TopologySignature != monitorsSignature)
            {
                _dialogAttemptedForTopology = false; // new topology: the token may cover it
            }
            else if (!_dialogAttemptedForTopology)
            {
                _forceDialogNext = true;
                _dialogAttemptedForTopology = true;
            }
            else
            {
                return; // already asked for this topology
            }
            FileLog.Write($"RoeSnip: screencast {_purpose} has no stream for a monitor; reopening" +
                (_forceDialogNext ? " with the share dialog" : " with the saved token"));
            DropLiveLocked("monitor topology changed");
            _opening = StartOpenLocked(monitorsSignature);
        }
    }

    /// <summary>Closes the session. The token stays saved.</summary>
    public void Close()
    {
        lock (_gate)
        {
            _generation++;
            if (_live is not null) DropLiveLocked("closed by RoeSnip");
        }
    }

    private void DropLiveLocked(string why)
    {
        var live = _live;
        _live = null;
        if (live is null) return;
        FileLog.Write($"RoeSnip: screencast {_purpose} session closing ({why})");
        _ = Task.Run(live.Dispose);
    }

    private Task<Live?> StartOpenLocked(string? topologySignature = null)
    {
        _lastOpenStartTick = Environment.TickCount64;
        bool forceDialog = _forceDialogNext;
        _forceDialogNext = false;
        int generation = _generation;
        return Task.Run(async () =>
        {
            try
            {
                var live = await OpenAsync(forceDialog, topologySignature).ConfigureAwait(false);
                live.Died += OnLiveDied;
                lock (_gate)
                {
                    if (generation != _generation)
                    {
                        // Close() ran while this open was in flight (a take ended first).
                        _ = Task.Run(live.Dispose);
                        return null;
                    }
                    _live = live;
                    _lastFailure = null;
                }
                return (Live?)live;
            }
            catch (Exception ex)
            {
                string reason = ex is PortalResponseException or InvalidOperationException or TimeoutException
                    ? ex.Message
                    : $"{ex.GetType().Name}: {ex.Message}";
                lock (_gate)
                {
                    _lastFailure = reason;
                    _retryAfterTick = Environment.TickCount64 + RetryAfterFailureMs;
                    if (ex is PortalResponseException { Response: 1 })
                    {
                        _deniedReason = reason;
                    }
                    if (ex is ScreenCastUnsupportedException)
                    {
                        _unsupportedReason = reason;
                    }
                }
                FileLog.Write($"RoeSnip: screencast {_purpose} session failed to open: {reason}");
                return null;
            }
        });
    }

    private void OnLiveDied(Live live, string why)
    {
        bool reopen;
        lock (_gate)
        {
            if (!ReferenceEquals(_live, live)) return;
            DropLiveLocked(why);
            reopen = _startAllStreams && Environment.TickCount64 - _lastOpenStartTick >= ReopenMinIntervalMs;
        }
        // GNOME closes screencasts at screen lock / logout; a restore asked then waits unseen (Start
        // got no portal response within 180s). Leave that one to the next capture or the next start.
        if (why.StartsWith("the portal closed the session", StringComparison.Ordinal)) return;
        // Silent with a saved token; the snapshot stream is the capture path, so get it back now.
        if (reopen && HasSavedToken) OpenInBackground();
    }

    private async Task<Live> OpenAsync(bool forceDialog, string? topologySignature)
    {
        var watch = Stopwatch.StartNew();
        if (!Gst.Available)
        {
            throw new ScreenCastUnsupportedException("GStreamer unavailable: " + Gst.UnavailableReason);
        }
        foreach (var element in new[] { "pipewiresrc", "videoconvert", "appsink" })
        {
            if (!Gst.HasElement(element))
            {
                throw new ScreenCastUnsupportedException($"GStreamer element '{element}' is not installed");
            }
        }

        string? address = Address.Session;
        if (string.IsNullOrEmpty(address))
        {
            throw new ScreenCastUnsupportedException("no D-Bus session bus");
        }

        PortalConnection shared;
        try
        {
            shared = await PortalConnection.GetAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            throw new ScreenCastUnsupportedException("could not connect to the D-Bus session bus: " + ex.Message);
        }
        var connection = shared.Connection;
        string sender = shared.SenderPart;
        CloseSafeHandle? fd = null;
        Live? live = null;
        ObjectPath? openedSession = null;
        try
        {
            var portal = connection.CreateProxy<IPortalScreenCast>(ScreenCastPortal.Service, ScreenCastPortal.DesktopPath);

            uint version;
            try
            {
                version = await portal.GetAsync<uint>("version").ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                throw new ScreenCastUnsupportedException("the ScreenCast portal is not available: " + ex.Message);
            }
            uint cursorModes = 0;
            try { cursorModes = await portal.GetAsync<uint>("AvailableCursorModes").ConfigureAwait(false); }
            catch { /* version 1 portals have no cursor modes */ }

            var created = await ScreenCastPortal.RequestAsync(connection, sender, "CreateSession",
                token => portal.CreateSessionAsync(new Dictionary<string, object>
                {
                    ["handle_token"] = token,
                    ["session_handle_token"] = "roesnip_" + Guid.NewGuid().ToString("N"),
                }), StepTimeout).ConfigureAwait(false);
            if (!created.TryGetValue("session_handle", out var handleValue) || handleValue is null)
            {
                throw new InvalidOperationException("CreateSession returned no session_handle");
            }
            var sessionPath = new ObjectPath(handleValue.ToString()!);
            openedSession = sessionPath;

            string? restoreToken = forceDialog ? null : _tokens.Load();
            uint cursorMode = (cursorModes & _cursorMode) != 0 ? _cursorMode
                : (cursorModes & ScreenCastPortal.CursorHidden) != 0 ? ScreenCastPortal.CursorHidden : 0;
            var select = new Dictionary<string, object>
            {
                ["types"] = ScreenCastPortal.SourceMonitor,
                ["multiple"] = true,
            };
            if (cursorMode != 0) select["cursor_mode"] = cursorMode;
            if (version >= 4)
            {
                select["persist_mode"] = ScreenCastPortal.PersistUntilRevoked;
                if (restoreToken is not null) select["restore_token"] = restoreToken;
            }
            await ScreenCastPortal.RequestAsync(connection, sender, "SelectSources",
                token =>
                {
                    select["handle_token"] = token;
                    return portal.SelectSourcesAsync(sessionPath, select);
                }, StepTimeout).ConfigureAwait(false);

            // A failed Start keeps the saved token: a timeout (locked screen) or a dismissed dialog
            // says nothing about it, and deleting it made every later login ask again. A token GNOME
            // no longer honours just brings up its dialog (prefilled), whose answer saves a new one.
            IDictionary<string, object> started = await ScreenCastPortal.RequestAsync(connection, sender, "Start",
                token => portal.StartAsync(sessionPath, "", new Dictionary<string, object> { ["handle_token"] = token }),
                DialogTimeout).ConfigureAwait(false);

            var geometries = ScreenCastPortal.ParseStreams(started.TryGetValue("streams", out var sv) ? sv : null);
            if (geometries.Count == 0)
            {
                throw new InvalidOperationException("Start returned no streams");
            }
            string? newToken = started.TryGetValue("restore_token", out var tv) ? tv as string : null;
            if (!string.IsNullOrEmpty(newToken)) _tokens.Save(newToken);

            fd = await portal.OpenPipeWireRemoteAsync(sessionPath, new Dictionary<string, object>()).ConfigureAwait(false);
            int rawFd = fd.DangerousGetHandle().ToInt32();

            live = new Live(this, shared, sessionPath, fd, rawFd, geometries, restoreToken is not null,
                topologySignature ?? MonitorsSignature(X11Capturer.EnumerateMonitorsViaRandR()));

            var session = connection.CreateProxy<IPortalSession>(ScreenCastPortal.Service, sessionPath);
            live.ClosedWatch = await session.WatchClosedAsync(
                _ => live.MarkDead("the portal closed the session"),
                ex => live.MarkDead("session watch failed: " + ex.Message)).ConfigureAwait(false);

            if (_startAllStreams)
            {
                await StartHdrReaderAsync(live, portal, sessionPath).ConfigureAwait(false);
                live.ProbeHdrNodes();
                foreach (var g in geometries) live.GetStream(g.NodeId);
                foreach (var g in geometries) live.GetStream(g.NodeId)?.WaitForFirstSample(TimeSpan.FromSeconds(1));
            }

            watch.Stop();
            FileLog.Write(
                $"RoeSnip: screencast {_purpose} session open in {watch.ElapsedMilliseconds} ms " +
                $"(portal v{version}, cursor_mode {cursorMode}, {(restoreToken is not null ? "restored from token" : "share dialog")}, " +
                $"token {(string.IsNullOrEmpty(newToken) ? "not returned" : "saved")}): " +
                string.Join(", ", geometries.Select(g => $"node {g.NodeId} at {g.X},{g.Y} {g.Width}x{g.Height}")));
            if (Interlocked.Exchange(ref s_indicatorLogged, 1) == 0)
            {
                FileLog.Write("RoeSnip: screencast open - GNOME shows its screen-sharing indicator in the top bar while it stays open");
            }
            live.TouchAll(IdleAfterOpen); // streams run while the cursor check looks, then idle
            if (cursorMode == ScreenCastPortal.CursorMetadata)
            {
                // Its own remote (no dialog on a started session): a dup of the gst fd shares the
                // socket, and two PipeWire cores on one socket break each other (seen live: the gst
                // streams' nodes vanished and the cursor streams failed "Buffer allocation failed").
                CloseSafeHandle? cursorFd = null;
                try
                {
                    cursorFd = await portal.OpenPipeWireRemoteAsync(sessionPath, new Dictionary<string, object>()).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    FileLog.Write($"RoeSnip: screencast cursor metadata: unavailable (second OpenPipeWireRemote failed: {ex.Message})");
                }
                if (cursorFd is not null)
                {
                    using (cursorFd) // the reader connects on its own dup
                    {
                        StartCursorMetadata(live, cursorFd.DangerousGetHandle().ToInt32(), geometries);
                    }
                }
            }
            else
            {
                FileLog.Write($"RoeSnip: screencast cursor metadata: unavailable (portal cursor modes {cursorModes}, no METADATA)");
            }
            return live;
        }
        catch
        {
            if (live is not null)
            {
                live.Dispose(); // owns the fd and the connection
            }
            else
            {
                fd?.Dispose();
                if (openedSession is { } path)
                {
                    // The shared connection outlives this attempt, so the session is closed by hand.
                    try { await connection.CreateProxy<IPortalSession>(ScreenCastPortal.Service, path).CloseAsync().ConfigureAwait(false); }
                    catch { /* already gone */ }
                }
            }
            throw;
        }
    }

    /// <summary>Creates the HDR reader (EGL + libpipewire on its own remote of this session, so no
    /// second dialog) when a monitor is in HDR mode. Any failure leaves every node on the 8-bit
    /// pipeline; one log line says why.</summary>
    private static async Task StartHdrReaderAsync(Live live, IPortalScreenCast portal, ObjectPath sessionPath)
    {
        if (!HdrDisplayProbe.ShouldTry(out string note))
        {
            FileLog.Write($"RoeSnip: HDR screencast capture off ({note})");
            return;
        }
        CloseSafeHandle? hdrFd = null;
        try
        {
            hdrFd = await portal.OpenPipeWireRemoteAsync(sessionPath, new Dictionary<string, object>()).ConfigureAwait(false);
            using (hdrFd)
            {
                live.Hdr = PipeWireHdrReader.TryStart(hdrFd.DangerousGetHandle().ToInt32(), out string why);
                if (live.Hdr is null) FileLog.Write($"RoeSnip: HDR screencast capture unavailable ({why}); frames stay 8-bit");
                else FileLog.Write($"RoeSnip: HDR screencast capture enabled ({note})");
            }
        }
        catch (Exception ex)
        {
            hdrFd?.Dispose();
            FileLog.Write($"RoeSnip: HDR screencast capture unavailable ({ex.GetType().Name}: {ex.Message}); frames stay 8-bit");
        }
    }

    /// <summary>Starts the cursor reader and, after <see cref="CursorMetaCheckAfter"/>, logs whether
    /// the nodes carry cursor metadata (a reader that saw none is dropped).</summary>
    private static void StartCursorMetadata(Live live, int rawFd, IReadOnlyList<ScreenCastStreamGeometry> geometries)
    {
        PipeWireCursorReader? reader;
        string why;
        try
        {
            reader = PipeWireCursorReader.TryStart(rawFd, geometries.Select(g => g.NodeId), out why);
        }
        catch (Exception ex)
        {
            reader = null;
            why = $"{ex.GetType().Name}: {ex.Message}";
        }
        if (reader is null)
        {
            FileLog.Write($"RoeSnip: screencast cursor metadata: unavailable ({why})");
            return;
        }
        live.Cursor = reader;
        _ = Task.Run(async () =>
        {
            await Task.Delay(CursorMetaCheckAfter).ConfigureAwait(false);
            if (live.IsDead || !ReferenceEquals(live.Cursor, reader)) return;
            if (reader.AnyMetaSeen)
            {
                FileLog.Write($"RoeSnip: screencast cursor metadata: on ({reader.StatesText})");
                return;
            }
            FileLog.Write($"RoeSnip: screencast cursor metadata: unavailable (no cursor meta within {CursorMetaCheckAfter.TotalSeconds:F0} s: {reader.StatesText})");
            live.Cursor = null;
            reader.Dispose();
        });
    }

    public static string MonitorsSignature(IReadOnlyList<MonitorInfo> monitors)
        => string.Join(";", monitors.Select(m => $"{m.DeviceName}@{m.BoundsPx.Left},{m.BoundsPx.Top},{m.BoundsPx.Width}x{m.BoundsPx.Height}"));

    /// <summary>One open portal session on the shared portal connection (closed explicitly on
    /// Dispose, dead when that connection drops), the PipeWire fd and the per-stream pipelines.</summary>
    internal sealed class Live : IDisposable
    {
        private readonly ScreenCastSession _owner;
        private readonly PortalConnection _shared;
        private readonly ObjectPath _sessionPath;
        private readonly CloseSafeHandle _fd;
        private readonly int _rawFd;
        private readonly Dictionary<uint, IScreenCastStream> _streams = new();
        private readonly HashSet<uint> _sdrNodes = new();   // nodes that did not offer HDR frames
        private static readonly TimeSpan HdrProbeWait = TimeSpan.FromMilliseconds(1500);
        private readonly object _gate = new();
        private volatile string? _deadReason;
        private int _disposed;
        private readonly Dictionary<string, IReadOnlyDictionary<int, ScreenCastStreamGeometry>> _mappingCache = new();

        public IReadOnlyList<ScreenCastStreamGeometry> Geometries { get; }
        public bool RestoredFromToken { get; }
        public string TopologySignature { get; }
        public IDisposable? ClosedWatch { get; set; }

        /// <summary>Cursor metadata of the nodes; null when the session has none.</summary>
        public volatile PipeWireCursorReader? Cursor;

        /// <summary>The half-float DMA-BUF consumer of the HDR nodes; null when none can be used.</summary>
        public volatile PipeWireHdrReader? Hdr;
        public bool IsDead => _deadReason is not null;
        public event Action<Live, string>? Died;

        // Idle streams: mutter keeps producing (and copying) a frame per damage for every running node,
        // ~60-80% of a gnome-shell core with three HDR monitors, so a node streams only while wanted.
        private readonly StreamActivity _activity = new();
        private readonly object _activityGate = new(); // guards _activity and the timer flag only
        private readonly System.Collections.Concurrent.ConcurrentDictionary<uint, object> _nodeLocks = new();
        private Timer? _idleTimer;
        private bool _idleTimerArmed;
        private static readonly TimeSpan IdleCheck = TimeSpan.FromMilliseconds(500);

        private static long NowMs => Stopwatch.GetTimestamp() * 1000 / Stopwatch.Frequency;

        /// <summary>Wants the node's streams (video and cursor) running for at least
        /// <paramref name="linger"/>. Returns the frame sequence to wait above for a fresh frame when
        /// the node was idle (<see cref="IScreenCastStream.WaitForFrameAfter"/>), else -1: a running
        /// stream's newest frame is current.</summary>
        public long Wake(uint nodeId, TimeSpan linger)
        {
            // One lock per node: waking three monitors at once must not queue their (slow) stream restarts.
            lock (NodeLock(nodeId))
            {
                long baseline = -1;
                bool wasIdle;
                lock (_activityGate) wasIdle = _activity.Touch(nodeId, NowMs, (long)linger.TotalMilliseconds);
                if (wasIdle) baseline = Apply(nodeId, true);
                ArmIdleTimer();
                return baseline;
            }
        }

        private object NodeLock(uint nodeId) => _nodeLocks.GetOrAdd(nodeId, _ => new object());

        /// <summary>Wakes every node (the pointer can be on any monitor). True when one was idle.</summary>
        public bool WakeAll(TimeSpan linger)
        {
            bool woke = false;
            foreach (var g in Geometries) woke |= Wake(g.NodeId, linger) >= 0;
            return woke;
        }

        /// <summary>The same as <see cref="WakeAll"/> for the open: nothing idles before the deadline.</summary>
        public void TouchAll(TimeSpan linger)
        {
            foreach (var g in Geometries) Wake(g.NodeId, linger);
        }

        /// <summary>Keeps the node streaming until the returned lease is disposed (a recording), then for
        /// <see cref="HoldLinger"/>. <paramref name="baseline"/> is as for <see cref="Wake"/>.</summary>
        public IDisposable Hold(uint nodeId, out long baseline)
        {
            lock (NodeLock(nodeId))
            {
                baseline = -1;
                bool wasIdle;
                lock (_activityGate) wasIdle = _activity.Hold(nodeId, NowMs);
                if (wasIdle) baseline = Apply(nodeId, true);
                ArmIdleTimer();
            }
            return new Lease(this, nodeId);
        }

        private static readonly TimeSpan HoldLinger = TimeSpan.FromSeconds(2);

        private sealed class Lease : IDisposable
        {
            private readonly Live _live;
            private readonly uint _node;
            private int _done;
            public Lease(Live live, uint node) { _live = live; _node = node; }
            public void Dispose()
            {
                if (Interlocked.Exchange(ref _done, 1) != 0) return;
                lock (_live._activityGate) _live._activity.Release(_node, NowMs, (long)HoldLinger.TotalMilliseconds);
                _live.ArmIdleTimer();
            }
        }

        /// <summary>Starts or stops a node's streams. Returns the video stream's sequence from before.</summary>
        private long Apply(uint nodeId, bool active)
        {
            IScreenCastStream? stream;
            lock (_gate) _streams.TryGetValue(nodeId, out stream);
            long before = stream?.Sequence ?? 0;
            try { stream?.SetActive(active); }
            catch (Exception ex) { FileLog.Write($"RoeSnip: screencast node {nodeId} set_active({active}) failed: {ex.Message}"); }
            try { Cursor?.SetActive(nodeId, active); }
            catch (Exception ex) { FileLog.Write($"RoeSnip: screencast cursor node {nodeId} set_active({active}) failed: {ex.Message}"); }
            return before;
        }

        private void ArmIdleTimer()
        {
            lock (_activityGate)
            {
                if (_idleTimerArmed || _disposed != 0) return;
                _idleTimerArmed = true;
                if (_idleTimer is null) _idleTimer = new Timer(_ => IdleTick(), null, IdleCheck, Timeout.InfiniteTimeSpan);
                else _idleTimer.Change(IdleCheck, Timeout.InfiniteTimeSpan);
            }
        }

        private void IdleTick()
        {
            List<uint> active;
            lock (_activityGate)
            {
                _idleTimerArmed = false;
                if (_disposed != 0) return;
                active = _activity.ActiveIds();
            }
            foreach (uint id in active)
            {
                lock (NodeLock(id))
                {
                    bool idle;
                    lock (_activityGate) idle = _activity.TryIdle(id, NowMs);
                    if (idle) Apply(id, false);
                }
            }
            bool any;
            lock (_activityGate) any = _activity.AnyActive;
            if (any) ArmIdleTimer();
        }

        public Live(ScreenCastSession owner, PortalConnection shared, ObjectPath sessionPath, CloseSafeHandle fd,
            int rawFd, IReadOnlyList<ScreenCastStreamGeometry> geometries, bool restoredFromToken, string topologySignature)
        {
            _owner = owner;
            _shared = shared;
            shared.Lost += OnConnectionLost;
            _sessionPath = sessionPath;
            _fd = fd;
            _rawFd = rawFd;
            Geometries = geometries;
            RestoredFromToken = restoredFromToken;
            TopologySignature = topologySignature;
        }

        private void OnConnectionLost(string why) => MarkDead("the portal connection dropped: " + why);

        public void MarkDead(string reason)
        {
            if (_deadReason is not null || Volatile.Read(ref _disposed) != 0) return;
            _deadReason = reason;
            FileLog.Write($"RoeSnip: screencast {_owner._purpose} session lost: {reason}");
            Died?.Invoke(this, reason);
        }

        /// <summary>The stream mapped to <paramref name="monitor"/> under the current monitor list.</summary>
        public ScreenCastStreamGeometry? StreamFor(MonitorInfo monitor, IReadOnlyList<MonitorInfo> allMonitors)
        {
            string sig = MonitorsSignature(allMonitors);
            IReadOnlyDictionary<int, ScreenCastStreamGeometry> map;
            lock (_gate)
            {
                if (_mappingCache.TryGetValue(sig, out var cached))
                {
                    map = cached;
                }
                else
                {
                    map = ScreenCastStreamMapping.Map(Geometries, allMonitors);
                    _mappingCache[sig] = map;
                    FileLog.Write($"RoeSnip: screencast {_owner._purpose} stream mapping: " +
                        string.Join(", ", allMonitors.Select(m =>
                            map.TryGetValue(m.Index, out var g) ? $"{m.DeviceName} -> node {g.NodeId}" : $"{m.DeviceName} -> none")));
                }
            }
            return map.TryGetValue(monitor.Index, out var hit) ? hit : null;
        }

        /// <summary>The kept-open stream for a node, started on first use: the HDR consumer when the
        /// node offers half-float DMA-BUF frames, else the 8-bit pipeline. Null if it cannot start
        /// (the session is then marked dead).</summary>
        public IScreenCastStream? GetStream(uint nodeId)
        {
            IScreenCastStream? before;
            lock (_gate) _streams.TryGetValue(nodeId, out before);
            var stream = GetStreamCore(nodeId);
            if (stream is not null && !ReferenceEquals(stream, before)) NoteRunning(nodeId); // outside _gate: Wake takes the locks the other way round
            return stream;
        }

        private IScreenCastStream? GetStreamCore(uint nodeId)
        {
            lock (_gate)
            {
                if (IsDead || _disposed != 0) return null;
                if (_streams.TryGetValue(nodeId, out var existing))
                {
                    if (!existing.IsDead) return existing;
                    string why = existing.DeadReason ?? "stream died";
                    _streams.Remove(nodeId);
                    existing.Dispose();
                    if (existing is HdrScreenCastStream)
                    {
                        // An HDR stream that dies (import failures) costs only its HDR: the node goes back to 8-bit.
                        FileLog.Write($"RoeSnip: HDR stream node {nodeId} unusable ({why}); using the 8-bit stream");
                        _sdrNodes.Add(nodeId);
                    }
                    else
                    {
                        ThreadPool.QueueUserWorkItem(_ => MarkDead(why));
                        return null;
                    }
                }
                var geometry = Geometries.FirstOrDefault(g => g.NodeId == nodeId);
                if (Hdr is { } hdr && !_sdrNodes.Contains(nodeId))
                {
                    var hdrStream = hdr.AddNode(geometry, HdrProbeWait, out string hdrWhy);
                    if (hdrStream is not null)
                    {
                        _streams[nodeId] = hdrStream;
                        return hdrStream;
                    }
                    _sdrNodes.Add(nodeId);
                    FileLog.Write($"RoeSnip: screencast node {nodeId} stays 8-bit ({hdrWhy})");
                }
                try
                {
                    var stream = new ScreenCastStream(_rawFd, geometry);
                    _streams[nodeId] = stream;
                    return stream;
                }
                catch (Exception ex)
                {
                    ThreadPool.QueueUserWorkItem(_ => MarkDead("pipeline start failed: " + ex.Message));
                    return null;
                }
            }
        }

        /// <summary>A stream that was just built is running whatever the node's idle state said.</summary>
        private void NoteRunning(uint nodeId)
        {
            lock (_activityGate)
            {
                _activity.Touch(nodeId, NowMs, (long)HoldLinger.TotalMilliseconds);
                ArmIdleTimer();
            }
        }

        /// <summary>Probes every node for HDR frames at once (an SDR node answers within a moment, but a
        /// silent one costs the full wait: do not pay it per monitor).</summary>
        public void ProbeHdrNodes()
        {
            var hdr = Hdr;
            if (hdr is null) return;
            Parallel.ForEach(Geometries, g =>
            {
                var s = hdr.AddNode(g, HdrProbeWait, out string why);
                lock (_gate)
                {
                    if (s is not null && !IsDead && _disposed == 0 && !_streams.ContainsKey(g.NodeId))
                    {
                        _streams[g.NodeId] = s;
                        return;
                    }
                    _sdrNodes.Add(g.NodeId);
                }
                if (s is not null) s.Dispose();
                else FileLog.Write($"RoeSnip: screencast node {g.NodeId} stays 8-bit ({why})");
            });
        }

        /// <summary>Takes <paramref name="nodeId"/> off HDR for good (the HDR frame could not be read):
        /// the next <see cref="GetStream"/> builds the 8-bit pipeline.</summary>
        public void DemoteHdr(uint nodeId, string why)
        {
            lock (_gate)
            {
                if (_streams.TryGetValue(nodeId, out var s) && s is HdrScreenCastStream)
                {
                    _streams.Remove(nodeId);
                    s.Dispose();
                    _sdrNodes.Add(nodeId);
                    FileLog.Write($"RoeSnip: HDR stream node {nodeId} given up ({why}); using the 8-bit stream");
                }
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            lock (_activityGate) { _idleTimer?.Dispose(); }
            var cursor = Cursor;
            Cursor = null;
            try { cursor?.Dispose(); } catch { }
            lock (_gate)
            {
                foreach (var s in _streams.Values) s.Dispose();
                _streams.Clear();
            }
            var hdr = Hdr;
            Hdr = null;
            try { hdr?.Dispose(); } catch { }
            _shared.Lost -= OnConnectionLost;
            try { ClosedWatch?.Dispose(); } catch { }
            try
            {
                _shared.Connection.CreateProxy<IPortalSession>(ScreenCastPortal.Service, _sessionPath)
                    .CloseAsync().Wait(TimeSpan.FromSeconds(2));
            }
            catch { /* the session may already be gone */ }
            try { _fd.Dispose(); } catch { }
        }
    }
}

/// <summary>The desktop cannot screencast at all (no portal, no GStreamer pipewiresrc, no bus).</summary>
internal sealed class ScreenCastUnsupportedException : InvalidOperationException
{
    public ScreenCastUnsupportedException(string message) : base(message) { }
}
