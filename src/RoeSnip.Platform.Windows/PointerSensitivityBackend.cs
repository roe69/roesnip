using System.Runtime.InteropServices;
using RoeSnip.Core.Diagnostics;
using RoeSnip.Core.Overlay;

namespace RoeSnip.Platform.Windows;

/// <summary>Slows the pointer while the loupe is zoomed in (see <see cref="PointerScaling"/>), with a
/// session-scoped WH_MOUSE_LL hook. For each real (non-injected) WM_MOUSEMOVE it takes the delta
/// between the hook's target point and the current cursor position, scales it with a sub-pixel
/// remainder, moves the cursor itself with SetCursorPos and swallows the original move. A factor of
/// 1.0 passes everything through untouched. System mouse settings are never touched.
///
/// Mirrors src/RoeSnip/Overlay/PointerScaleHook.cs. Must be installed on a thread that pumps
/// messages (the UI thread); the callback does no allocation, and any failure passes the event
/// through. Dispose is idempotent.</summary>
internal sealed class WindowsPointerSensitivityBackend : IPointerSensitivityBackend
{
    public IDisposable? Install(Func<double> getFactor)
    {
        var hook = new PointerScaleHook(getFactor);
        return hook.IsInstalled ? hook : null;
    }
}

internal sealed class PointerScaleHook : IDisposable
{
    private const int WhMouseLl = 14;
    private const int WmMouseMove = 0x0200;
    private const int LlmhfInjected = 0x01;
    private const int SmXVirtualScreen = 76, SmYVirtualScreen = 77, SmCxVirtualScreen = 78, SmCyVirtualScreen = 79;

    // MSLLHOOKSTRUCT: POINT pt (x at 0, y at 4), DWORD mouseData (8), DWORD flags (12).
    private const int OffsetY = 4, OffsetFlags = 12;

    private delegate IntPtr LowLevelMouseProc(int nCode, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct Pt { public int X, Y; }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelMouseProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);

    [DllImport("user32.dll")] private static extern bool GetCursorPos(out Pt lpPoint);
    [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int nIndex);

    private readonly Func<double> _getFactor;
    private readonly LowLevelMouseProc _proc; // rooted for the hook's lifetime
    private readonly PointerDeltaScaler _scaler = new();
    private readonly int _minX, _minY, _maxX, _maxY;
    private IntPtr _hookHandle = IntPtr.Zero;
    private bool _inSet;
    private int _disposed;

    public PointerScaleHook(Func<double> getFactor)
    {
        _getFactor = getFactor;
        _proc = HookProc;
        _minX = GetSystemMetrics(SmXVirtualScreen);
        _minY = GetSystemMetrics(SmYVirtualScreen);
        _maxX = _minX + GetSystemMetrics(SmCxVirtualScreen) - 1;
        _maxY = _minY + GetSystemMetrics(SmCyVirtualScreen) - 1;
        try
        {
            using var currentProcess = System.Diagnostics.Process.GetCurrentProcess();
            using var mainModule = currentProcess.MainModule;
            IntPtr hMod = mainModule is not null ? GetModuleHandle(mainModule.ModuleName) : IntPtr.Zero;

            _hookHandle = SetWindowsHookEx(WhMouseLl, _proc, hMod, 0);
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

    public bool IsInstalled => _hookHandle != IntPtr.Zero;

    private IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && (int)wParam == WmMouseMove && !_inSet)
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
        return CallNextHookEx(_hookHandle, nCode, wParam, lParam);
    }

    /// <summary>True when the move was replaced by a scaled SetCursorPos (swallow the original).</summary>
    private bool ScaleMove(IntPtr lParam)
    {
        if ((Marshal.ReadInt32(lParam, OffsetFlags) & LlmhfInjected) != 0)
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
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }
        if (_hookHandle != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_hookHandle);
            _hookHandle = IntPtr.Zero;
        }
    }
}

file static class PointerSensitivityModuleInit
{
    // Same seam as WindowsCaptureBackend's initializer: the App force-loads the platform assemblies
    // at startup, which makes this registration deterministic. Non-Windows assemblies never set it.
#pragma warning disable CA2255
    [System.Runtime.CompilerServices.ModuleInitializer]
#pragma warning restore CA2255
    internal static void Init()
    {
        if (OperatingSystem.IsWindows())
        {
            PointerSensitivityRegistry.Current = new WindowsPointerSensitivityBackend();
        }
    }
}
