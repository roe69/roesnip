using System.Collections.Concurrent;
using System.Diagnostics;
using RoeSnip.Core.Capture;
using RoeSnip.Core.Diagnostics;

namespace RoeSnip.Platform.MacOS;

/// <summary>The serve helper is not running and could not be started; callers use the one-shot
/// helper instead.</summary>
public sealed class ScksnapUnavailableException : Exception
{
    public ScksnapUnavailableException(string message) : base(message) { }
}

/// <summary>Process-wide client for <c>scksnap serve</c> (protocol in <see cref="ScksnapProtocol"/>):
/// started on first use (the tray warmup's capture), restarted after a crash, requests matched to
/// replies by id with a timeout each. Crash loops back off to the one-shot helper for a minute.
/// Helper stderr lines go to the RoeSnip log.</summary>
public sealed class ScksnapServer
{
    public static ScksnapServer Shared { get; } = new(ScksnapHelperClient.LocateHelper());

    private static readonly TimeSpan PingTimeout = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan CrashWindow = TimeSpan.FromSeconds(60);
    private const int MaxStartsPerWindow = 3;

    private readonly string _helperPath;
    private readonly object _startGate = new();
    private readonly object _writeGate = new();
    private readonly ConcurrentDictionary<uint, Pending> _pending = new();
    private readonly Queue<DateTime> _starts = new();

    private Process? _process;
    private Stream? _stdin;
    private int _generation;
    private int _nextId;
    private int _consecutiveTimeouts;
    private DateTime _retryAfterUtc = DateTime.MinValue;
    private bool _missingLogged;

    /// <summary>Raised (reader thread) when the helper reports that the active recording failed.</summary>
    public event Action<string>? RecordingFailed;

    /// <summary>Raised (reader thread) when a running helper exits; an active recording is gone.</summary>
    public event Action<string>? Exited;

    /// <summary>True while an MP4 take is recording in the helper; set by the encoder. A wedged
    /// helper is not killed while this is set.</summary>
    public volatile bool RecordingActive;

    private sealed record Pending(byte Op, TaskCompletionSource<ScksnapMessage> Reply);

    public ScksnapServer(string helperPath)
    {
        _helperPath = helperPath;
        AppDomain.CurrentDomain.ProcessExit += (_, _) => Quit();
    }

