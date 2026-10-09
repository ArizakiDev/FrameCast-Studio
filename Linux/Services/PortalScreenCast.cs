using System.Runtime.CompilerServices;
using Tmds.DBus;

namespace FrameCastStudio.Linux.Services;

[DBusInterface("org.freedesktop.portal.ScreenCast")]
public interface IScreenCastPortal : IDBusObject
{
    Task<ObjectPath> CreateSessionAsync(IDictionary<string, object> options);
    Task<ObjectPath> SelectSourcesAsync(ObjectPath sessionHandle, IDictionary<string, object> options);
    Task<ObjectPath> StartAsync(ObjectPath sessionHandle, string parentWindow, IDictionary<string, object> options);
}

[DBusInterface("org.freedesktop.portal.Request")]
public interface IPortalRequest : IDBusObject
{
    Task<IDisposable> WatchResponseAsync(Action<(uint response, IDictionary<string, object> results)> handler,
        Action<Exception>? onError = null);
}

[DBusInterface("org.freedesktop.portal.Session")]
public interface IPortalSession : IDBusObject
{
    Task CloseAsync();
}

public sealed record PortalStream(uint NodeId, int Width, int Height, string? RestoreToken);

/// <summary>
/// Capture d'écran Wayland (et X11 moderne) via xdg-desktop-portal : le système affiche son propre sélecteur d'écran,
/// puis fournit un flux PipeWire identifié par un numéro de nœud.
/// </summary>
public sealed class PortalScreenCast : IAsyncDisposable
{
    private const string Service = "org.freedesktop.portal.Desktop";
    private static readonly ObjectPath PortalPath = new("/org/freedesktop/portal/desktop");

    private Connection? _conn;
    private string _localName = "";
    private ObjectPath? _session;

    public async Task<PortalStream> StartAsync(bool cursor, string? restoreToken, CancellationToken ct = default)
    {
        // Connexion dédiée à cette session : le portail ferme la session si la connexion tombe.
        var conn = new Connection(Address.Session ?? throw new InvalidOperationException("Pas de bus de session D-Bus (DBUS_SESSION_BUS_ADDRESS vide)."));
        _conn = conn;
        object info = await conn.ConnectAsync();
        _localName = info.GetType().GetProperty("LocalName")?.GetValue(info) as string
                     ?? conn.GetType().GetProperty("LocalName")?.GetValue(conn) as string
                     ?? throw new InvalidOperationException("Nom D-Bus de la connexion introuvable.");
        var portal = conn.CreateProxy<IScreenCastPortal>(Service, PortalPath);

        // 1. CreateSession
        var o1 = new Dictionary<string, object> { ["session_handle_token"] = "fc" + Guid.NewGuid().ToString("N")[..10] };
        var r1 = await RequestAsync(o => portal.CreateSessionAsync(o), o1, ct);
        string? sh = r1.TryGetValue("session_handle", out var shObj) ? shObj?.ToString() : null;
        if (string.IsNullOrEmpty(sh)) throw new InvalidOperationException("Le portail n'a pas renvoyé de session.");
        _session = new ObjectPath(sh);

        // 2. SelectSources (écran entier ou fenêtre, curseur intégré, jeton de mémorisation)
        var o2 = new Dictionary<string, object>
        {
            ["types"] = 3u,
            ["multiple"] = false,
            ["cursor_mode"] = cursor ? 2u : 1u,
            ["persist_mode"] = 2u,
        };
        if (!string.IsNullOrEmpty(restoreToken)) o2["restore_token"] = restoreToken;
        await RequestAsync(o => portal.SelectSourcesAsync(_session.Value, o), o2, ct);

        // 3. Start : le portail affiche la fenêtre de sélection (sauf si le jeton mémorisé est encore valable)
        var r3 = await RequestAsync(o => portal.StartAsync(_session.Value, "", o), new Dictionary<string, object>(), ct);

        string? newToken = r3.TryGetValue("restore_token", out var tk) ? tk?.ToString() : null;
        if (!r3.TryGetValue("streams", out var streamsObj) || streamsObj is not System.Collections.IEnumerable streams)
            throw new InvalidOperationException("Le portail n'a fourni aucun flux vidéo.");

        foreach (var st in streams)
        {
            object? idObj = Item(st, 0);
            if (idObj == null) continue;
            uint node = Convert.ToUInt32(idObj);
            int w = 0, h = 0;
            if (Item(st, 1) is IDictionary<string, object> props && props.TryGetValue("size", out var sz) && sz != null)
            {
                try { w = Convert.ToInt32(Item(sz, 0)); h = Convert.ToInt32(Item(sz, 1)); } catch { }
            }
            return new PortalStream(node, w, h, newToken);
        }
        throw new InvalidOperationException("Aucun flux vidéo dans la réponse du portail.");
    }

    /// <summary>Lit un champ d'une structure D-Bus quelle que soit sa représentation (ValueTuple, tableau d'objets...).</summary>
    private static object? Item(object? o, int i)
    {
        if (o == null) return null;
        if (o is ITuple t) return i < t.Length ? t[i] : null;
        if (o is object[] arr) return i < arr.Length ? arr[i] : null;
        var type = o.GetType();
        var f = type.GetField("Item" + (i + 1));
        if (f != null) return f.GetValue(o);
        var p = type.GetProperty("Item" + (i + 1));
        return p?.GetValue(o);
    }

    private async Task<IDictionary<string, object>> RequestAsync(
        Func<IDictionary<string, object>, Task<ObjectPath>> call, Dictionary<string, object> options, CancellationToken ct)
    {
        string token = "fc" + Guid.NewGuid().ToString("N")[..12];
        options["handle_token"] = token;
        string sender = _localName.TrimStart(':').Replace('.', '_');
        var reqPath = new ObjectPath($"/org/freedesktop/portal/desktop/request/{sender}/{token}");

        var tcs = new TaskCompletionSource<(uint, IDictionary<string, object>)>(TaskCreationOptions.RunContinuationsAsynchronously);
        var req = _conn!.CreateProxy<IPortalRequest>(Service, reqPath);
        using var sub = await req.WatchResponseAsync(r => tcs.TrySetResult(r), ex => tcs.TrySetException(ex));
        await call(options);

        using var reg = ct.Register(() => tcs.TrySetCanceled());
        var finished = await Task.WhenAny(tcs.Task, Task.Delay(TimeSpan.FromMinutes(3), ct));
        if (finished != tcs.Task) throw new TimeoutException("Le portail n'a pas répondu (sélection d'écran non validée ?).");

        var (code, results) = await tcs.Task;
        if (code == 1) throw new OperationCanceledException("Partage d'écran annulé.");
        if (code != 0) throw new InvalidOperationException($"Le portail a refusé la demande (code {code}).");
        return results;
    }

    public async Task CloseAsync()
    {
        try
        {
            if (_conn != null && _session != null)
                await _conn.CreateProxy<IPortalSession>(Service, _session.Value).CloseAsync();
        }
        catch { }
        _session = null;
        try { _conn?.Dispose(); } catch { }
        _conn = null;
    }

    public async ValueTask DisposeAsync() => await CloseAsync();
}
