using System;
using System.IO.Pipes;
using System.Threading;
using System.Threading.Tasks;

namespace RoeSnip.App.AppShell;

/// <summary>The single byte sent over the single-instance pipe — extends the WPF app's fixed
/// single meaning (capture) to a small enum matching the two bare CLI verbs
/// (PLAN-XPLAT.md §3.2).</summary>
public enum InstanceSignal : byte
{
    None = 0,
    TriggerCapture = 1,
    TriggerSettings = 2,
}

/// <summary>Single-instance enforcement: named mutex + named pipe, ported from the WPF app's
/// TrayApp (SignalExistingInstance/ListenForSignalAsync, PLAN.md §3.3) per PLAN-XPLAT.md §3.2.
/// .NET named pipes are Unix domain sockets on macOS/Linux and named Mutex works cross-platform
/// (PLAN-XPLAT.md §5), so this code is OS-conditional only in the mutex name (the Windows
/// "Global\" Terminal-Services prefix is meaningless elsewhere).
///
/// DELIBERATE deviation from §3.2's literal names: the plan reuses the frozen WPF app's exact
/// mutex/pipe names ("RoeSnip-SingleInstance"/"RoeSnip-SingleInstance-Capture"), but both apps run
/// side by side on the verification machine this cycle (PLAN-XPLAT.md §4's own A/B checklist), and
/// sharing names would make whichever app launches second silently signal the OTHER app's capture
/// flow and exit. Distinct names ("RoeSnip.App-...") keep the two products' instances independent;
/// flagged in the WP-X2 report for the integrator.</summary>
public sealed class SingleInstance : IDisposable
{
    private const string PipeName = "RoeSnip.App-SingleInstance";

    private static string MutexName => OperatingSystem.IsWindows()
        ? @"Global\RoeSnip.App-SingleInstance"
        : "RoeSnip.App-SingleInstance";

    private readonly Mutex _mutex;

    private SingleInstance(Mutex mutex) => _mutex = mutex;

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

        return new SingleInstance(mutex);
    }

    /// <summary>Sends one signal byte to the resident instance's pipe. Returns false (and logs to
    /// stderr) if the resident instance could not be reached.</summary>
    public static bool SignalExistingInstance(InstanceSignal signal)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
            client.Connect(2000);
            client.WriteByte((byte)signal);
            client.Flush();
            return true;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(
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
                if (raw is (int)InstanceSignal.TriggerCapture or (int)InstanceSignal.TriggerSettings)
                {
                    onSignal((InstanceSignal)raw);
                }
                else if (raw >= 0)
                {
                    Console.Error.WriteLine($"RoeSnip: ignoring unknown single-instance signal {raw}.");
                }
            }
            catch (OperationCanceledException)
            {
                // shutting down
            }
            catch (Exception ex) when (!token.IsCancellationRequested)
            {
                Console.Error.WriteLine($"RoeSnip: single-instance pipe listener error: {ex.Message}");
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
