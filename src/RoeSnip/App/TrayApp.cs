using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace RoeSnip.App;

/// <summary>The tray-app shell (DESIGN.md §1-2, PLAN.md §3.3): a WinForms <see cref="NotifyIcon"/>
/// with a context menu, the global hotkey, single-instance enforcement (named mutex + a named-pipe
/// signal so a second launch triggers the first instance's capture flow and exits 0), and the
/// <see cref="ITrayNotifier"/> balloons for the cross-cutting "saved"/"error" notifications that
/// <see cref="AppComposition.RunCaptureFlowAsync"/> surfaces back through this class.</summary>
public sealed class TrayApp : ITrayNotifier
{
    private const string MutexName = @"Global\RoeSnip-SingleInstance";
    private const string PipeName = "RoeSnip-SingleInstance-Capture";

    private NotifyIcon? _notifyIcon;
    private HotkeyManager? _hotkeyManager;
    private System.Windows.Forms.Control? _uiThreadMarshal;
    private CancellationTokenSource? _pipeListenerCts;
    private RoeSnipSettings _settings = RoeSnipSettings.Default;
    private EventHandler? _activeBalloonClickHandler;

    /// <summary>Runs the tray app: NotifyIcon + context menu + message loop. If another instance is
    /// already running (named mutex held), signals it to run the capture flow instead and returns
    /// 0 immediately without creating a second tray icon.</summary>
    public static int Run(string[] args)
    {
        using var mutex = new Mutex(initiallyOwned: true, MutexName, out bool createdNew);
        if (!createdNew)
        {
            SignalExistingInstance();
            return 0;
        }

        var app = new TrayApp();
        return app.RunInstance();
    }

    private int RunInstance()
    {
        Application.EnableVisualStyles();

        _settings = AppComposition.LoadSettings?.Invoke() ?? RoeSnipSettings.Default;

        // A hidden Control purely so the pipe-listener background task can marshal the capture
        // trigger back onto this (UI/message-loop) thread via BeginInvoke.
        _uiThreadMarshal = new System.Windows.Forms.Control();
        _uiThreadMarshal.CreateControl();

        _hotkeyManager = new HotkeyManager(() => TriggerCapture());
        _hotkeyManager.Register(_settings);

        using var trayIcon = CreateTrayIcon();
        _notifyIcon = trayIcon;

        var menu = new ContextMenuStrip();
        menu.Items.Add("Capture", null, (_, _) => TriggerCapture());
        menu.Items.Add("Settings...", null, (_, _) => OpenSettings());
        menu.Items.Add("About", null, (_, _) => ShowAbout());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => Application.Exit());

        _notifyIcon.ContextMenuStrip = menu;
        _notifyIcon.Text = "RoeSnip";
        _notifyIcon.Visible = true;
        _notifyIcon.DoubleClick += (_, _) => TriggerCapture();

        _pipeListenerCts = new CancellationTokenSource();
        _ = ListenForSignalAsync(_pipeListenerCts.Token);

        Application.Run();

        _pipeListenerCts.Cancel();
        _notifyIcon.Visible = false;
        _hotkeyManager.Dispose();
        _uiThreadMarshal.Dispose();

