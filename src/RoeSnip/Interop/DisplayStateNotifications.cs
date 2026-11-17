using System;
using System.Runtime.InteropServices;
using RoeSnip.Core.Diagnostics;

namespace RoeSnip.Interop;

/// <summary>Console display on/off/dimmed notifications (GUID_CONSOLE_DISPLAY_STATE via
/// PowerSettingRegisterNotification, callback delivery - no window needed). The monitors turning
/// back on after the power-plan display timeout is the moment the capture stack is coldest, so
/// TrayApp re-primes it here instead of on the first hotkey press. The callback arrives on a
/// power-manager thread: the delegate must return quickly.</summary>
internal static class DisplayStateNotifications
{
    private const int DeviceNotifyCallback = 2; // DEVICE_NOTIFY_CALLBACK
    private const int PbtPowerSettingChange = 0x8013;

    // GUID_CONSOLE_DISPLAY_STATE: payload DWORD 0 = off, 1 = on, 2 = dimmed.
    private static readonly Guid GuidConsoleDisplayState = new("6fe69556-704a-47a0-8f24-c28d936fda47");

    private delegate int DeviceNotifyCallbackRoutine(IntPtr context, int type, IntPtr setting);

    [StructLayout(LayoutKind.Sequential)]
    private struct DEVICE_NOTIFY_SUBSCRIBE_PARAMETERS
    {
        public IntPtr Callback;
        public IntPtr Context;
    }

    // Rooted for the registration's lifetime: the native side holds only the raw function pointer.
    private static DeviceNotifyCallbackRoutine? s_callback;
    private static Action<int>? s_onState;
    private static IntPtr s_registration;

    /// <summary>Invokes <paramref name="onState"/> with 0 (off), 1 (on) or 2 (dimmed) on every
    /// console display state change. Never throws; a failed registration is logged and the app
    /// just keeps its pre-existing behaviour.</summary>
    public static void Register(Action<int> onState)
    {
        if (s_registration != IntPtr.Zero)
        {
            return;
        }
        try
        {
            s_onState = onState;
            s_callback = OnPowerEvent;
            var parameters = new DEVICE_NOTIFY_SUBSCRIBE_PARAMETERS
            {
                Callback = Marshal.GetFunctionPointerForDelegate(s_callback),
                Context = IntPtr.Zero,
            };
            Guid setting = GuidConsoleDisplayState;
            uint result = PowerSettingRegisterNotification(
                ref setting, DeviceNotifyCallback, ref parameters, out s_registration);
            if (result != 0)
            {
                FileLog.Write($"RoeSnip: display state notification registration failed (0x{result:X8}).");
                s_registration = IntPtr.Zero;
                s_callback = null;
            }
        }
        catch (Exception ex)
        {
            FileLog.Write($"RoeSnip: display state notification registration failed ({ex.Message}).");
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
            PowerSettingUnregisterNotification(s_registration);
        }
        catch { /* teardown best-effort */ }
        s_registration = IntPtr.Zero;
        s_callback = null;
        s_onState = null;
    }

    private static int OnPowerEvent(IntPtr context, int type, IntPtr setting)
    {
        if (type == PbtPowerSettingChange && setting != IntPtr.Zero)
        {
            try
            {
                // POWERBROADCAST_SETTING: Guid PowerSetting (16), uint DataLength (4), byte Data[].
                var guid = Marshal.PtrToStructure<Guid>(setting);
                int length = Marshal.ReadInt32(setting, 16);
                if (guid == GuidConsoleDisplayState && length >= 4)
                {
                    s_onState?.Invoke(Marshal.ReadInt32(setting, 20));
                }
            }
            catch { /* never let an exception escape into the power manager's thread */ }
        }
        return 0;
    }

    [DllImport("powrprof.dll", ExactSpelling = true)]
    private static extern uint PowerSettingRegisterNotification(
        ref Guid settingGuid, int flags, ref DEVICE_NOTIFY_SUBSCRIBE_PARAMETERS recipient, out IntPtr registrationHandle);

    [DllImport("powrprof.dll", ExactSpelling = true)]
    private static extern uint PowerSettingUnregisterNotification(IntPtr registrationHandle);
}
