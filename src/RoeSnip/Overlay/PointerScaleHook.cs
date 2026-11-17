using System;
using System.Runtime.InteropServices;
using RoeSnip.Core.Diagnostics;
using PointerDeltaScaler = RoeSnip.Core.Overlay.PointerDeltaScaler;
using PointerScaling = RoeSnip.Core.Overlay.PointerScaling;

namespace RoeSnip.Overlay;

/// <summary>Session-scoped WH_MOUSE_LL hook that slows the pointer while the loupe is zoomed in
/// (see <see cref="PointerScaling"/>). For each real (non-injected) WM_MOUSEMOVE it takes the delta
/// between the hook's target point and the current cursor position, scales it with a sub-pixel
/// remainder, moves the cursor itself with SetCursorPos and swallows the original move. A factor of
/// 1.0 passes everything through untouched. System mouse settings are never touched.
///
/// Rules: the callback does no allocation and no WPF work beyond the cheap factor delegate (it reads
/// plain window fields; the hook runs on the installing UI thread); a failure of any kind passes the
/// event through; injected moves (including our own SetCursorPos echo, guarded by _inSet) pass
/// through; Dispose is idempotent and the owning session calls it from Finish().</summary>
internal sealed class PointerScaleHook : IDisposable
{
    private const int WH_MOUSE_LL = 14;
    private const int WM_MOUSEMOVE = 0x0200;
    private const int LLMHF_INJECTED = 0x01;
    private const int SM_XVIRTUALSCREEN = 76, SM_YVIRTUALSCREEN = 77, SM_CXVIRTUALSCREEN = 78, SM_CYVIRTUALSCREEN = 79;

    // MSLLHOOKSTRUCT: POINT pt (x at 0, y at 4), DWORD mouseData (8), DWORD flags (12).
    private const int OffsetY = 4, OffsetFlags = 12;

    [StructLayout(LayoutKind.Sequential)]
    private struct Pt { public int X, Y; }

    [DllImport("user32.dll")] private static extern bool GetCursorPos(out Pt lpPoint);
    [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int nIndex);

    private readonly Func<double> _getFactor;
    private readonly OverlayInputInterop.LowLevelKeyboardProc _proc; // rooted for the hook's lifetime
    private readonly PointerDeltaScaler _scaler = new();
    private readonly int _minX, _minY, _maxX, _maxY;
    private IntPtr _hookHandle = IntPtr.Zero;
    private bool _inSet;
    private int _disposed;

    public PointerScaleHook(Func<double> getFactor)
    {
        _getFactor = getFactor;
        _proc = HookProc;
        _minX = GetSystemMetrics(SM_XVIRTUALSCREEN);
        _minY = GetSystemMetrics(SM_YVIRTUALSCREEN);
        _maxX = _minX + GetSystemMetrics(SM_CXVIRTUALSCREEN) - 1;
        _maxY = _minY + GetSystemMetrics(SM_CYVIRTUALSCREEN) - 1;
        try
        {
            using var currentProcess = System.Diagnostics.Process.GetCurrentProcess();
            using var mainModule = currentProcess.MainModule;
            IntPtr hMod = mainModule is not null
                ? OverlayInputInterop.GetModuleHandle(mainModule.ModuleName)
                : IntPtr.Zero;

            _hookHandle = OverlayInputInterop.SetWindowsHookEx(WH_MOUSE_LL, _proc, hMod, 0);
            if (_hookHandle == IntPtr.Zero)
            {
                FileLog.Write(
                    $"RoeSnip: failed to install the pointer-scale hook (error 0x{Marshal.GetLastWin32Error():X}); " +
                    "the pointer will not slow down with the loupe zoom.");
            }
        }
        catch (Exception ex)
        {
            FileLog.Write($"RoeSnip: failed to install the pointer-scale hook: {ex.Message}");
            _hookHandle = IntPtr.Zero;
        }
    }

    private IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && (int)wParam == WM_MOUSEMOVE && !_inSet)
        {
            try
            {
                if (ScaleMove(lParam))
                {
                    return (IntPtr)1;
                }
            }
            catch (Exception ex)
            {
                FileLog.Write($"RoeSnip: pointer-scale hook callback error: {ex.Message}");
            }
        }
        return OverlayInputInterop.CallNextHookEx(_hookHandle, nCode, wParam, lParam);
    }

    /// <summary>True when the move was replaced by a scaled SetCursorPos (swallow the original).</summary>
    private bool ScaleMove(IntPtr lParam)
    {
        if ((Marshal.ReadInt32(lParam, OffsetFlags) & LLMHF_INJECTED) != 0)
        {
            return false;
        }

        double factor = _getFactor();
        if (factor >= 0.999)
        {
            _scaler.Reset();
            return false;
        }

        // The cursor has not moved yet (this hook runs first), so the raw delta is target - current.
        if (!GetCursorPos(out Pt cur))
        {
            return false;
        }
        int dx = Marshal.ReadInt32(lParam, 0) - cur.X;
        int dy = Marshal.ReadInt32(lParam, OffsetY) - cur.Y;
        if (dx == 0 && dy == 0)
        {
            return false;
        }

        _scaler.Scale(dx, dy, factor, out int sx, out int sy);
        if (sx == 0 && sy == 0)
        {
            return true; // sub-pixel: swallowed, the remainder carries to the next move
        }

        int nx = Math.Clamp(cur.X + sx, _minX, _maxX);
        int ny = Math.Clamp(cur.Y + sy, _minY, _maxY);
        _inSet = true;
        try
        {
            return SetCursorPos(nx, ny);
        }
        finally
        {
            _inSet = false;
        }
    }

    public void Dispose()
    {
        if (System.Threading.Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }
        if (_hookHandle != IntPtr.Zero)
        {
            OverlayInputInterop.UnhookWindowsHookEx(_hookHandle);
            _hookHandle = IntPtr.Zero;
        }
    }
}
