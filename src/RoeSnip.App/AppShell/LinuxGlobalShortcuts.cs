#if LINUX_BACKEND
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using RoeSnip.Core.Diagnostics;
using RoeSnip.Platform.Linux;
using Tmds.DBus;

namespace RoeSnip.App.AppShell;

/// <summary>Tmds.DBus proxy for <c>org.freedesktop.portal.GlobalShortcuts</c>. Public: Tmds.DBus
/// emits the proxy into a dynamic assembly that must see the interface.</summary>
[DBusInterface("org.freedesktop.portal.GlobalShortcuts")]
public interface IGlobalShortcutsPortal : IDBusObject
{
    Task<ObjectPath> CreateSessionAsync(IDictionary<string, object> options);

    Task<ObjectPath> BindShortcutsAsync(
        ObjectPath sessionHandle,
        (string id, IDictionary<string, object> props)[] shortcuts,
        string parentWindow,
        IDictionary<string, object> options);

    Task<IDisposable> WatchActivatedAsync(
        Action<(ObjectPath sessionHandle, string shortcutId, ulong timestamp, IDictionary<string, object> options)> handler,
        Action<Exception>? onError = null);

    Task<T> GetAsync<T>(string prop);
}

[DBusInterface("org.freedesktop.portal.Request")]
public interface IGlobalShortcutsRequest : IDBusObject
{
    Task<IDisposable> WatchResponseAsync(
        Action<(uint response, IDictionary<string, object> results)> handler,
        Action<Exception>? onError = null);
}

[DBusInterface("org.freedesktop.portal.Session")]
public interface IGlobalShortcutsSession : IDBusObject
{
    Task CloseAsync();
}

/// <summary>The Wayland capture hotkey (SharpHook is X11-only). GNOME: a custom keybinding
/// "roesnip" running "&lt;exe&gt; capture" via gsettings (silent), written only when no other custom
/// entry already uses the binding. Elsewhere (KDE Plasma 6, Hyprland): the
/// org.freedesktop.portal.GlobalShortcuts portal, bound as shortcut "capture" on the process-wide
/// <see cref="PortalConnection"/> (the app id is registered there first); it shows the desktop's dialog.</summary>
public sealed class LinuxGlobalShortcuts : IDisposable
{
    public enum Mechanism { None, Portal, GnomeKeybinding, UserShortcut }

    private const string PortalService = PortalConnection.PortalService;
    private const string ShortcutId = "capture";
    private static readonly ObjectPath PortalPath = new("/org/freedesktop/portal/desktop");
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan SessionTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan BindTimeout = TimeSpan.FromSeconds(30); // a consent dialog may be up

    private readonly Action _onActivated;
    private readonly object _gate = new();
    private Connection? _connection;
    private IGlobalShortcutsPortal? _portal;
    private IDisposable? _activatedWatch;
    private string _sender = "";
    private volatile string? _sessionHandle;
    private string? _requestedTrigger;
    private string? _gnomeAccel;
    private Task _bindChain = Task.CompletedTask;
    private volatile bool _active;
    private volatile bool _disposed;

    public LinuxGlobalShortcuts(Action onActivated)
    {
        _onActivated = onActivated;
    }

    public Mechanism Kind { get; private set; }

    /// <summary>True while a shortcut is believed to reach RoeSnip (portal bind accepted, or a
    /// GNOME keybinding in place). Optimistic for the portal until a bind is refused.</summary>
    public bool IsActive => _active;

    /// <summary>What the desktop shows for the bound trigger (portal trigger_description, or the
    /// GTK accelerator written to GNOME).</summary>
    public string? TriggerDescription { get; private set; }

    /// <summary>Raised (on any thread) when <see cref="IsActive"/> changes after <see cref="Start"/>.</summary>
    public event Action? StatusChanged;