    /// <summary>Starts the helper when it is not running. False when it cannot run (missing,
    /// crash-looping, no ping reply); the reason is logged.</summary>
    public bool EnsureRunning()
    {
        lock (_startGate)
        {
            if (_process is { HasExited: false }) return true;
            if (DateTime.UtcNow < _retryAfterUtc) return false;
            if (!File.Exists(_helperPath))
            {
                if (!_missingLogged)
                {
                    FileLog.Write($"RoeSnip: scksnap helper not found at \"{_helperPath}\"; serve mode unavailable");
                    _missingLogged = true;
                }
                _retryAfterUtc = DateTime.MaxValue;
                return false;
            }

            var now = DateTime.UtcNow;
            while (_starts.Count > 0 && now - _starts.Peek() > CrashWindow) _starts.Dequeue();
            if (_starts.Count >= MaxStartsPerWindow)
            {
                FileLog.Write("RoeSnip: scksnap serve keeps exiting; using the one-shot helper for 60 s");
                _starts.Clear();
                _retryAfterUtc = now + CrashWindow;
                return false;
            }
            _starts.Enqueue(now);

            var watch = Stopwatch.StartNew();
            var psi = new ProcessStartInfo
            {
                FileName = _helperPath,
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add("serve");
            var process = new Process { StartInfo = psi };
            try
            {
                if (!process.Start()) throw new InvalidOperationException("Process.Start returned false");
            }
            catch (Exception ex)
            {
                FileLog.Write($"RoeSnip: scksnap serve failed to start: {ex.Message}");
                process.Dispose();
                _retryAfterUtc = now + CrashWindow;
                return false;
            }

            int generation = Interlocked.Increment(ref _generation);
            _process = process;
            _stdin = process.StandardInput.BaseStream;
            _consecutiveTimeouts = 0;
            var stdout = process.StandardOutput.BaseStream;
            var stderr = process.StandardError;
            new Thread(() => ReadLoop(process, stdout, generation)) { IsBackground = true, Name = "RoeSnip-Scksnap-Reader" }.Start();
            new Thread(() => StderrLoop(stderr)) { IsBackground = true, Name = "RoeSnip-Scksnap-Stderr" }.Start();

            try
            {
                var reply = SendCore(ScksnapProtocol.OpPing, Array.Empty<byte>(), PingTimeout);
                if (reply.Status != ScksnapProtocol.StatusOk) throw new IOException(reply.Text);
            }
            catch (Exception ex)
            {
                FileLog.Write($"RoeSnip: scksnap serve did not answer its ping ({ex.Message}); using the one-shot helper");
                Kill(process);
                _process = null;
                _retryAfterUtc = DateTime.UtcNow + CrashWindow;
                return false;
            }
            FileLog.Write($"RoeSnip: scksnap serve started in {watch.ElapsedMilliseconds} ms (pid {process.Id})");
            return true;
        }
    }

    /// <summary>One request. Throws <see cref="ScksnapUnavailableException"/> when the helper cannot
    /// run, <see cref="TimeoutException"/> when no reply came in time, <see cref="IOException"/> when
    /// the helper exited mid-request. A non-ok status is returned, not thrown.</summary>
    public ScksnapMessage Send(byte op, byte[] payload, TimeSpan timeout)
    {
        if (!EnsureRunning()) throw new ScksnapUnavailableException("scksnap serve is unavailable");
        return SendCore(op, payload, timeout);
    }

    private ScksnapMessage SendCore(byte op, byte[] payload, TimeSpan timeout)
    {
        uint id = (uint)Interlocked.Increment(ref _nextId);
        if (id == ScksnapProtocol.EventId) id = (uint)Interlocked.Increment(ref _nextId);
        var pending = new Pending(op, new TaskCompletionSource<ScksnapMessage>(TaskCreationOptions.RunContinuationsAsynchronously));
        _pending[id] = pending;
        try
        {
            byte[] request = ScksnapProtocol.EncodeRequest(op, id, payload);
            lock (_writeGate)
            {
                var stdin = _stdin ?? throw new IOException("scksnap serve is not running");
                stdin.Write(request);
                stdin.Flush();
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
            _pending.TryRemove(id, out _);
            throw new IOException($"scksnap serve request failed: {ex.Message}", ex);
        }

        if (!pending.Reply.Task.Wait(timeout))
        {
            _pending.TryRemove(id, out _);
            OnTimeout(op, timeout);
            throw new TimeoutException($"scksnap serve op {op} timed out after {timeout.TotalSeconds:0.#} s");
        }
        Interlocked.Exchange(ref _consecutiveTimeouts, 0);
        return pending.Reply.Task.GetAwaiter().GetResult();
    }

    private void OnTimeout(byte op, TimeSpan timeout)
    {
        FileLog.Write($"RoeSnip: scksnap serve op {op} timed out after {timeout.TotalSeconds:0.#} s");
        if (Interlocked.Increment(ref _consecutiveTimeouts) >= 2 && !RecordingActive)
        {
            FileLog.Write("RoeSnip: scksnap serve looks wedged; restarting it on next use");
            Process? process;
            lock (_startGate) process = _process;
            if (process is not null) Kill(process);
        }
    }

    private void ReadLoop(Process process, Stream stdout, int generation)
    {
        string reason = "stdout closed";
        try
        {
            while (true)
            {
                var message = ScksnapProtocol.ReadMessage(stdout, id =>
                    _pending.TryGetValue(id, out var p)
                    && (p.Op == ScksnapProtocol.OpSnapshot || p.Op == ScksnapProtocol.OpSnapshotRegion));
                if (message is null) break;
                if (message.Id == ScksnapProtocol.EventId)
                {
                    HandleEvent(message.Payload);
                    continue;
                }
                if (_pending.TryRemove(message.Id, out var pending))
                {
                    pending.Reply.TrySetResult(message);
                }
                // else: a reply to a request that already timed out; dropped.
            }
        }
        catch (Exception ex)
        {
            reason = ex.Message;
        }

        // Fail waiters first: EnsureRunning may hold _startGate while waiting on its ping.
        foreach (var (id, pending) in _pending.ToArray())
        {
            if (_pending.TryRemove(id, out _))
            {
                pending.Reply.TrySetException(new IOException($"scksnap serve exited ({reason})"));
            }
        }

        int exitCode = -1;
        try
        {
            if (process.WaitForExit(2000)) exitCode = process.ExitCode;
            else Kill(process);
        }
        catch (InvalidOperationException) { }

        lock (_startGate)
        {
            if (Volatile.Read(ref _generation) == generation && ReferenceEquals(_process, process))
            {
                _process = null;
                _stdin = null;
            }
        }
        string summary = $"scksnap serve exited (code {exitCode}, {reason})";
        FileLog.Write($"RoeSnip: {summary}");
        process.Dispose();
        Exited?.Invoke(summary);
    }

    private void HandleEvent(byte[] payload)
    {
        if (payload.Length == 0) return;
        string message = System.Text.Encoding.UTF8.GetString(payload, 1, payload.Length - 1);
        if (payload[0] == ScksnapProtocol.EventRecordingFailed)
        {
            RecordingFailed?.Invoke(message);
        }
    }

    private static void StderrLoop(StreamReader stderr)
    {
        try
        {
            string? line;
            while ((line = stderr.ReadLine()) is not null)
            {
                if (line.Length > 0) FileLog.Write($"RoeSnip: {line}");
            }
        }
        catch (Exception) { /* helper gone */ }
    }

    private void Quit()
    {
        Process? process;
        lock (_startGate) process = _process;
        if (process is null) return;
        try
        {
            SendCore(ScksnapProtocol.OpQuit, Array.Empty<byte>(), TimeSpan.FromMilliseconds(500));
        }
        catch (Exception) { /* exiting anyway */ }
        try { _stdin?.Dispose(); } catch (Exception) { }
    }

    private static void Kill(Process process)
    {
        try { process.Kill(); } catch (Exception) { /* already gone */ }
    }

    // ---- typed requests ----

    /// <summary>The serve list reply, or null when serve is unavailable or failed (logged).</summary>
    public IReadOnlyList<ScksnapDisplay>? TryListDisplays()
    {
        try
        {
            var reply = Send(ScksnapProtocol.OpList, Array.Empty<byte>(), TimeSpan.FromSeconds(5));
            if (reply.Status == ScksnapProtocol.StatusOk) return ScksnapHelperClient.ParseDisplays(reply.Text);
            FileLog.Write($"RoeSnip: scksnap serve list failed: {reply.Text}");
        }
        catch (ScksnapUnavailableException) { }
        catch (Exception ex) when (ex is IOException or TimeoutException or CaptureException)
        {
            FileLog.Write($"RoeSnip: scksnap serve list failed: {ex.Message}");
        }
        return null;
    }

    /// <summary>Full-display frames for <paramref name="displayIds"/>, or null when serve is
    /// unavailable or failed (logged; the caller uses the one-shot helper). A display the helper
    /// could not capture is missing from the list. <paramref name="background"/>: never show the
    /// Screen Recording prompt (warmup).</summary>
    public IReadOnlyList<ScksnapFrame>? TrySnapshot(IReadOnlyList<uint> displayIds, bool background)
    {
        ScksnapMessage reply;
        try
        {
            reply = Send(ScksnapProtocol.OpSnapshot, ScksnapProtocol.EncodeSnapshot(background, displayIds),
                TimeSpan.FromSeconds(5));
        }
        catch (ScksnapUnavailableException)
        {
            return null;
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or InvalidDataException or CaptureException)
        {
            FileLog.Write($"RoeSnip: scksnap serve snapshot failed: {ex.Message}");
            return null;
        }
        ThrowIfTccDenied(reply);
        if (reply.Status != ScksnapProtocol.StatusOk)
        {
            FileLog.Write($"RoeSnip: scksnap serve snapshot failed (status {reply.Status}): {reply.Text}");
            return null;
        }
        return reply.Frames ?? Array.Empty<ScksnapFrame>();
    }

    /// <summary>A crop of one display's latest stream frame (GIF recording). Throws on failure.</summary>
    public ScksnapFrame SnapshotRegion(uint displayId, int x, int y, int width, int height)
    {
        var reply = Send(ScksnapProtocol.OpSnapshotRegion,
            ScksnapProtocol.EncodeRegion(displayId, x, y, width, height), TimeSpan.FromSeconds(2));
        ThrowIfTccDenied(reply);
        if (reply.Status != ScksnapProtocol.StatusOk || reply.Frames is not { Count: 1 } frames)
        {
            throw new CaptureException($"scksnap region capture failed (status {reply.Status}): {reply.Text}");
        }
        return frames[0];
    }

    public static void ThrowIfTccDenied(ScksnapMessage reply)
    {
        if (reply.Status == ScksnapProtocol.StatusTccDenied)
        {
            throw new ScreenRecordingPermissionDeniedException(reply.Text);
        }
    }
}
