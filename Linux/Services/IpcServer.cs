using System.Net.Sockets;
using System.Text;

namespace FrameCastStudio.Linux.Services;

/// <summary>
/// Instance unique + commandes externes : <c>FrameCastStudio --toggle-record</c> envoie l'ordre à l'application déjà lancée.
/// C'est la solution universelle pour les raccourcis globaux (Wayland n'autorise pas les raccourcis capturés par une appli) :
/// l'utilisateur lie la commande à une touche dans les réglages de son bureau.
/// </summary>
public sealed class IpcServer : IDisposable
{
    public static string SocketPath => Path.Combine(Paths.RuntimeDir, $"framecaststudio-{Environment.UserName}.sock");

    private Socket? _listener;
    private readonly CancellationTokenSource _cts = new();

    public event Action<string>? Command;

    public static bool TrySend(string command)
    {
        try
        {
            using var s = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            s.SendTimeout = 1500; s.ReceiveTimeout = 1500;
            s.Connect(new UnixDomainSocketEndPoint(SocketPath));
            s.Send(Encoding.UTF8.GetBytes(command + "\n"));
            return true;
        }
        catch { return false; }
    }

    public bool Start()
    {
        try
        {
            try { if (File.Exists(SocketPath)) File.Delete(SocketPath); } catch { }
            _listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            _listener.Bind(new UnixDomainSocketEndPoint(SocketPath));
            try { File.SetUnixFileMode(SocketPath, UnixFileMode.UserRead | UnixFileMode.UserWrite); } catch { }
            _listener.Listen(8);
            _ = Task.Run(AcceptLoop);
            return true;
        }
        catch (Exception ex) { Log.Error("Socket de commande", ex); return false; }
    }

    private async Task AcceptLoop()
    {
        while (!_cts.IsCancellationRequested && _listener != null)
        {
            try
            {
                var c = await _listener.AcceptAsync(_cts.Token);
                _ = Task.Run(async () =>
                {
                    using (c)
                    {
                        var buf = new byte[256];
                        int n = await c.ReceiveAsync(buf, SocketFlags.None);
                        string cmd = Encoding.UTF8.GetString(buf, 0, n).Trim();
                        if (cmd.Length > 0) Command?.Invoke(cmd);
                    }
                });
            }
            catch (OperationCanceledException) { break; }
            catch { await Task.Delay(100); }
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _listener?.Close(); } catch { }
        try { if (File.Exists(SocketPath)) File.Delete(SocketPath); } catch { }
    }
}