    /// <summary>Picks a mechanism. Blocks for at most a few seconds (portal probe only); false when
    /// neither the portal nor the GNOME fallback is usable.</summary>
    public bool Start()
    {
        // GNOME first: a custom keybinding is silent, the portal shows a dialog (owner rule).
        if (IsGnome() && Gsettings("get", LinuxShortcutFormat.GnomeMediaKeysSchema, "custom-keybindings") is not null)
        {
            FileLog.Write("RoeSnip: Wayland on GNOME; binding the capture hotkey as a GNOME custom keybinding (no dialog).");
            Kind = Mechanism.GnomeKeybinding;
            return true;
        }

        FileLog.Write("RoeSnip: Wayland session; trying the GlobalShortcuts portal for the capture hotkey.");
        try
        {
            var probe = Task.Run(ProbePortalAsync);
            if (probe.Wait(ProbeTimeout))
            {
                FileLog.Write($"RoeSnip: GlobalShortcuts portal version {probe.Result} found.");
                Kind = Mechanism.Portal;
                _active = true;
                return true;
            }

            FileLog.Write($"RoeSnip: GlobalShortcuts portal probe timed out after {ProbeTimeout.TotalSeconds:F0}s.");
            _ = probe.ContinueWith(_ => CloseConnection(), TaskScheduler.Default); // a late probe leaves nothing open
        }
        catch (Exception ex)
        {
            FileLog.Write($"RoeSnip: GlobalShortcuts portal not available ({ex.GetBaseException().Message}).");
        }

        CloseConnection();
        return false;
    }

    /// <summary>(Re)binds the capture shortcut to the given stored hotkey. Portal binds run in the
    /// background (the desktop may show a consent dialog); a repeat of the same trigger is a no-op.</summary>
    public void Apply(uint modifiers, uint virtualKey)
    {
        if (Kind == Mechanism.Portal)
        {
            string? trigger = LinuxShortcutFormat.ToPortalTrigger(modifiers, virtualKey);
            if (trigger is null)
            {
                FileLog.Write($"RoeSnip: hotkey virtual key 0x{virtualKey:X} has no portal trigger; not rebinding.");
                return;
            }

            lock (_gate)
            {
                if (trigger == _requestedTrigger)
                {
                    return;
                }

                // gnome-shell keeps its own grab, so a portal bind for that key never answers.
                if (GnomeShellHolder(modifiers, virtualKey) is string held)
                {
                    FileLog.Write(
                        $"RoeSnip: GNOME still binds {trigger} to {held}; not asking the portal for it " +
                        "(allow the PrintScreen takeover or pick another hotkey).");
                    _requestedTrigger = null;
                    _bindChain = _bindChain.ContinueWith(_ => CloseSessionAsync(), TaskScheduler.Default).Unwrap();
                    SetActive(false);
                    return;
                }

                _requestedTrigger = trigger;
                _bindChain = _bindChain
                    .ContinueWith(_ => BindPortalAsync(trigger, modifiers, virtualKey), TaskScheduler.Default).Unwrap();
            }
        }
        else if (Kind is Mechanism.GnomeKeybinding or Mechanism.UserShortcut)
        {
            ApplyGnomeKeybinding(modifiers, virtualKey);
        }
    }

    /// <summary>The org.gnome.shell.keybindings key(s) holding this hotkey, or null.</summary>
    private static string? GnomeShellHolder(uint modifiers, uint virtualKey)
    {
        if (!IsGnome())
        {
            return null;
        }

        var held = new GnomePrintScreenTakeover().Detect(modifiers, virtualKey);
        return held.Count == 0 ? null : string.Join(", ", held.Keys);
    }

    /// <summary>The portal never answered: drop its session (so a late bind cannot double-fire)
    /// and use the GNOME custom keybinding route instead.</summary>
    private async Task FallBackToGnomeAsync(uint modifiers, uint virtualKey, string why)
    {
        await CloseSessionAsync().ConfigureAwait(false);
        if (_disposed)
        {
            return;
        }

        if (!IsGnome())
        {
            FileLog.Write($"RoeSnip: {why}; no fallback outside GNOME.");
            SetActive(false);
            return;
        }

        FileLog.Write($"RoeSnip: {why}; falling back to a GNOME custom keybinding.");
        lock (_gate)
        {
            Kind = Mechanism.GnomeKeybinding;
            _requestedTrigger = null;
            ApplyGnomeKeybinding(modifiers, virtualKey);
        }
    }

