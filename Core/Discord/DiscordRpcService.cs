using DiscordRPC;
using DiscordRPC.Logging;

namespace FrameCastStudio.Core.Discord;

public sealed class DiscordRpcService : IDisposable
{
    private const string ClientId = "1557422602916602019";

    private DiscordRpcClient? _client;
    private bool _enabled;
    private string _state = "Idle";
    private bool _disposed;

    public bool IsEnabled => _enabled;

    public void SetEnabled(bool enabled)
    {
        if (_enabled == enabled) return;
        _enabled = enabled;
        if (enabled) Start();
        else Stop();
    }

    private void Start()
    {
        if (_client != null) return;
        try
        {
            _client = new DiscordRpcClient(ClientId)
            {
                Logger = new NullLogger()
            };
            _client.Initialize();
            UpdatePresence(_state);
        }
        catch (Exception ex)
        {
            Log.Write("Discord RPC: failed to start — " + ex.Message);
            _client?.Dispose();
            _client = null;
        }
    }

    private void Stop()
    {
        try { _client?.ClearPresence(); _client?.Dispose(); } catch { }
        _client = null;
    }

    public void UpdatePresence(string state, string? details = null)
    {
        _state = state;
        if (_client is not { IsInitialized: true }) return;
        try
        {
            _client.SetPresence(new RichPresence
            {
                Details = details ?? "FrameCast Studio",
                State = state,
                Assets = new Assets
                {
                    LargeImageKey = "logo",
                    LargeImageText = "FrameCast Studio"
                },
                Timestamps = Timestamps.Now
            });
        }
        catch { }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
    }
}
