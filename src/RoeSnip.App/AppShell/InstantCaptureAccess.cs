using System.Threading.Tasks;

namespace RoeSnip.App.AppShell;

/// <summary>Wayland instant capture (Platform.Linux InstantCapture) for TrayApp and Settings,
/// compiled to "does not apply" where the Linux backend is not referenced.</summary>
internal static class InstantCaptureAccess
{
    public static bool Applies
    {
        get
        {
#if LINUX_BACKEND
            return RoeSnip.Platform.Linux.InstantCapture.Applies;
#else
            return false;
#endif
        }
    }

    public static bool IsAllowed
    {
        get
        {
#if LINUX_BACKEND
            return RoeSnip.Platform.Linux.InstantCapture.IsAllowed;
#else
            return false;
#endif
        }
    }

    /// <summary>Shows the desktop's share dialog; true when allowed.</summary>
    public static Task<bool> RequestAsync()
    {
#if LINUX_BACKEND
        return RoeSnip.Platform.Linux.InstantCapture.RequestAsync();
#else
        return Task.FromResult(false);
#endif
    }
}
