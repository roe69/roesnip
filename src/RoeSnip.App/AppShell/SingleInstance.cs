using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Threading;
using System.Threading.Tasks;
using RoeSnip.Core.Diagnostics;
using RoeSnip.Core.Updates;

namespace RoeSnip.App.AppShell;

/// <summary>The single byte sent over the single-instance pipe — extends the WPF app's fixed
/// single meaning (capture) to a small enum matching the two bare CLI verbs (PLAN-XPLAT.md §3.2)
/// plus <see cref="Exit"/> (item 13a), which implements replace-on-run: a plain (no-flag) launch
/// asks the resident instance to exit instead of capturing, mirroring the WPF app's TrayApp.cs:
/// 46-133 semantics ("just run the exe" always means "run the latest build", never "poke whatever
/// is already running").</summary>
public enum InstanceSignal : byte
{
    None = 0,
    TriggerCapture = 1,
    TriggerSettings = 2,
    Exit = 3,
}

/// <summary>Single-instance enforcement: named mutex + named pipe, ported from the WPF app's
/// TrayApp (SignalExistingInstance/ListenForSignalAsync, PLAN.md §3.3) per PLAN-XPLAT.md §3.2.
/// .NET named pipes are Unix domain sockets on macOS/Linux and named Mutex works cross-platform
/// (PLAN-XPLAT.md section 5), so this code is OS-conditional only in the names (off Windows they carry
/// the user name and the mutex is Global\, see PipeName).
///
/// DELIBERATE deviation from §3.2's literal names: the plan reuses the frozen WPF app's exact
/// mutex/pipe names ("RoeSnip-SingleInstance"/"RoeSnip-SingleInstance-Capture"), but both apps run
/// side by side on the verification machine this cycle (PLAN-XPLAT.md §4's own A/B checklist), and
/// sharing names would make whichever app launches second silently signal the OTHER app's capture
/// flow and exit. Distinct names ("RoeSnip.App-...") keep the two products' instances independent;
/// flagged in the WP-X2 report for the integrator.</summary>
public sealed class SingleInstance : IDisposable
{
    // Off Windows a plain .NET mutex name is scoped to the Unix session (getsid), and a LaunchAgent,
    // `open -n`, GNOME autostart and a GNOME menu launch each land in a different one: Global\ plus
    // the user name makes it one lock per user. The pipe is a socket in the per-user TMPDIR (macOS)
    // or the shared /tmp (Linux), reachable from every session; the user name keeps users apart.
    private const string WindowsName = "RoeSnip.App-SingleInstance";

    // Builds before the per-user names: session-scoped mutex, this socket name.
    private const string LegacyUnixPipeName = "RoeSnip.App-SingleInstance";

    private static readonly string PipeName = OperatingSystem.IsWindows()
        ? WindowsName
        : WindowsName + "-" + SafeUserName();

    private static string MutexName => OperatingSystem.IsWindows()
        ? @"Global\" + WindowsName
        : @"Global\" + PipeName;

    // getpwuid(geteuid()) name; anything but [A-Za-z0-9._-] dropped so it stays a valid file name.
    private static string SafeUserName()
    {
        string name;
        try { name = Environment.UserName; }
        catch { name = string.Empty; }

        var safe = new System.Text.StringBuilder(name.Length);
        foreach (char c in name)
        {
            if (c is (>= 'A' and <= 'Z') or (>= 'a' and <= 'z') or (>= '0' and <= '9') or '.' or '_' or '-')
            {
                safe.Append(c);
            }
        }
        return safe.Length > 0 ? safe.ToString() : "user";
    }

    private readonly Mutex _mutex;

    private SingleInstance(Mutex mutex) => _mutex = mutex;

    private static SingleInstance Acquired(Mutex mutex)
    {
        RetireLegacyResident();
        return new SingleInstance(mutex);
    }

