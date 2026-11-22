using System.Collections;
using System.Runtime.CompilerServices;
using RoeSnip.Core.Capture;
using Tmds.DBus;

namespace RoeSnip.Platform.Linux;

/// <summary>Tmds.DBus proxy for <c>org.freedesktop.portal.ScreenCast</c>. Public because Tmds.DBus
/// emits its proxy into a dynamic assembly that must see the interface.</summary>
[DBusInterface("org.freedesktop.portal.ScreenCast")]
public interface IPortalScreenCast : IDBusObject
{
    Task<ObjectPath> CreateSessionAsync(IDictionary<string, object> options);
    Task<ObjectPath> SelectSourcesAsync(ObjectPath sessionHandle, IDictionary<string, object> options);
    Task<ObjectPath> StartAsync(ObjectPath sessionHandle, string parentWindow, IDictionary<string, object> options);
    Task<CloseSafeHandle> OpenPipeWireRemoteAsync(ObjectPath sessionHandle, IDictionary<string, object> options);
    Task<T> GetAsync<T>(string prop);
}

/// <summary>Tmds.DBus proxy for <c>org.freedesktop.portal.Session</c>.</summary>
[DBusInterface("org.freedesktop.portal.Session")]
public interface IPortalSession : IDBusObject
{
    Task CloseAsync();
    Task<IDisposable> WatchClosedAsync(Action<IDictionary<string, object>> handler, Action<Exception>? onError = null);
}

/// <summary>A portal Request answered with a non-zero response code (1 = the user cancelled or
/// denied the dialog, 2 = any other error).</summary>
internal sealed class PortalResponseException : Exception
{
    public uint Response { get; }
    public PortalResponseException(string step, uint response)
        : base($"{step} {(response == 1 ? "was cancelled or denied by the user" : $"failed (portal response {response})")}")
        => Response = response;
}

/// <summary>The org.freedesktop.portal.Request dance shared by every ScreenCast step, plus the
/// variant decoding for the streams result.</summary>
internal static class ScreenCastPortal
{
    public const string Service = "org.freedesktop.portal.Desktop";
    public static readonly ObjectPath DesktopPath = new("/org/freedesktop/portal/desktop");

    public const uint SourceMonitor = 1;
    public const uint CursorHidden = 1;
    public const uint CursorEmbedded = 2;
    public const uint CursorMetadata = 4;
    public const uint PersistUntilRevoked = 2;

    /// <summary>Subscribes to the predicted request path first (no signal race), issues the call,
    /// re-subscribes if an old portal minted its own path, and returns the Response results.</summary>
    public static async Task<IDictionary<string, object>> RequestAsync(
        Connection connection, string senderPart, string step,
        Func<string, Task<ObjectPath>> call, TimeSpan timeout)
    {
        string token = "roesnip_" + Guid.NewGuid().ToString("N");
        var expected = new ObjectPath($"/org/freedesktop/portal/desktop/request/{senderPart}/{token}");
        var response = new TaskCompletionSource<(uint Response, IDictionary<string, object> Results)>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        IDisposable? watcher = null;
        try
        {
            watcher = await connection.CreateProxy<IPortalRequest>(Service, expected)
                .WatchResponseAsync(r => response.TrySetResult(r), ex => response.TrySetException(ex))
                .ConfigureAwait(false);

            ObjectPath actual = await call(token).ConfigureAwait(false);
            if (actual.ToString() != expected.ToString())
            {
                watcher.Dispose();
                watcher = await connection.CreateProxy<IPortalRequest>(Service, actual)
                    .WatchResponseAsync(r => response.TrySetResult(r), ex => response.TrySetException(ex))
                    .ConfigureAwait(false);
            }

            if (await Task.WhenAny(response.Task, Task.Delay(timeout)).ConfigureAwait(false) != response.Task)
            {
                throw new TimeoutException($"{step} got no portal response within {timeout.TotalSeconds:F0}s");
            }
            var (code, results) = await response.Task.ConfigureAwait(false);
            if (code != 0)
            {
                throw new PortalResponseException(step, code);
            }
            return results;
        }
        finally
        {
            watcher?.Dispose();
        }
    }

    /// <summary>Decodes the Start result's <c>streams</c> (signature <c>a(ua{sv})</c>).</summary>
    public static List<ScreenCastStreamGeometry> ParseStreams(object? value)
    {
        var list = new List<ScreenCastStreamGeometry>();
        if (value is not IEnumerable items) return list;
        foreach (var item in items)
        {
            var parts = Elements(item);
            if (parts.Count < 2) continue;
            uint node = Convert.ToUInt32(parts[0]);
            int x = 0, y = 0, w = 0, h = 0;
            if (parts[1] is IDictionary<string, object> props)
            {
                if (props.TryGetValue("position", out var pos))
                {
                    var p = Elements(pos);
                    if (p.Count >= 2) { x = Convert.ToInt32(p[0]); y = Convert.ToInt32(p[1]); }
                }
                if (props.TryGetValue("size", out var size))
                {
                    var s = Elements(size);
                    if (s.Count >= 2) { w = Convert.ToInt32(s[0]); h = Convert.ToInt32(s[1]); }
                }
            }
            list.Add(new ScreenCastStreamGeometry(node, x, y, w, h));
        }
        return list;
    }

    /// <summary>A D-Bus struct as Tmds.DBus hands it back inside a variant: a ValueTuple, an
    /// object[] or a plain object with fields, depending on version.</summary>
    private static List<object?> Elements(object? value)
    {
        var result = new List<object?>();
        switch (value)
        {
            case null:
                return result;
            case ITuple tuple:
                for (int i = 0; i < tuple.Length; i++) result.Add(tuple[i]);
                return result;
            case object[] array:
                result.AddRange(array);
                return result;
            default:
                foreach (var f in value.GetType().GetFields()) result.Add(f.GetValue(value));
                return result;
        }
    }
}
