using System.Diagnostics;
using RoeSnip.Core.Capture;
using RoeSnip.Core.Diagnostics;
using Tmds.DBus;

namespace RoeSnip.Platform.Linux;

/// <summary>A kept-open org.freedesktop.portal.ScreenCast session (MONITOR sources, multiple,
/// persist_mode=2) with one GStreamer pipeline per shared monitor. One per process:
/// <see cref="Snapshot"/> (cursor hidden, every stream started at open, kept for the process life),
/// used by captures and recordings alike so the share dialog appears once ever (owner rule: a
/// second session with its own token meant a second dialog). Its restore token is saved; a restore
/// that still shows a dialog means the token was rejected (GNOME then saves a new one).
///
/// Opening runs on a background task. Callers wait for it with a bound. The Screenshot portal is
/// used only when the desktop cannot screencast at all (<see cref="State"/> Unsupported) and never
/// on GNOME (shutter sound and flash), see <see cref="LinuxCapturePolicy"/>.</summary>
internal sealed class ScreenCastSession
{
    public static readonly ScreenCastSession Snapshot = new("snapshot", ScreenCastPortal.CursorHidden, startAllStreams: true, needsPermission: true);

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

            IDictionary<string, object> started;
            try
            {
                started = await ScreenCastPortal.RequestAsync(connection, sender, "Start",
                    token => portal.StartAsync(sessionPath, "", new Dictionary<string, object> { ["handle_token"] = token }),
                    DialogTimeout).ConfigureAwait(false);
            }
            catch (PortalResponseException)
            {
                _tokens.Save(null); // a denied or broken restore must not be replayed
                throw;
            }

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
        private readonly Dictionary<uint, ScreenCastStream> _streams = new();
        private readonly object _gate = new();
        private volatile string? _deadReason;
        private int _disposed;
        private readonly Dictionary<string, IReadOnlyDictionary<int, ScreenCastStreamGeometry>> _mappingCache = new();

        public IReadOnlyList<ScreenCastStreamGeometry> Geometries { get; }
        public bool RestoredFromToken { get; }
        public string TopologySignature { get; }
        public IDisposable? ClosedWatch { get; set; }
        public bool IsDead => _deadReason is not null;
        public event Action<Live, string>? Died;

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

        /// <summary>The running pipeline for a node, started on first use. Null if it cannot start
        /// (the session is then marked dead).</summary>
        public ScreenCastStream? GetStream(uint nodeId)
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
                    ThreadPool.QueueUserWorkItem(_ => MarkDead(why));
                    return null;
                }
                var geometry = Geometries.FirstOrDefault(g => g.NodeId == nodeId);
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

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            lock (_gate)
            {
                foreach (var s in _streams.Values) s.Dispose();
                _streams.Clear();
            }
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
