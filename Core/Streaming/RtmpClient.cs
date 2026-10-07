using System.Buffers.Binary;
using System.Net.Security;
using System.Net.Sockets;
using System.Threading.Channels;

namespace FrameCastStudio.Core.Streaming;

public enum RtmpState { Idle, Connecting, Handshake, Publishing, Error, Closed }

public sealed class RtmpClient : IAsyncDisposable
{
    private Stream _net = null!; private TcpClient _tcp = new() { NoDelay = true };
    private readonly Channel<(byte type, uint ts, byte[] payload, int len)> _q;
    private int _chunkSize = 128;
    private readonly int _desiredChunkSize;
    private const int Stream1 = 1;
    public RtmpState State { get; private set; }
    public long BytesSent, FramesDropped;

    public int QueueCapacity { get; }
    public event Action<RtmpState>? StateChanged;

    public RtmpClient(int queueCapacity = 240, int chunkSize = 4096)
    {
        QueueCapacity = Math.Clamp(queueCapacity, 8, 4096);
        _desiredChunkSize = Math.Clamp(chunkSize, 128, 65536);
        _q = Channel.CreateBounded<(byte, uint, byte[], int)>(
            new BoundedChannelOptions(QueueCapacity) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });
    }

    public async Task ConnectAsync(string url, string streamKey, CancellationToken ct)
    {
        var u = new Uri(url); bool tls = u.Scheme == "rtmps";
        int port = u.Port > 0 ? u.Port : tls ? 443 : 1935;
        var app = u.AbsolutePath.Trim('/');
        SetState(RtmpState.Connecting);
        await _tcp.ConnectAsync(u.Host, port, ct);
        _net = _tcp.GetStream();
        if (tls) { var ssl = new SslStream(_net); await ssl.AuthenticateAsClientAsync(u.Host); _net = ssl; }

        SetState(RtmpState.Handshake);
        var c0c1 = new byte[1537]; c0c1[0] = 3; Random.Shared.NextBytes(c0c1.AsSpan(9));
        await _net.WriteAsync(c0c1, ct);
        var s = new byte[1537 + 1536]; await ReadExact(s, ct);
        await _net.WriteAsync(s.AsMemory(1, 1536), ct);

        var chunkMsg = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(chunkMsg, (uint)_desiredChunkSize);
        await WriteMessage(2, 1, 0, 0, chunkMsg, ct);
        _chunkSize = _desiredChunkSize;

        await SendCommand(3, 0, Amf("connect", 1.0, new (string, object)[] {
            ("app", app), ("type", "nonprivate"), ("flashVer", "FMLE/3.0"), ("tcUrl", url) }), ct);
        await Task.Delay(150, ct);
        await SendCommand(3, 0, Amf("createStream", 2.0, null), ct);
        await Task.Delay(150, ct);
        await SendCommand(4, Stream1, Amf("publish", 0.0, null, streamKey, "live"), ct);
        SetState(RtmpState.Publishing);
        _ = Task.Run(() => SendLoop(ct), ct);
    }

    public void Enqueue(byte type, uint tsMs, ReadOnlySpan<byte> payload)
    {
        var buf = System.Buffers.ArrayPool<byte>.Shared.Rent(payload.Length); payload.CopyTo(buf);
        if (!_q.Writer.TryWrite((type, tsMs, buf, payload.Length))) Interlocked.Increment(ref FramesDropped);
    }

    private async Task SendLoop(CancellationToken ct)
    {
        try
        {
            await foreach (var (type, ts, buf, len) in _q.Reader.ReadAllAsync(ct))
            {
                await WriteMessage(type == 9 ? 6 : 7, type, ts, Stream1, buf.AsMemory(0, len), ct);
                System.Buffers.ArrayPool<byte>.Shared.Return(buf);
            }
        }
        catch { SetState(RtmpState.Error); }
    }

    private async Task WriteMessage(int csid, byte type, uint ts, int streamId, ReadOnlyMemory<byte> data, CancellationToken ct)
    {
        var hdr = new byte[12]; hdr[0] = (byte)csid;
        hdr[1] = (byte)(ts >> 16); hdr[2] = (byte)(ts >> 8); hdr[3] = (byte)ts;
        hdr[4] = (byte)(data.Length >> 16); hdr[5] = (byte)(data.Length >> 8); hdr[6] = (byte)data.Length;
        hdr[7] = type; BinaryPrimitives.WriteInt32LittleEndian(hdr.AsSpan(8), streamId);
        await _net.WriteAsync(hdr, ct);
        int off = 0;
        while (off < data.Length)
        {
            int n = Math.Min(_chunkSize, data.Length - off);
            if (off > 0) await _net.WriteAsync(new[] { (byte)(0xC0 | csid) }, ct);
            await _net.WriteAsync(data.Slice(off, n), ct); off += n;
        }
        BytesSent += data.Length;
    }

    private Task SendCommand(int csid, int sid, byte[] amf, CancellationToken ct) => WriteMessage(csid, 20, 0, sid, amf, ct);

    private static byte[] Amf(string name, double tx, (string, object)[]? obj, params string[] extra)
    {
        var m = new MemoryStream();
        void Str(string s) { m.WriteByte(2); m.WriteByte((byte)(s.Length >> 8)); m.WriteByte((byte)s.Length); m.Write(System.Text.Encoding.UTF8.GetBytes(s)); }
        void Num(double d) { m.WriteByte(0); var b = new byte[8]; BinaryPrimitives.WriteDoubleBigEndian(b, d); m.Write(b); }
        Str(name); Num(tx);
        if (obj != null)
        {
            m.WriteByte(3);
            foreach (var (k, v) in obj) { m.WriteByte((byte)(k.Length >> 8)); m.WriteByte((byte)k.Length); m.Write(System.Text.Encoding.UTF8.GetBytes(k)); Str((string)v); }
            m.Write(new byte[] { 0, 0, 9 });
        }
        else m.WriteByte(5);
        foreach (var e in extra) Str(e);
        return m.ToArray();
    }

    private async Task ReadExact(byte[] b, CancellationToken ct)
    { int o = 0; while (o < b.Length) { int n = await _net.ReadAsync(b.AsMemory(o), ct); if (n == 0) throw new IOException(); o += n; } }

    private void SetState(RtmpState s) { State = s; StateChanged?.Invoke(s); }
    public async ValueTask DisposeAsync() { _q.Writer.TryComplete(); SetState(RtmpState.Closed); _tcp.Dispose(); await Task.CompletedTask; }
}
