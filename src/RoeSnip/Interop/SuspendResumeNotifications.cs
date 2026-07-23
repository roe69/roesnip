using System;
using System.Runtime.InteropServices;
using RoeSnip.Core.Diagnostics;

namespace RoeSnip.Interop;

/// <summary>Callback-based suspend/resume notifications
/// (PowerRegisterSuspendResumeNotification, Win8+). This exists because
/// SystemEvents.PowerModeChanged is NOT a reliable resume signal: .NET's SystemEvents maps only
/// PBT_APMRESUMESUSPEND/RESUMECRITICAL/RESUMESTANDBY to PowerModes.Resume and silently drops
/// PBT_APMRESUMEAUTOMATIC — the one broadcast every wake actually delivers on modern
/// standby-capable machines. Measured on the dev machine (2026-07-23 log audit): 10 sleep/wake
/// cycles, 10 DisplaySettingsChanged bursts, ZERO PowerModeChanged(Resume) deliveries — the whole
/// post-sleep capture-stack re-warm was dead code. This registration hears PBT_APMRESUMEAUTOMATIC
/// and PBT_APMRESUMESUSPEND both; TrayApp keeps the SystemEvents subscription as a belt and
/// dedupes the two sources.
///
/// The callback arrives on a power-manager thread and must return quickly — the registered
/// onResume delegate has to stay tiny (TrayApp.OnSystemResumed only notes the resume and
/// schedules a background task, which qualifies).</summary>
internal static class SuspendResumeNotifications
{
    private const int DeviceNotifyCallback = 2; // DEVICE_NOTIFY_CALLBACK
    private const int PbtApmResumeSuspend = 0x0007;
    private const int PbtApmResumeAutomatic = 0x0012;

    private delegate int DeviceNotifyCallbackRoutine(IntPtr context, int type, IntPtr setting);

    [StructLayout(LayoutKind.Sequential)]
    private struct DEVICE_NOTIFY_SUBSCRIBE_PARAMETERS
    {
        public IntPtr Callback;
        public IntPtr Context;
    }

    // The delegate must stay rooted for the registration's lifetime — the native side holds only
    // the raw function pointer, and a collected delegate means a callback into freed memory.
    private static DeviceNotifyCallbackRoutine? s_callback;
    private static Action<string>? s_onResume;
    private static IntPtr s_registration;

    /// <summary>Registers for suspend/resume callbacks; on any resume broadcast, invokes
    /// <paramref name="onResume"/> with a short source description. Idempotent-ish: call once at
    /// startup. Never throws — a failed registration is logged and the caller's SystemEvents belt
    /// still stands.</summary>
    public static void Register(Action<string> onResume)
    {
        if (s_registration != IntPtr.Zero)
        {
            return;
        }
        try
        {
            s_onResume = onResume;
            s_callback = OnPowerEvent;
            var parameters = new DEVICE_NOTIFY_SUBSCRIBE_PARAMETERS
            {
                Callback = Marshal.GetFunctionPointerForDelegate(s_callback),
                Context = IntPtr.Zero,
            };
            uint result = PowerRegisterSuspendResumeNotification(
                DeviceNotifyCallback, ref parameters, out s_registration);
            if (result != 0)
            {
                FileLog.Write(
                    $"RoeSnip: suspend/resume notification registration failed (0x{result:X8}); " +
                    "falling back to SystemEvents.PowerModeChanged only.");
                s_registration = IntPtr.Zero;
                s_callback = null;
            }
        }
        catch (Exception ex)
        {
            FileLog.Write(
                $"RoeSnip: suspend/resume notification registration failed ({ex.Message}); " +
                "falling back to SystemEvents.PowerModeChanged only.");
            s_registration = IntPtr.Zero;
            s_callback = null;
        }
    }

    public static void Unregister()
    {
        if (s_registration == IntPtr.Zero)
        {
            return;
        }
        try
        {
            PowerUnregisterSuspendResumeNotification(s_registration);
        }
        catch { /* teardown best-effort */ }
        s_registration = IntPtr.Zero;
        s_callback = null;
        s_onResume = null;
    }

    private static int OnPowerEvent(IntPtr context, int type, IntPtr setting)
    {
        if (type is PbtApmResumeAutomatic or PbtApmResumeSuspend)
        {
            try
            {
                s_onResume?.Invoke($"power callback 0x{type:X}");
            }
            catch { /* never let an exception escape into the power manager's thread */ }
        }
        return 0;
    }

    [DllImport("powrprof.dll", ExactSpelling = true)]
    private static extern uint PowerRegisterSuspendResumeNotification(
        int flags, ref DEVICE_NOTIFY_SUBSCRIBE_PARAMETERS recipient, out IntPtr registrationHandle);

    [DllImport("powrprof.dll", ExactSpelling = true)]
    private static extern uint PowerUnregisterSuspendResumeNotification(IntPtr registrationHandle);
}
