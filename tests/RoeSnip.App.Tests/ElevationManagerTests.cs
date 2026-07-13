using System;
using RoeSnip.App.AppShell;
using Xunit;

namespace RoeSnip.App.Tests;

/// <summary>Item 15: everything else in ElevationManager mutates the real Scheduled Task/registry
/// state or spawns schtasks.exe (same "code-reviewed rather than unit-tested" call the WPF app's own
/// ElevationManager and this port's StartupManager both make - no ElevationManagerTests/
/// StartupManagerTests exist for the WPF app either). ResolveTargetExePath is the one pure, portable
/// slice - which exe an elevated task should point at - so it gets a real test instead.</summary>
public class ElevationManagerTests
{
    [Fact]
    public void ResolveTargetExePath_InstallExists_PrefersInstalledPath()
    {
        string result = ElevationManager.ResolveTargetExePath(
            installExists: true,
            installedExePath: @"C:\Users\someone\AppData\Local\RoeSnip.App\RoeSnip.exe",
            processPath: @"C:\dev\roesnip\bin\Release\RoeSnip.exe");

        Assert.Equal(@"C:\Users\someone\AppData\Local\RoeSnip.App\RoeSnip.exe", result);
    }

    [Fact]
    public void ResolveTargetExePath_NoInstall_FallsBackToProcessPath()
    {
        string result = ElevationManager.ResolveTargetExePath(
            installExists: false,
            installedExePath: @"C:\Users\someone\AppData\Local\RoeSnip.App\RoeSnip.exe",
            processPath: @"C:\dev\roesnip\bin\Release\RoeSnip.exe");

        Assert.Equal(@"C:\dev\roesnip\bin\Release\RoeSnip.exe", result);
    }

    [Fact]
    public void ResolveTargetExePath_NoInstallAndNoProcessPath_Throws()
    {
        Assert.Throws<InvalidOperationException>(() =>
            ElevationManager.ResolveTargetExePath(
                installExists: false,
                installedExePath: @"C:\Users\someone\AppData\Local\RoeSnip.App\RoeSnip.exe",
                processPath: null));
    }

    [Fact]
    public void TaskName_IsDistinctFromTheWpfAppsTaskName()
    {
        // Both apps may be installed side by side; sharing "RoeSnip" (the WPF app's own
        // ElevationManager.TaskName) would make each app's checkbox create/delete/replace the
        // OTHER app's scheduled task.
        Assert.Equal("RoeSnip.App", ElevationManager.TaskName);
        Assert.NotEqual("RoeSnip", ElevationManager.TaskName);
    }
}