    private async Task<uint> ProbePortalAsync()
    {
        var shared = await PortalConnection.GetAsync().ConfigureAwait(false);
        var connection = shared.Connection;
        _connection = connection;
        _sender = shared.SenderPart;

        var portal = connection.CreateProxy<IGlobalShortcutsPortal>(PortalService, PortalPath);
        uint version = await portal.GetAsync<uint>("version").ConfigureAwait(false);
        if (version < 1)
        {
            throw new InvalidOperationException($"GlobalShortcuts version {version}");
        }

        _activatedWatch = await portal.WatchActivatedAsync(
            OnActivated,
            ex => FileLog.Write($"RoeSnip: GlobalShortcuts Activated watch failed: {ex.Message}")).ConfigureAwait(false);
        _portal = portal;
        return version;
    }

    private void OnActivated((ObjectPath sessionHandle, string shortcutId, ulong timestamp, IDictionary<string, object> options) e)
    {
        string? session = _sessionHandle;
        if (_disposed || session is null || e.shortcutId != ShortcutId || e.sessionHandle.ToString() != session)
        {
            return;
        }

        FileLog.Write("RoeSnip: GlobalShortcuts 'capture' activated.");
        _onActivated();
    }

    private async Task BindPortalAsync(string trigger, uint modifiers, uint virtualKey)
    {
        var connection = _connection;
        var portal = _portal;
        if (_disposed || connection is null || portal is null)
        {
            return;
        }

        try
        {
            await CloseSessionAsync().ConfigureAwait(false);

            string sessionToken = "roesnip_s" + Guid.NewGuid().ToString("N");
            var created = await RequestAsync(connection, token => portal.CreateSessionAsync(
                new Dictionary<string, object>
                {
                    ["handle_token"] = token,
                    ["session_handle_token"] = sessionToken,
                }), SessionTimeout).ConfigureAwait(false);
            if (created is null)
            {
                return;
            }

            string session = created.TryGetValue("session_handle", out object? handle) && handle is not null
                ? handle.ToString()!
                : $"/org/freedesktop/portal/desktop/session/{_sender}/{sessionToken}";
            _sessionHandle = session;
            FileLog.Write($"RoeSnip: GlobalShortcuts session created: {session}");

            var shortcuts = new (string, IDictionary<string, object>)[]
            {
                (ShortcutId, new Dictionary<string, object>
                {
                    ["description"] = "Take a screenshot",
                    ["preferred_trigger"] = trigger,
                }),
            };
            var bound = await RequestAsync(connection, token => portal.BindShortcutsAsync(
                new ObjectPath(session), shortcuts, "",
                new Dictionary<string, object> { ["handle_token"] = token }), BindTimeout).ConfigureAwait(false);
            if (bound is null)
            {
                return;
            }

            string? description = bound.TryGetValue("shortcuts", out object? list) ? FindTriggerDescription(list) : null;
            TriggerDescription = string.IsNullOrEmpty(description) ? trigger : description;
            FileLog.Write(
                $"RoeSnip: GlobalShortcuts bound 'capture' (preferred {trigger}, desktop shows '{description ?? "(none)"}').");
            SetActive(true);
        }
        catch (TimeoutException ex)
        {
            await FallBackToGnomeAsync(modifiers, virtualKey, ex.Message).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            FileLog.Write($"RoeSnip: GlobalShortcuts bind failed: {ex.GetBaseException().Message}");
            SetActive(false);
        }
    }

