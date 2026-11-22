using RoeSnip.Core.Diagnostics;
using RoeSnip.Core.Updates;
using Tmds.DBus;

namespace RoeSnip.Platform.Linux;

/// <summary>xdg-desktop-portal 1.19+: lets a host (non-sandboxed) app name its app id. Must be the
/// first portal call on the connection; the id needs an installed &lt;id&gt;.desktop.</summary>
[DBusInterface("org.freedesktop.host.portal.Registry")]
public interface IHostPortalRegistry : IDBusObject
{
    Task RegisterAsync(string appId, IDictionary<string, object> options);
}

/// <summary>The systemd user manager, for moving this process into its own app scope.</summary>
[DBusInterface("org.freedesktop.systemd1.Manager")]
public interface ISystemdManager : IDBusObject
{
    Task<ObjectPath> StartTransientUnitAsync(string name, string mode, (string, object)[] properties,
        (string, (string, object)[])[] aux);
}

/// <summary>The session-bus connection every portal call in the process uses. The portal names a
/// host app in its dialogs by the app id registered on the calling connection, else by the systemd
/// scope the process runs in (a scope inherited from whatever launched RoeSnip). So the first use
/// writes net.roelite.roesnip.desktop if missing, registers that id, and when the portal has no
/// Registry moves the process into its own app scope - all before any other portal call.</summary>
public sealed class PortalConnection
{
    public const string PortalService = "org.freedesktop.portal.Desktop";
    public static readonly ObjectPath PortalPath = new("/org/freedesktop/portal/desktop");
    public const string AppId = DesktopIntegrationFiles.LinuxAppId;

    private static readonly SemaphoreSlim s_gate = new(1, 1);
    private static PortalConnection? s_current;
    private static int s_identityDone;

    private PortalConnection(Connection connection, string senderPart)
    {
        Connection = connection;
        SenderPart = senderPart;
    }

    public Connection Connection { get; }

    /// <summary>The unique bus name as it appears in portal request/session paths (":1.42" -> "1_42").</summary>
    public string SenderPart { get; }

    public bool IsConnected { get; private set; } = true;

    /// <summary>Raised once when this connection drops; portal sessions on it are gone.</summary>
    public event Action<string>? Lost;

