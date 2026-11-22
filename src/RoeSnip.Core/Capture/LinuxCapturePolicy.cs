namespace RoeSnip.Core.Capture;

/// <summary>Where the Linux snapshot screencast stands, for <see cref="LinuxCapturePolicy"/>.</summary>
public enum ScreenCastState
{
    /// <summary>Not opened yet (a saved token restores it silently, or the user has not been asked).</summary>
    Idle,
    Opening,
    Live,
    /// <summary>The user cancelled or denied the share dialog.</summary>
    Denied,
    /// <summary>The last open failed for another reason (timeout, stream error).</summary>
    Failed,
    /// <summary>The desktop cannot screencast at all: no ScreenCast portal, no GStreamer pipewiresrc,
    /// no session bus, or an X11 session (no screencast used).</summary>
    Unsupported,
}

/// <summary>Answer of the instant-capture share request.</summary>
public enum ScreenShareRequestResult
{
    Allowed,
    /// <summary>Cancelled, denied or left unanswered.</summary>
    Declined,
    /// <summary>The desktop cannot screencast (see <see cref="ScreenCastState.Unsupported"/>).</summary>
    Unsupported,
}

/// <summary>Which Linux capture path a capture may take. GNOME's Screenshot portal plays a shutter
/// sound and flashes every screen, so it is never used there; elsewhere only when the desktop
/// cannot screencast at all.</summary>
public static class LinuxCapturePolicy
{
    /// <summary>XDG_CURRENT_DESKTOP is a colon list ("ubuntu:GNOME", "GNOME-Classic:GNOME").</summary>
    public static bool IsGnome(string? xdgCurrentDesktop)
    {
        if (string.IsNullOrEmpty(xdgCurrentDesktop)) return false;
        foreach (var part in xdgCurrentDesktop.Split(':'))
        {
            if (part.Contains("GNOME", StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    public static bool IsGnomeDesktop => IsGnome(Environment.GetEnvironmentVariable("XDG_CURRENT_DESKTOP"));

    /// <summary>True when the Screenshot portal may run; <paramref name="refusal"/> says why not.</summary>
    public static bool ScreenshotPortalAllowed(bool isGnome, bool isBackground, ScreenCastState state, out string refusal)
    {
        if (isBackground) { refusal = "background capture"; return false; }
        if (isGnome) { refusal = "GNOME desktop: its screenshot portal plays a shutter sound and flashes the screen"; return false; }
        if (state != ScreenCastState.Unsupported) { refusal = $"screencast is {state.ToString().ToLowerInvariant()}"; return false; }
        refusal = "";
        return true;
    }

    /// <summary>A user's capture on Wayland without instant capture allowed asks for the screen
    /// share first (the dialog the Settings button shows) instead of capturing.</summary>
    public static bool AskForScreenShareFirst(bool isWayland, bool instantCaptureAllowed, ScreenCastState state)
        => isWayland && !instantCaptureAllowed && state is not (ScreenCastState.Unsupported or ScreenCastState.Live);
}