    /// <summary>Calls a portal method that answers through a Request object's Response signal:
    /// subscribes to the token-predicted path first so the signal cannot be missed. Returns the
    /// results, null after logging a refusal, or throws TimeoutException on no answer.</summary>
    private async Task<IDictionary<string, object>?> RequestAsync(
        Connection connection, Func<string, Task<ObjectPath>> call, TimeSpan timeout)
    {
        string token = "roesnip_" + Guid.NewGuid().ToString("N");
        var expected = new ObjectPath($"/org/freedesktop/portal/desktop/request/{_sender}/{token}");
        var response = new TaskCompletionSource<(uint, IDictionary<string, object>)>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        IDisposable? watcher = null;
        try
        {
            watcher = await connection.CreateProxy<IGlobalShortcutsRequest>(PortalService, expected)
                .WatchResponseAsync(r => response.TrySetResult(r), ex => response.TrySetException(ex))
                .ConfigureAwait(false);

            ObjectPath actual = await call(token).ConfigureAwait(false);
            if (actual.ToString() != expected.ToString())
            {
                watcher.Dispose();
                watcher = await connection.CreateProxy<IGlobalShortcutsRequest>(PortalService, actual)
                    .WatchResponseAsync(r => response.TrySetResult(r), ex => response.TrySetException(ex))
                    .ConfigureAwait(false);
            }

            if (await Task.WhenAny(response.Task, Task.Delay(timeout)).ConfigureAwait(false) != response.Task)
            {
                throw new TimeoutException($"GlobalShortcuts request got no answer within {timeout.TotalSeconds:F0}s");
            }

            var (code, results) = await response.Task.ConfigureAwait(false);
            if (code != 0)
            {
                FileLog.Write(code == 1
                    ? "RoeSnip: GlobalShortcuts request was cancelled (shortcut not allowed)."
                    : $"RoeSnip: GlobalShortcuts request failed (response code {code}).");
                SetActive(false);
                return null;
            }

            return results;
        }
        finally
        {
            watcher?.Dispose();
        }
    }

    /// <summary>trigger_description of our shortcut in the Response's a(sa{sv}) list, read
    /// defensively because the variant's struct shape depends on the Tmds.DBus deserializer.</summary>
    private static string? FindTriggerDescription(object? list)
    {
        if (list is not IEnumerable items)
        {
            return null;
        }

        foreach (object? item in items)
        {
            object? id = null, props = null;
            if (item is ITuple tuple && tuple.Length >= 2)
            {
                id = tuple[0];
                props = tuple[1];
            }
            else if (item is object[] array && array.Length >= 2)
            {
                id = array[0];
                props = array[1];
            }

            if (id?.ToString() == ShortcutId
                && props is IDictionary<string, object> dict
                && dict.TryGetValue("trigger_description", out object? text))
            {
                return text?.ToString();
            }
        }

        return null;
    }

    private async Task CloseSessionAsync()
    {
        string? session = _sessionHandle;
        _sessionHandle = null;
        if (session is null || _connection is null)
        {
            return;
        }

        try
        {
            await _connection.CreateProxy<IGlobalShortcutsSession>(PortalService, new ObjectPath(session))
                .CloseAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            FileLog.Write($"RoeSnip: GlobalShortcuts session close failed: {ex.Message}");
        }
    }

    private void ApplyGnomeKeybinding(uint modifiers, uint virtualKey)
    {
        lock (_gate) // also reached from the portal fallback on a pool thread
        {
            ApplyGnomeKeybindingCore(modifiers, virtualKey);
        }
    }

