namespace RoeSnip.App;

/// <summary>WP-X1 stub entry point so the executable project (and the solution) compiles before
/// WP-X2/WP-X3 land. WP-X2 owns Program.cs/App.axaml/AppShell (PLAN-XPLAT.md §3.2) and must DELETE
/// this file when it adds the real Program.cs (two Main methods cannot coexist).
///
/// NOTE for WP-X2 (orchestrator-approved override to PLAN-XPLAT.md §6 flag 7): the csproj sets
/// OutputType=WinExe when building for Windows, so the real Program.Main must call
/// kernel32!AttachConsole(ATTACH_PARENT_PROCESS) — i.e.
/// <c>[DllImport("kernel32.dll")] static extern bool AttachConsole(int pid);</c> with pid -1,
/// guarded by OperatingSystem.IsWindows() — at CLI-verb startup (--diag/--capture/capture/settings)
/// BEFORE writing any console output, so a WinExe binary still prints to the invoking terminal.</summary>
internal static class Placeholder
{
    private static int Main()
    {
        // Deliberately inert: WP-X1 never launches any GUI. Replaced wholesale by WP-X2's Program.cs.
        return 0;
    }
}
