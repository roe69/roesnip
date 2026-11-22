using System.Runtime.InteropServices;

namespace RoeSnip.Core.Updates;

/// <summary>Linux: marks every descriptor above stderr close-on-exec before this process starts
/// another copy. The AppImage runtime hands the app the read end of its FUSE keepalive pipe
/// without O_CLOEXEC (fd 3), and Process.Start does not close inherited descriptors, so a launched
/// child kept the parent's mount daemon (same command line, e.g. "RoeSnip.AppImage
/// --self-update-now") alive for as long as the child lived. Never throws.</summary>
public static class InheritedDescriptors
{
    private const int F_GETFD = 1;
    private const int F_SETFD = 2;
    private const int FD_CLOEXEC = 1;

    [DllImport("libc", EntryPoint = "fcntl", SetLastError = true)]
    private static extern int Fcntl(int fd, int cmd, int arg);

    /// <summary>Returns how many descriptors it changed (0 off Linux).</summary>
    public static int MarkCloseOnExec()
    {
        if (!OperatingSystem.IsLinux())
        {
            return 0;
        }

        int changed = 0;
        try
        {
            foreach (string entry in Directory.GetFileSystemEntries("/proc/self/fd"))
            {
                if (!int.TryParse(Path.GetFileName(entry), out int fd) || fd <= 2)
                {
                    continue;
                }

                int flags = Fcntl(fd, F_GETFD, 0);
                if (flags < 0 || (flags & FD_CLOEXEC) != 0)
                {
                    continue; // closed meanwhile (the enumeration's own fd) or already set
                }

                if (Fcntl(fd, F_SETFD, flags | FD_CLOEXEC) == 0)
                {
                    changed++;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DllNotFoundException or EntryPointNotFoundException)
        {
        }

        return changed;
    }
}
