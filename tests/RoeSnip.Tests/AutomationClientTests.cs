using System;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Threading.Tasks;
using RoeSnip.App;
using Xunit;

namespace RoeSnip.Tests;

/// <summary>Regression coverage for the --auto CLI client's disposal-order bug: a real resident
/// commonly tears its end of the pipe down the instant after flushing the response line (a fast
/// request/response cycle), which made <c>AutomationClient.Run</c>'s old `using var` cleanup throw
/// "Cannot access a closed pipe" from INSIDE its own try block — caught by the generic
/// `catch (Exception)`, stomping an already-correct, already-printed ok:true response with a
/// spurious exit code 1. This reproduces that exact abrupt-close shape directly against a bare
/// <see cref="NamedPipeServerStream"/> (never a live RoeSnip.exe resident, never the
/// single-instance mutex/pipe) and asserts the exit code tracks the response's own "ok" field
/// regardless of what the server does to its end immediately after replying.</summary>
public class AutomationClientTests
{
    /// <summary>Starts a one-shot server on a private, per-call pipe name (never the real
    /// "RoeSnip-Automation" name — a resident running with --automation owns that with
    /// maxNumberOfServerInstances:1, so squatting on it here would race a real resident's bind and
    /// let a stray real `--auto` invocation talk to this fake server mid-run) that reads exactly
    /// one request line, writes <paramref name="serverResponse"/>, and disposes its stream/reader/
    /// writer immediately afterward (all three go out of scope at the end of the `using` block,
    /// deliberately right after the write) — the abrupt-close race this test exists to cover, not
    /// a graceful shutdown. Returns whatever exit code <see cref="AutomationClient.Run"/> produces
    /// against it.</summary>
    private static async Task<int> RunAgainstServerAsync(string request, string serverResponse)
    {
        string pipeName = $"RoeSnip-Automation-Test-{Guid.NewGuid():N}";
        var serverReady = new TaskCompletionSource<bool>();
        var serverTask = Task.Run(() =>
        {
            try
            {
                using var server = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1);
                serverReady.SetResult(true);
                server.WaitForConnection();
                using var writer = new StreamWriter(server, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), leaveOpen: true) { AutoFlush = true, NewLine = "\n" };
                using var reader = new StreamReader(server, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, leaveOpen: true);
                _ = reader.ReadLine(); // the client's request line — content doesn't matter to this test
                writer.WriteLine(serverResponse);
                // server/writer/reader all dispose here, right after the response line is flushed —
                // exactly the abrupt-close shape a real resident produces on a fast round trip.
            }
            catch (Exception ex)
            {
                // Fault serverReady too, not just serverTask: without this, a throw BEFORE
                // SetResult(true) above (e.g. the pipe name is somehow already taken) leaves
                // serverReady's task neither completed nor faulted, so `await serverReady.Task`
                // below hangs forever instead of failing loudly with this exception.
                serverReady.TrySetException(ex);
                throw;
            }
        });

        // Block until the pipe instance actually exists and is listening before the client tries
        // to connect — Connect(2000) would otherwise race a server that hasn't bound the name yet.
        await serverReady.Task;

        int exitCode = AutomationClient.Run(request, pipeName);
        await serverTask;
        return exitCode;
    }

    [Fact]
    public async Task Run_OkTrueResponse_ExitsZero_EvenWithAbruptServerClose()
    {
        int exitCode = await RunAgainstServerAsync("state", "{\"ok\":true,\"mode\":\"idle\"}");
        Assert.Equal(0, exitCode);
    }

    [Fact]
    public async Task Run_OkFalseResponse_ExitsOne_EvenWithAbruptServerClose()
    {
        int exitCode = await RunAgainstServerAsync("state", "{\"ok\":false,\"error\":\"boom\"}");
        Assert.Equal(1, exitCode);
    }
}
