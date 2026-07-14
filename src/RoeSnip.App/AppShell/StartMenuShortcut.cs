using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Runtime.Versioning;
using System.Text;

namespace RoeSnip.App.AppShell;

/// <summary>Start Menu shortcut for the installed copy, so the app is findable in Windows search
/// (port of the WPF app's App/StartMenuShortcut.cs). Install() puts the exe under
/// %LOCALAPPDATA%\RoeSnip.App, which Windows Search does not index - an installed copy is invisible
/// to Start-menu search unless a shortcut exists under the user's Start Menu\Programs folder (the
/// thing search actually indexes). The shortcut is named "RoeSnip.App.lnk", matching this port's
/// Run-key value and keeping it distinct from the WPF app's "RoeSnip.lnk" when both are installed.
/// Self-updates swap the exe at the same path, so a once-created shortcut stays valid across
/// updates; <see cref="EnsureFor"/> is also called at startup (not just from Install) so installs
/// that predate this feature gain the shortcut on their first launch after updating. Windows-only:
/// Linux/macOS have no Install() at all (parity item 13's accepted limitation), so there is nothing
/// to make findable there. Same convenience-never-load-bearing contract as the rest of the
/// install/update machinery: never throws, reviewed by eye rather than unit-tested (real COM +
/// real filesystem).</summary>
[SupportedOSPlatform("windows")]
public static class StartMenuShortcut
{
    public static string ShortcutPath { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "RoeSnip.App.lnk");

    /// <summary>Creates (or repoints) the Start Menu shortcut at <see cref="ShortcutPath"/> so it
    /// targets <paramref name="targetExePath"/>. Skips the write entirely when an existing shortcut
    /// already points there - this runs on every startup of the installed copy, and rewriting an
    /// unchanged .lnk each launch would churn the search index for nothing. Never throws.</summary>
    public static void EnsureFor(string targetExePath)
    {
        try
        {
            if (TargetAlreadyCorrect(targetExePath))
            {
                return;
            }

            var link = (IShellLinkW)new ShellLinkCoClass();
            link.SetPath(targetExePath);
            link.SetWorkingDirectory(Path.GetDirectoryName(targetExePath) ?? string.Empty);
            link.SetDescription("RoeSnip screen capture");
            ((IPersistFile)link).Save(ShortcutPath, true);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"RoeSnip: could not create the Start Menu shortcut (non-fatal): {ex.Message}");
        }
    }

    /// <summary>True only when a shortcut already exists AND resolves to
    /// <paramref name="targetExePath"/>. Any failure to read it (corrupt file, COM error) returns
    /// false so the caller rewrites it - a bad shortcut is repaired, never trusted.</summary>
    private static bool TargetAlreadyCorrect(string targetExePath)
    {
        try
        {
            if (!File.Exists(ShortcutPath))
            {
                return false;
            }

            var link = (IShellLinkW)new ShellLinkCoClass();
            ((IPersistFile)link).Load(ShortcutPath, 0);
            var current = new StringBuilder(260);
            link.GetPath(current, current.Capacity, IntPtr.Zero, SLGP_RAWPATH);
            return string.Equals(
                Path.GetFullPath(current.ToString()),
                Path.GetFullPath(targetExePath),
                StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    // SLGP_RAWPATH: return the stored path as-is instead of resolving through the link tracker -
    // the comparison is against the literal install path this class itself wrote.
    private const uint SLGP_RAWPATH = 0x4;

    [ComImport, Guid("00021401-0000-0000-C000-000000000046")]
    private class ShellLinkCoClass
    {
    }

    // Vtable order is the binary contract with shell32 - never reorder or remove entries, even the
    // unused ones (each method's slot position is what COM dispatches on, not its name).
    [ComImport, Guid("000214F9-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszFile, int cch, IntPtr pfd, uint fFlags);
        void GetIDList(out IntPtr ppidl);
        void SetIDList(IntPtr pidl);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszName, int cch);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string pszName);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszDir, int cch);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string pszDir);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszArgs, int cch);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string pszArgs);
        void GetHotkey(out ushort pwHotkey);
        void SetHotkey(ushort wHotkey);
        void GetShowCmd(out int piShowCmd);
        void SetShowCmd(int iShowCmd);
        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszIconPath, int cch, out int piIcon);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string pszIconPath, int iIcon);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string pszPathRel, uint dwReserved);
        void Resolve(IntPtr hwnd, uint fFlags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string pszFile);
    }
}
