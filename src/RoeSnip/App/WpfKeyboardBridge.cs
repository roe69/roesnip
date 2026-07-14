using System;
using System.Windows.Interop;
using RoeSnip.Core.Diagnostics;

namespace RoeSnip.App;

/// <summary>Routes keyboard messages from the WinForms message pump into WPF's keyboard stack.
///
/// ROOT CAUSE this fixes (UX rounds 1-3's "keys don't work" lineage): TrayApp runs the thread's
/// message loop via WinForms Application.Run(), but every overlay/settings/color-picker window is
/// WPF. WPF keyboard input (KeyDown routing, WM_CHAR text composition — i.e. typing in a TextBox)
/// only functions when the message loop calls ComponentDispatcher.RaiseThreadMessage for keyboard
/// messages, which WPF's own Dispatcher loop does and WinForms' loop does NOT. Without this
/// bridge, WPF windows on this thread are keyboard-deaf even when they hold real OS focus — mouse
/// works (plain WndProc), keyboard never reaches KeyboardDevice. The session LL hook papered over
/// it for the handful of session shortcuts; typing into text annotations stayed dead.
///
/// The filter forwards the full WM_KEYFIRST..WM_KEYLAST range thread-wide: WPF's
/// ThreadPreprocessMessage handlers check whether the target HWND belongs to one of their own
/// HwndSources and return unhandled otherwise, so WinForms-targeted keys (tray menu navigation)
/// are unaffected. Installed once in TrayApp.Run before the message loop starts.</summary>
internal sealed class WpfKeyboardBridge : System.Windows.Forms.IMessageFilter
{
    private const int WmKeyFirst = 0x0100; // WM_KEYDOWN
    private const int WmKeyLast = 0x0109;  // WM_UNICHAR

    public bool PreFilterMessage(ref System.Windows.Forms.Message m)
    {
        if (m.Msg < WmKeyFirst || m.Msg > WmKeyLast)
        {
            return false;
        }

        try
        {
            var msg = new MSG
            {
                hwnd = m.HWnd,
                message = m.Msg,
                wParam = m.WParam,
                lParam = m.LParam,
            };
            // true = WPF handled it (typically also generating the WM_CHAR text composition
            // itself) — swallow so it isn't double-dispatched. false = not a WPF-targeted key;
            // let the WinForms loop process it normally.
            return ComponentDispatcher.RaiseThreadMessage(ref msg);
        }
        catch (Exception ex)
        {
            FileLog.Write($"RoeSnip: WPF keyboard bridge error (key passed through): {ex.Message}");
            return false;
        }
    }
}
