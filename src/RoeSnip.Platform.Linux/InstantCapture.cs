namespace RoeSnip.Platform.Linux;

/// <summary>The app-facing switch for Wayland instant capture (the kept-open snapshot screencast).
/// The share dialog opens only from <see cref="RequestAsync"/> (Settings' "Allow instant capture");
/// with a saved restore token the session reopens silently on every start.</summary>
public static class InstantCapture
{
    /// <summary>Wayland: captures go through the screencast (else X11 needs no permission).</summary>
    public static bool Applies => OperatingSystem.IsLinux() && ScreenCastSession.IsWaylandSession;

    /// <summary>A restore token is saved or a session is open this process.</summary>
    public static bool IsAllowed => ScreenCastSession.Snapshot.HasSavedToken || ScreenCastSession.Snapshot.IsLive;

    /// <summary>Shows the desktop's share dialog; true when the user allowed it.</summary>
    public static Task<bool> RequestAsync() => ScreenCastSession.Snapshot.RequestPermissionAsync();
}
