namespace RoeSnip.Core.Capture;

/// <summary>Captures a single frame of one monitor. Unchanged from the WPF app's contract
/// (PLAN.md §2.3) — Windows' DD/WGC capturers, Linux's portal/X11 capturers all implement this.</summary>
public interface IScreenCapturer
{
    CapturedFrame Capture(MonitorInfo monitor);
}