    /// <summary>Off Windows: a resident from a build that still used the session-scoped lock holds
    /// none of ours (the update that started this process is one), so ask it to exit over its old
    /// socket. Only tried when that socket file exists, so a normal start pays nothing.</summary>
    private static void RetireLegacyResident()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        // .NET's Unix pipe socket path: <temp>/CoreFxPipe_<name>.
        string socket = Path.Combine(Path.GetTempPath(), "CoreFxPipe_" + LegacyUnixPipeName);
        try
        {
            if (!File.Exists(socket))
            {
                return;
            }

            using var client = new NamedPipeClientStream(".", LegacyUnixPipeName, PipeDirection.Out);
            client.Connect(500);
            client.WriteByte((byte)InstanceSignal.Exit);
            client.Flush();
            FileLog.Write("RoeSnip: asked a resident from an older build to exit");
        }
        catch (Exception ex)
        {
            // A stale socket (connect spins until the timeout on it): remove it so the next start
            // skips the probe.
            FileLog.Write($"RoeSnip: no older-build resident answered ({ex.Message}), removing its socket");
            try { File.Delete(socket); }
            catch (Exception deleteEx) when (deleteEx is IOException or UnauthorizedAccessException) { }
        }
    }

    /// <summary>Returns the held single-instance lock, or null if another instance already holds
    /// it (in which case the caller should signal that instance instead).</summary>
    public static SingleInstance? TryAcquire()
    {
        var mutex = new Mutex(initiallyOwned: true, MutexName, out bool createdNew);
        if (!createdNew)
        {
            mutex.Dispose();
            return null;
        }

        // Makes a verb run (`capture`) that missed the resident and became one visible in the log.
        FileLog.Write($"RoeSnip: no resident found (lock {MutexName} free), becoming the resident");
        return Acquired(mutex);
    }

    /// <summary>Replace-on-run takeover (item 13a, WPF TrayApp.cs:75-92 semantics): another
    /// instance already holds the single-instance lock. Ask it to <see cref="InstanceSignal.Exit"/>,
    /// then wait up to <paramref name="signalWaitTimeout"/> for it to release the mutex so this
    /// process can take over. If it does not go quietly (hung, mid-capture, or the pipe never
    /// landed), force-terminate it as a last resort and wait up to
    /// <paramref name="killWaitTimeout"/> more — see <see cref="KillOtherInstances"/> for the
    /// CRITICAL path-based discrimination that keeps this from ever touching the WPF app's own
    /// resident. Returns the acquired lock, or null if takeover could not be completed (the caller
    /// should leave the existing instance in place and exit).</summary>
    public static SingleInstance? TryTakeOver(TimeSpan signalWaitTimeout, TimeSpan killWaitTimeout)
    {
        var mutex = new Mutex(initiallyOwned: true, MutexName, out bool createdNew);
        if (createdNew)
        {
            // Race: the other instance exited between the caller's failed TryAcquire and this call.
            return Acquired(mutex);
        }

        SignalExistingInstance(InstanceSignal.Exit);
        if (TryAcquireMutex(mutex, signalWaitTimeout))
        {
            return Acquired(mutex);
        }

        KillOtherInstances();
        if (TryAcquireMutex(mutex, killWaitTimeout))
        {
            return Acquired(mutex);
        }

        mutex.Dispose();
        return null;
    }

    /// <summary>Waits to own the single-instance mutex. AbandonedMutexException (the previous
    /// owner died without releasing) still hands us ownership, so it counts as success.</summary>
    private static bool TryAcquireMutex(Mutex mutex, TimeSpan timeout)
    {
        try { return mutex.WaitOne(timeout); }
        catch (AbandonedMutexException) { return true; }
    }

    /// <summary>Force-terminates every OTHER process that is genuinely THIS app's own exe (last
    /// resort when a running instance will not exit on request).
    ///
    /// CRITICAL: RoeSnip.App's AssemblyName is also "RoeSnip" (matching the WPF app's), so
    /// <see cref="Process.GetProcessesByName"/>("RoeSnip") returns BOTH products' processes on a
    /// machine running both. Killing by NAME alone would murder the user's separate WPF resident —
    /// this discriminates by each candidate's own MainModule executable path instead, only killing
    /// a process whose path matches THIS app's own path (a dev/portable copy taking over another
    /// dev/portable copy) or its install path (<see cref="UpdateManager.InstalledExePath"/> — an
    /// installed resident). A candidate whose path matches neither (the WPF app, or literally any
    /// other same-named binary) is left untouched. Best-effort per process; never throws out.</summary>
    private static void KillOtherInstances()
    {
        var ownPaths = OwnExecutablePaths();
        if (ownPaths.Count == 0)
        {
            // Could not resolve even our own path — refuse to guess rather than risk killing the
            // wrong process.
            return;
        }

        int self = Environment.ProcessId;
        foreach (var p in Process.GetProcessesByName("RoeSnip"))
        {
            try
            {
                if (p.Id == self)
                {
                    continue;
                }

                // Linux AppImage: compare the APPIMAGE file, not the per-run /tmp/.mount_* exe.
                string? path = UpdateManager.ProcessIdentityPath(p, TryGetMainModulePath(p));
                if (path is null || !ownPaths.Contains(Comparable(path)))
                {
                    continue; // not one of THIS app's own exe paths — e.g. the WPF resident.
                }

                p.Kill();
                p.WaitForExit(2000);
            }
            catch (Exception ex)
            {
                FileLog.Write($"RoeSnip: could not terminate a stale instance (pid {p.Id}): {ex.Message}");
            }
            finally
            {
                p.Dispose();
            }
        }
    }

    private static HashSet<string> OwnExecutablePaths()
    {
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        AddPath(paths, UpdateManager.CurrentLaunchPath);
        AddPath(paths, Environment.ProcessPath);
        foreach (string installed in UpdateManager.InstalledIdentityPaths())
        {
            AddPath(paths, installed);
        }
        return paths;
    }

    private static void AddPath(HashSet<string> paths, string? path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return;
        }
        try { paths.Add(Comparable(path)); }
        catch (Exception ex) when (ex is IOException or ArgumentException or NotSupportedException)
        {
            // Unparseable path — ignore rather than throw out of a best-effort discrimination check.
        }
    }

    // Off Windows symlinks resolved: $APPIMAGE in another process's environ is always the real
    // path, while our install paths come from $HOME / $XDG_DATA_HOME as written.
    private static string Comparable(string path) =>
        OperatingSystem.IsWindows() ? Path.GetFullPath(path) : RealPath.ResolveOrFull(path);

    private static string? TryGetMainModulePath(Process p)
    {
        try { return p.MainModule?.FileName; }
        catch { return null; }
    }

    /// <summary>Sends one signal byte to the resident instance's pipe. Returns false (and logs to
    /// stderr) if the resident instance could not be reached.</summary>
    public static bool SignalExistingInstance(InstanceSignal signal)
    {
        try
        {
            var watch = Stopwatch.StartNew();
            using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
            client.Connect(2000);
            client.WriteByte((byte)signal);
            client.Flush();
            // The `capture` verb's cost is mostly this process starting; both numbers are logged.
            double sinceStart = (DateTime.Now - Process.GetCurrentProcess().StartTime).TotalMilliseconds;
            FileLog.Write($"RoeSnip: signalled the resident ({signal}) in {watch.ElapsedMilliseconds} ms, {sinceStart:F0} ms after this process started");
            return true;
        }
        catch (Exception ex)
        {
            FileLog.Write(
                $"RoeSnip: another instance appears to be running, but signalling it failed: {ex.Message}");
            return false;
        }
    }

    /// <summary>The resident instance's listener loop — one pipe connection per signal, exactly
    /// like the WPF app's ListenForSignalAsync. Static: the listener only needs the pipe, not the
    /// mutex (which the resident's RunResident frame holds). <paramref name="onSignal"/> is
    /// invoked on a thread pool thread; the caller is responsible for marshalling to its UI
    /// thread.</summary>
    public static async Task ListenForSignalsAsync(Action<InstanceSignal> onSignal, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                using var server = new NamedPipeServerStream(
                    PipeName, PipeDirection.In, maxNumberOfServerInstances: 1,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                await server.WaitForConnectionAsync(token).ConfigureAwait(false);
                int raw = server.ReadByte();
                if (raw is (int)InstanceSignal.TriggerCapture or (int)InstanceSignal.TriggerSettings
                    or (int)InstanceSignal.Exit)
                {
                    FileLog.Write($"RoeSnip: single-instance signal {(InstanceSignal)raw} received");
                    onSignal((InstanceSignal)raw);
                }
                else if (raw >= 0)
                {
                    FileLog.Write($"RoeSnip: ignoring unknown single-instance signal {raw}.");
                }
            }
            catch (OperationCanceledException)
            {
                // shutting down
            }
            catch (Exception ex) when (!token.IsCancellationRequested)
            {
                FileLog.Write($"RoeSnip: single-instance pipe listener error: {ex.Message}");
                await Task.Delay(1000, token).ContinueWith(_ => { }).ConfigureAwait(false);
            }
        }
    }

    public void Dispose()
    {
        try
        {
            _mutex.ReleaseMutex();
        }
        catch
        {
            // Released from a different thread than the acquirer, or already released — the mutex
            // is destroyed with the process either way; disposal below is what matters.
        }
        _mutex.Dispose();
    }
}
