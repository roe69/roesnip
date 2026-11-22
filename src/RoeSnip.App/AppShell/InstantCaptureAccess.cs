using System.Threading.Tasks;
using RoeSnip.Core.Capture;

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

    /// <summary>A user's capture must ask for the screen share first (Wayland, not allowed yet,
    /// the desktop can screencast). Never the Screenshot portal in its place.</summary>
    public static bool CaptureMustAskFirst
    {
        get
        {
#if LINUX_BACKEND
            return LinuxCapturePolicy.AskForScreenShareFirst(
                RoeSnip.Platform.Linux.InstantCapture.Applies,
                RoeSnip.Platform.Linux.InstantCapture.IsAllowed,
                RoeSnip.Platform.Linux.InstantCapture.State);
#else
            return false;
#endif
        }
    }

    public static string UnavailableReason
    {
        get
        {
#if LINUX_BACKEND
            return RoeSnip.Platform.Linux.InstantCapture.UnavailableReason;
#else
            return "";
#endif
        }
    }

    /// <summary>Settings' button: shows the desktop's share dialog; true when allowed.</summary>
    public static async Task<bool> RequestAsync()
        => await RequestAsync("Settings").ConfigureAwait(false) == ScreenShareRequestResult.Allowed;

    /// <summary>Shows the desktop's share dialog and waits for the answer.</summary>
    public static Task<ScreenShareRequestResult> RequestAsync(string source)
    {
#if LINUX_BACKEND
        return RoeSnip.Platform.Linux.InstantCapture.RequestAsync(source);
#else
        return Task.FromResult(ScreenShareRequestResult.Unsupported);
#endif
    }
}