        return 0;
    }

    private void TriggerCapture()
    {
        // Fire-and-forget from a UI event handler: RunCaptureFlowAsync reports its own failures
        // via ITrayNotifier (this), so there's nothing further to await/observe here.
        _ = AppComposition.RunCaptureFlowAsync(_settings, this);
    }

    private void OpenSettings()
    {
        var window = new SettingsWindow(_settings, updated =>
        {
            _settings = updated;
            _hotkeyManager?.Register(_settings);
        });
        window.ShowDialog();
    }

    private static void ShowAbout()
    {
        MessageBox.Show(
            "RoeSnip\n\n" +
            "An HDR-correct screenshot tool. On HDR/Advanced-Color displays, RoeSnip captures the " +
            "true linear scRGB frame and tone-maps it properly (matching the SDR white level and " +
            "rolling off highlights) instead of producing the washed-out gray screenshots typical " +
            "of legacy capture tools.",
            "About RoeSnip",
            MessageBoxButtons.OK,
            MessageBoxIcon.Information);
    }

    /// <inheritdoc/>
    public void ShowSavedBalloon(string filePath)
    {
        if (_notifyIcon is null) return;

        DetachActiveBalloonHandler();
        _activeBalloonClickHandler = (_, _) =>
        {
            DetachActiveBalloonHandler();
            try
            {
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{filePath}\"") { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"RoeSnip: failed to open folder for {filePath}: {ex.Message}");
            }
        };
        _notifyIcon.BalloonTipClicked += _activeBalloonClickHandler;
        _notifyIcon.BalloonTipTitle = "RoeSnip";
        _notifyIcon.BalloonTipText = $"Saved {Path.GetFileName(filePath)} — click to open folder";
        _notifyIcon.ShowBalloonTip(4000);
    }

    /// <inheritdoc/>
    public void ShowError(string message)
    {
        if (_notifyIcon is null)
        {
            Console.Error.WriteLine($"RoeSnip: {message}");
            return;
        }

        DetachActiveBalloonHandler();
        _notifyIcon.BalloonTipTitle = "RoeSnip";
        _notifyIcon.BalloonTipText = message;
        _notifyIcon.BalloonTipIcon = ToolTipIcon.Error;
        _notifyIcon.ShowBalloonTip(6000);
    }

    private void DetachActiveBalloonHandler()
    {
        if (_activeBalloonClickHandler is not null && _notifyIcon is not null)
        {
            _notifyIcon.BalloonTipClicked -= _activeBalloonClickHandler;
        }
        _activeBalloonClickHandler = null;
    }

    // ---------------- Single-instance signalling (named mutex + named pipe) ----------------

    private static void SignalExistingInstance()
    {
        try
        {
            using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
            client.Connect(2000);
            client.WriteByte(1);
            client.Flush();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"RoeSnip: another instance appears to be running, but signalling it failed: {ex.Message}");
        }
    }

    private async Task ListenForSignalAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                using var server = new NamedPipeServerStream(
                    PipeName, PipeDirection.In, maxNumberOfServerInstances: 1,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                await server.WaitForConnectionAsync(token).ConfigureAwait(false);
                _ = server.ReadByte(); // drain the single signal byte
                _uiThreadMarshal?.BeginInvoke(new Action(TriggerCapture));
            }
            catch (OperationCanceledException)
            {
                // shutting down
            }
            catch (Exception ex) when (!token.IsCancellationRequested)
            {
                Console.Error.WriteLine($"RoeSnip: single-instance pipe listener error: {ex.Message}");
                await Task.Delay(1000, token).ContinueWith(_ => { }).ConfigureAwait(false);
            }
        }
    }

    // ---------------- Programmatic tray icon (no asset files needed) ----------------

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr hIcon);

    private static NotifyIcon CreateTrayIcon()
    {
        using var bitmap = new Bitmap(16, 16);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.Clear(System.Drawing.Color.Transparent);

            // Simple stylized camera glyph: dark body, a small "flash bump" on top, and a lens.
            using var bodyBrush = new SolidBrush(System.Drawing.Color.FromArgb(255, 74, 158, 255)); // accent blue
            using var darkBrush = new SolidBrush(System.Drawing.Color.FromArgb(255, 30, 30, 30));
            using var lensPen = new Pen(System.Drawing.Color.FromArgb(255, 30, 30, 30), 1.5f);

            g.FillRectangle(bodyBrush, 1, 4, 14, 10);
            g.FillRectangle(bodyBrush, 5, 1, 6, 3);
            g.FillEllipse(darkBrush, 4, 6, 8, 6);
            g.DrawEllipse(lensPen, 6, 8, 4, 3);
        }

        IntPtr hIcon = bitmap.GetHicon();
        try
        {
            using var tempIcon = Icon.FromHandle(hIcon);
            var icon = (Icon)tempIcon.Clone(); // Clone owns its own resources, safe to keep after DestroyIcon.
            return new NotifyIcon { Icon = icon };
        }
        finally
        {
            DestroyIcon(hIcon);
        }
    }
}

file static class ModuleInit
{
    [System.Runtime.CompilerServices.ModuleInitializer]
    internal static void Init() => AppComposition.RunTrayApp = TrayApp.Run;
}