    private void ApplyGnomeKeybindingCore(uint modifiers, uint virtualKey)
    {
        string? accel = LinuxShortcutFormat.ToGtkAccelerator(modifiers, virtualKey);
        if (accel is null)
        {
            FileLog.Write($"RoeSnip: hotkey virtual key 0x{virtualKey:X} has no GNOME accelerator.");
            SetActive(false);
            return;
        }

        if (_active && accel == _gnomeAccel)
        {
            return; // Register runs on every Settings close; skip the gsettings round trips
        }

        if (GnomeShellHolder(modifiers, virtualKey) is string held)
        {
            FileLog.Write($"RoeSnip: GNOME still binds {accel} to {held}; not adding the RoeSnip keybinding.");
            _gnomeAccel = null;
            SetActive(false);
            return;
        }

        string? appImage = Environment.GetEnvironmentVariable("APPIMAGE");
        string? exe = !string.IsNullOrEmpty(appImage) ? appImage : Environment.ProcessPath;
        string? listText = Gsettings("get", LinuxShortcutFormat.GnomeMediaKeysSchema, "custom-keybindings");
        if (string.IsNullOrEmpty(exe) || listText is null)
        {
            FileLog.Write("RoeSnip: GNOME custom keybinding not written (no executable path or gsettings failed).");
            SetActive(false);
            return;
        }

        var list = LinuxShortcutFormat.ParseStringArray(listText);
        foreach (string path in list)
        {
            if (string.Equals(path.TrimEnd('/'), LinuxShortcutFormat.GnomeRoeSnipPath.TrimEnd('/'), StringComparison.Ordinal))
            {
                continue;
            }

            string schema = LinuxShortcutFormat.GnomeCustomKeybindingSchema + ":" + path;
            string? command = FirstString(Gsettings("get", schema, "command"));
            string? binding = FirstString(Gsettings("get", schema, "binding"));
            if (LinuxShortcutFormat.IsRoeSnipCaptureCommand(command))
            {
                FileLog.Write($"RoeSnip: GNOME custom shortcut {path} already runs '{command}' ({binding}); leaving it.");
                Kind = Mechanism.UserShortcut;
                TriggerDescription = binding;
                _gnomeAccel = accel;
                SetActive(true);
                return;
            }

            if (LinuxShortcutFormat.AcceleratorsEqual(binding, accel))
            {
                FileLog.Write(
                    $"RoeSnip: GNOME custom shortcut {path} already uses {accel} ('{command}'); not adding the RoeSnip keybinding.");
                SetActive(false);
                return;
            }
        }

        string ours = LinuxShortcutFormat.GnomeCustomKeybindingSchema + ":" + LinuxShortcutFormat.GnomeRoeSnipPath;
        string captureCommand = LinuxShortcutFormat.CaptureCommand(exe);
        bool ok = Gsettings("set", ours, "name", LinuxShortcutFormat.GVariantString("RoeSnip capture")) is not null
            && Gsettings("set", ours, "command", LinuxShortcutFormat.GVariantString(captureCommand)) is not null
            && Gsettings("set", ours, "binding", LinuxShortcutFormat.GVariantString(accel)) is not null;

        var merged = LinuxShortcutFormat.MergeKeybindingList(list, LinuxShortcutFormat.GnomeRoeSnipPath);
        if (ok && merged is not null)
        {
            ok = Gsettings("set", LinuxShortcutFormat.GnomeMediaKeysSchema, "custom-keybindings",
                LinuxShortcutFormat.FormatStringArray(merged)) is not null;
        }

        FileLog.Write(ok
            ? $"RoeSnip: GNOME custom keybinding 'roesnip' set: {accel} -> {captureCommand}"
            : "RoeSnip: GNOME custom keybinding 'roesnip' could not be written (gsettings set failed).");
        Kind = Mechanism.GnomeKeybinding;
        TriggerDescription = accel;
        _gnomeAccel = ok ? accel : null;
        SetActive(ok);
    }

    private static string? FirstString(string? gvariant) =>
        LinuxShortcutFormat.ParseStringArray(gvariant).FirstOrDefault();

    private static bool IsGnome() => GnomePrintScreenTakeover.IsGnomeSession();

    private static string? Gsettings(params string[] args) => GnomePrintScreenTakeover.RunGsettings(args);

    private void SetActive(bool active)
    {
        if (_active == active)
        {
            return;
        }

        _active = active;
        StatusChanged?.Invoke();
    }

    private void CloseConnection()
    {
        _activatedWatch?.Dispose();
        _activatedWatch = null;
        _portal = null;
        _connection = null; // shared with the capture portals: never disposed here
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        try
        {
            CloseSessionAsync().Wait(TimeSpan.FromSeconds(1));
        }
        catch
        {
            // best effort; the session ends with the connection anyway
        }

        CloseConnection();
    }
}
#endif
