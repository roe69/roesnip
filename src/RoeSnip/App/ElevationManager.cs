using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace RoeSnip.App;

/// <summary>Persistent elevation via a Windows Scheduled Task, opt-in from Settings. Why this exists:
/// when an elevated window has foreground focus, UIPI blocks a non-elevated RoeSnip from receiving
/// the low-level keyboard hook, taking foreground, or overlaying interaction on top of it — so the
/// snip hotkey and overlay silently stop working around admin apps. The standard fix for a tray app
/// is a Scheduled Task with RunLevel=Highest and an onlogon trigger: Windows will start the task
/// elevated at login with no per-launch UAC prompt, because the user already consented once when the
/// task itself was created/registered (which does require elevation).
///
/// RoeSnip stays asInvoker by default (see app.manifest) — this is opt-in only, toggled from
/// SettingsWindow, which round-trips through exactly one UAC prompt (Program.cs's
/// --enable-elevated-startup / --disable-elevated-startup hidden verbs) to create or delete the task.
/// Nothing here ever silently elevates anything.</summary>
public static class ElevationManager
{
    public const string TaskName = "RoeSnip";

    // ---------------- Token elevation check ----------------

    private const uint TOKEN_QUERY = 0x0008;
    private const int TokenElevation = 20; // TOKEN_INFORMATION_CLASS.TokenElevation

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(IntPtr processHandle, uint desiredAccess, out IntPtr tokenHandle);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool GetTokenInformation(
        IntPtr tokenHandle,
        int tokenInformationClass,
        IntPtr tokenInformation,
        uint tokenInformationLength,
        out uint returnLength);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);

    /// <summary>True if THIS process is running with an elevated token — distinct from merely being
    /// a member of the Administrators group (a split admin token under UAC is NOT elevated until the
    /// user actually consents, which is exactly the distinction this feature cares about).</summary>
    public static bool IsProcessElevated()
    {
        IntPtr token = IntPtr.Zero;
        try
        {
            if (!OpenProcessToken(GetCurrentProcess(), TOKEN_QUERY, out token))
            {
                return false;
            }

            int size = sizeof(int); // TOKEN_ELEVATION is a single DWORD (TokenIsElevated)
            IntPtr buffer = Marshal.AllocHGlobal(size);
            try
            {
                if (!GetTokenInformation(token, TokenElevation, buffer, (uint)size, out _))
                {
                    return false;
                }
                return Marshal.ReadInt32(buffer) != 0;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        catch
        {
            return false;
        }
        finally
        {
            if (token != IntPtr.Zero)
            {
                CloseHandle(token);
            }
        }
    }

    // ---------------- Scheduled task management (schtasks.exe) ----------------

    /// <summary>True if the "RoeSnip" scheduled task exists. Querying a task you own does not
    /// require elevation.</summary>
    public static bool IsElevatedTaskInstalled()
    {
        var (exitCode, _, _) = RunSchtasks("/query", "/tn", TaskName);
        return exitCode == 0;
    }

    /// <summary>Creates (or replaces, via /f) the "RoeSnip" scheduled task: run <paramref name="exePath"/>
    /// at the current user's logon with the highest available privileges. Registering a task with
    /// RunLevel=Highest itself requires THIS process to already be elevated — Windows will not let a
    /// non-elevated process silently grant a task admin rights, which is exactly the round-trip
    /// Program.cs's --enable-elevated-startup verb exists to satisfy. The /tr value must carry its
    /// own embedded quotes (schtasks' own parser, not just argv splitting, needs them to tell a
    /// spaces-containing path apart from trailing arguments).</summary>
    public static void EnableElevatedStartup(string exePath)
    {
        var (exitCode, stdout, stderr) = RunSchtasks(
            "/create", "/tn", TaskName, "/tr", $"\"{exePath}\"", "/sc", "onlogon", "/rl", "highest", "/f");

        if (exitCode != 0)
        {
            throw new InvalidOperationException(
                $"schtasks /create failed (exit {exitCode}): {FirstNonEmpty(stderr, stdout)}");
        }
    }

    /// <summary>Deletes the "RoeSnip" scheduled task. A no-op if it isn't installed.</summary>
    public static void DisableElevatedStartup()
    {
        if (!IsElevatedTaskInstalled())
        {
            return;
        }

        var (exitCode, stdout, stderr) = RunSchtasks("/delete", "/tn", TaskName, "/f");
        if (exitCode != 0)
        {
            throw new InvalidOperationException(
                $"schtasks /delete failed (exit {exitCode}): {FirstNonEmpty(stderr, stdout)}");
        }
    }

    /// <summary>Runs the "RoeSnip" task now. Triggering a task you own is a normal-user operation
    /// even though the task itself runs elevated — no UAC prompt, regardless of whether the calling
    /// process is elevated.</summary>
    public static void StartViaTask()
    {
        var (exitCode, stdout, stderr) = RunSchtasks("/run", "/tn", TaskName);
        if (exitCode != 0)
        {
            throw new InvalidOperationException(
                $"schtasks /run failed (exit {exitCode}): {FirstNonEmpty(stderr, stdout)}");
        }
    }

    private static string FirstNonEmpty(string primary, string fallback) =>
        string.IsNullOrWhiteSpace(primary) ? fallback.Trim() : primary.Trim();

    private static (int ExitCode, string StdOut, string StdErr) RunSchtasks(params string[] arguments)
    {
        var psi = new ProcessStartInfo("schtasks.exe")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (string arg in arguments)
        {
            psi.ArgumentList.Add(arg);
        }

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start schtasks.exe.");
        string stdout = process.StandardOutput.ReadToEnd();
        string stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        return (process.ExitCode, stdout, stderr);
    }

    // ---------------- Hidden CLI verbs (Program.cs) ----------------

    /// <summary>Handles --enable-elevated-startup: must be invoked already elevated (Program.cs's
    /// caller is the one relaunched via Verb=runas). Creates the task, then applies the startup
    /// interplay documented on <see cref="StartupManager"/>: the task's onlogon trigger replaces the
    /// HKCU Run key, so any existing Run entry is removed regardless of the persisted RunAtStartup
    /// value (there is nothing left for the Run key to usefully do once the task owns startup).</summary>
    public static int RunEnableElevatedStartupCli()
    {
        if (!IsProcessElevated())
        {
            Console.Error.WriteLine("RoeSnip: --enable-elevated-startup must be run elevated.");
            return 1;
        }

        string exePath;
        try
        {
            exePath = Environment.ProcessPath
                ?? Process.GetCurrentProcess().MainModule?.FileName
                ?? throw new InvalidOperationException("Could not determine the current executable path.");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"RoeSnip: {ex.Message}");
            return 1;
        }

        try
        {
            EnableElevatedStartup(exePath);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"RoeSnip: failed to create the elevated startup task: {ex.Message}");
            return 1;
        }

        try
        {
            StartupManager.SetRunAtStartup(false);
        }
        catch (Exception ex)
        {
            // Non-fatal: the task itself was created successfully, which is the operation that
            // matters. A stray Run key entry alongside it just means RoeSnip's second-instance
            // signalling (TrayApp.SignalExistingInstance) hands off to the elevated task's own
            // capture flow at the next logon — harmless double-launch race, not silent breakage.
            Console.Error.WriteLine($"RoeSnip: warning: failed to remove the HKCU Run key: {ex.Message}");
        }

        Console.WriteLine("RoeSnip: elevated startup task installed.");
        return 0;
    }

    /// <summary>Handles --disable-elevated-startup: must be invoked already elevated. Deletes the
    /// task, then restores the HKCU Run key to match the persisted RunAtStartup setting (see
    /// <see cref="StartupManager"/>).</summary>
    public static int RunDisableElevatedStartupCli()
    {
        if (!IsProcessElevated())
        {
            Console.Error.WriteLine("RoeSnip: --disable-elevated-startup must be run elevated.");
            return 1;
        }

        try
        {
            DisableElevatedStartup();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"RoeSnip: failed to remove the elevated startup task: {ex.Message}");
            return 1;
        }

        try
        {
            var settings = SettingsStore.Load();
            StartupManager.SetRunAtStartup(settings.RunAtStartup);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"RoeSnip: warning: failed to restore the HKCU Run key: {ex.Message}");
        }

        Console.WriteLine("RoeSnip: elevated startup task removed.");
        return 0;
    }
}

file static class ModuleInit
{
    [System.Runtime.CompilerServices.ModuleInitializer]
    internal static void Init()
    {
        AppComposition.RunEnableElevatedStartupCli = ElevationManager.RunEnableElevatedStartupCli;
        AppComposition.RunDisableElevatedStartupCli = ElevationManager.RunDisableElevatedStartupCli;
    }
}