    /// <summary>The shared connection, opening and registering it on first use (or after a drop).
    /// Throws when there is no session bus or it cannot connect.</summary>
    public static async Task<PortalConnection> GetAsync()
    {
        var current = Volatile.Read(ref s_current);
        if (current is { IsConnected: true })
        {
            return current;
        }

        await s_gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (s_current is { IsConnected: true })
            {
                return s_current;
            }

            string? address = Address.Session;
            if (string.IsNullOrEmpty(address))
            {
                throw new InvalidOperationException("no D-Bus session bus (DBUS_SESSION_BUS_ADDRESS unset)");
            }

            bool wroteEntry = EnsureDesktopEntry();
            var (connection, sender) = await ConnectAsync(address).ConfigureAwait(false);
            string? failure = await RegisterAsync(connection, wroteEntry).ConfigureAwait(false);
            bool log = Interlocked.Exchange(ref s_identityDone, 1) == 0;
            if (failure is null)
            {
                if (log) FileLog.Write($"RoeSnip: portal app id registered as {AppId}.");
            }
            else
            {
                if (log) FileLog.Write($"RoeSnip: portal app id not registered ({failure}).");
                if (await MoveToOwnScopeAsync(connection, log).ConfigureAwait(false))
                {
                    // The failed Register may have pinned the old identity to this sender; start clean.
                    connection.Dispose();
                    (connection, sender) = await ConnectAsync(address).ConfigureAwait(false);
                }
            }

            var shared = new PortalConnection(connection, sender);
            connection.StateChanged += (_, e) =>
            {
                if (e.State is ConnectionState.Disconnecting or ConnectionState.Disconnected && shared.IsConnected)
                {
                    shared.IsConnected = false;
                    string why = e.DisconnectReason?.Message ?? "the session bus connection closed";
                    FileLog.Write($"RoeSnip: portal connection lost ({why}).");
                    shared.Lost?.Invoke(why);
                }
            };
            Volatile.Write(ref s_current, shared);
            return shared;
        }
        finally
        {
            s_gate.Release();
        }
    }

    private static async Task<(Connection, string)> ConnectAsync(string address)
    {
        var connection = new Connection(address);
        try
        {
            var info = await connection.ConnectAsync().ConfigureAwait(false);
            return (connection, info.LocalName.TrimStart(':').Replace('.', '_'));
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    /// <summary>Null on success, else why. A just-written desktop file may not be indexed by the
    /// portal yet, so that case retries once.</summary>
    private static async Task<string?> RegisterAsync(Connection connection, bool wroteEntry)
    {
        var registry = connection.CreateProxy<IHostPortalRegistry>(PortalService, PortalPath);
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                await registry.RegisterAsync(AppId, new Dictionary<string, object>()).ConfigureAwait(false);
                return null;
            }
            catch (Exception ex)
            {
                string why = ex is DBusException dbus ? $"{dbus.ErrorName}: {dbus.ErrorMessage}" : ex.Message;
                if (attempt == 0 && wroteEntry && why.Contains("not found", StringComparison.OrdinalIgnoreCase))
                {
                    await Task.Delay(750).ConfigureAwait(false);
                    continue;
                }
                return why;
            }
        }
    }

    /// <summary>Writes ~/.local/share/applications/net.roelite.roesnip.desktop when missing or
    /// pointing at an old launcher path (see <see cref="DesktopIntegrationFiles.RegistrationEntryExec"/>).
    /// True when it wrote the file. Never throws.</summary>
    private static bool EnsureDesktopEntry()
    {
        try
        {
            Func<string, string?> env = Environment.GetEnvironmentVariable;
            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            string entryPath = UpdateInstallLayout.LinuxMenuEntryPath(env, home);
            string installed = UpdateInstallLayout.LinuxAppImagePath(env, home);
            string? existing = File.Exists(entryPath) ? File.ReadAllText(entryPath) : null;
            string? launcher = UpdateInstallLayout.RunningAppImage(env) ?? Environment.ProcessPath;
            string? exec = DesktopIntegrationFiles.RegistrationEntryExec(existing, installed, File.Exists(installed), launcher);
            if (exec is null)
            {
                return false;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(entryPath)!);
            File.WriteAllText(entryPath, DesktopIntegrationFiles.LinuxMenuEntry(exec, null));
            FileLog.Write($"RoeSnip: wrote {entryPath} (Exec {exec}) so the portal can name RoeSnip.");
            return true;
        }
        catch (Exception ex)
        {
            FileLog.Write($"RoeSnip: could not write the portal desktop entry (non-fatal): {ex.Message}");
            return false;
        }
    }

    /// <summary>Moves this process into app-net.roelite.roesnip-&lt;pid&gt;.scope unless its scope
    /// already carries that app id. Silent when there is no systemd user manager. True when moved.</summary>
    private static async Task<bool> MoveToOwnScopeAsync(Connection connection, bool log)
    {
        string? before = ReadOwnUnit();
        if (DesktopIntegrationFiles.AppIdOfUnit(before) == AppId)
        {
            return false;
        }

        int pid = Environment.ProcessId;
        string unit = DesktopIntegrationFiles.OwnScopeName(pid);
        try
        {
            var manager = connection.CreateProxy<ISystemdManager>("org.freedesktop.systemd1", new ObjectPath("/org/freedesktop/systemd1"));
            await manager.StartTransientUnitAsync(unit, "fail",
                new (string, object)[]
                {
                    ("PIDs", new[] { (uint)pid }),
                    ("Description", "RoeSnip"),
                },
                Array.Empty<(string, (string, object)[])>()).ConfigureAwait(false);
        }
        catch (DBusException ex) when (ex.ErrorName is "org.freedesktop.DBus.Error.ServiceUnknown" or "org.freedesktop.DBus.Error.NameHasNoOwner")
        {
            return false;
        }
        catch (Exception ex)
        {
            if (log) FileLog.Write($"RoeSnip: could not move into systemd scope {unit} (was {before ?? "none"}): {ex.Message}");
            return false;
        }

        // The job runs asynchronously; the portal reads /proc/<pid>/cgroup on the first call.
        for (int i = 0; i < 20 && ReadOwnUnit() != unit; i++)
        {
            await Task.Delay(50).ConfigureAwait(false);
        }
        string? after = ReadOwnUnit();
        if (log) FileLog.Write($"RoeSnip: moved into systemd scope {after ?? "none"} (was {before ?? "none"}) so portal dialogs name RoeSnip.");
        return true;
    }

    private static string? ReadOwnUnit()
    {
        try { return DesktopIntegrationFiles.SystemdUnitOf(File.ReadAllText("/proc/self/cgroup")); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }
}
