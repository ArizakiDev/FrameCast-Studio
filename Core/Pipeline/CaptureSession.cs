using FrameCastStudio.Core.Audio;
using FrameCastStudio.Core.Capture;
using FrameCastStudio.Core.Encoding;
using FrameCastStudio.Core.Recording;
using FrameCastStudio.Core.Streaming;
using FrameCastStudio.Core.Telemetry;
using System.Runtime.InteropServices;
using Windows.Graphics.Capture;

namespace FrameCastStudio.Core.Pipeline;

public sealed class CaptureSession : IAsyncDisposable
{
    public TelemetryService Telemetry { get; } = new();
    private D3DCaptureService? _cap; private HardwareEncoder? _enc; private AudioMixer? _audio; private RtmpClient? _rtmp;
    private PreviewRenderer? _preview;
    private SessionSettings _cfg = new();
    private bool _hdrSent;

    private volatile Mp4Recorder? _mp4;
    private readonly object _mp4Lock = new();
    private Func<int, long, Mp4Recorder>? _recFactory;
    private int _part = 1; private long _splitBytes; private bool _splitFailed;

    private long _t0, _pauseStart, _pauseAccum, _lastVideoRel = -1, _offsetHns;
    private volatile bool _paused, _stopping;
    private readonly object _pauseLock = new();

    private bool _pktErr; private long _pktCount;
    private AacEncoder? _aac; private bool _aacHdr; private byte[] _s16 = Array.Empty<byte>();
    private Timer? _abrTimer; private int _abrCur, _abrTarget, _abrStable; private long _abrLastDrops;

    public RtmpClient? Rtmp => _rtmp;
    public AudioMixer? Mixer => _audio;
    public string EncoderName => _enc?.AdapterName ?? "Aperçu seul";

    public string? AudioWarning { get; private set; }
    public long LastFrameTick => _cap?.LastFrameTick ?? Environment.TickCount64;
    public bool PreviewEnabled { get => _preview?.Enabled ?? false; set { if (_preview != null) _preview.Enabled = value; } }

    public bool Paused
    {
        get => _paused;
        set
        {
            lock (_pauseLock)
            {
                if (value == _paused) return;
                if (value) _pauseStart = Clock.NowHns(); else _pauseAccum += Clock.NowHns() - _pauseStart;
                _paused = value;
            }
        }
    }

    private static string PartPath(string first, int part)
    {
        if (part <= 1) return first;
        string dir = Path.GetDirectoryName(first) ?? "";
        return Path.Combine(dir, $"{Path.GetFileNameWithoutExtension(first)}_part{part:D3}{Path.GetExtension(first)}");
    }

    private static Mp4Recorder CreateRecorder(string path, int w, int h, SessionSettings cfg, bool hasAudio, long baseHns)
    {
        try
        {
            return new Mp4Recorder(path, w, h, cfg.Fps, cfg.BitrateKbps, hasAudio, cfg.AudioSampleRate, 2, AacEncoder.ClampKbps(cfg.AudioBitrateKbps),
                                   cfg.FragmentedMp4, baseHns, cfg.Codec == Codec.HEVC);
        }
        catch (Exception ex) when (cfg.FragmentedMp4)
        {
            Log.Error("Mp4Recorder fragmenté → repli sur MP4 classique", ex);
            return new Mp4Recorder(path, w, h, cfg.Fps, cfg.BitrateKbps, hasAudio, cfg.AudioSampleRate, 2, AacEncoder.ClampKbps(cfg.AudioBitrateKbps),
                                   false, baseHns, cfg.Codec == Codec.HEVC);
        }
    }

    public async Task StartAsync(GraphicsCaptureItem item, SessionSettings cfg, string? rtmpUrl, string? streamKey,
                                 string? recordPath, IntPtr previewPanel, bool previewVisible, CancellationToken ct)
    {
        _cfg = cfg;
        _offsetHns = (long)(cfg.CaptureOffsetMs * 10_000);
        bool encode = rtmpUrl != null || recordPath != null;
        Log.Write($"StartAsync encode={encode} rec={recordPath} rtmp={(rtmpUrl != null)}");
        if (recordPath != null && cfg.Codec == Codec.AV1)
            throw new NotSupportedException("L'enregistrement MP4 en AV1 n'est pas supporté : choisis H264 ou HEVC dans l'onglet Encodeur.");
        if (rtmpUrl != null && cfg.Codec != Codec.H264)
            throw new NotSupportedException("Le direct RTMP (FLV) ne supporte que H264 : choisis H264 dans l'onglet Encodeur.");

        _cap = new D3DCaptureService();
        _cap.Start(item, cfg.Fps, cfg.CaptureCursor, cfg.ScalePercent, cfg.Bt2020, cfg.DisableWgcBorder, encode);
        var (encW, encH) = _cap.Size;
        var (srcW, srcH) = _cap.SourceSize;

        if (previewPanel != IntPtr.Zero)
        {
            var (pw, ph) = PreviewRenderer.ComputeSize(srcW, srcH);
            _preview = new PreviewRenderer(_cap.Device, previewPanel, srcW, srcH, pw, ph, Math.Max(60, cfg.Fps)) { Enabled = previewVisible };
            _cap.Preview = _preview;
        }
        var cap = _cap;
        Telemetry.Dropped = () => (_rtmp?.FramesDropped ?? 0) + Interlocked.Read(ref cap.DroppedFrames);
        Telemetry.CaptureFrames = () => Interlocked.Read(ref cap.FramesCaptured);
        Telemetry.OutputFrames = () => Interlocked.Read(ref cap.FramesEncoded);
        Telemetry.PreviewFrames = () => _preview?.FramesPresented ?? 0;

        if (encode)
        {
            _enc = new HardwareEncoder(_cap.Device, cfg.ToEncoderSettings(encW, encH));
            Log.Write("Encodeur : initialisation…");
            _enc.Initialize();
            Log.Write("Encodeur : OK " + _enc.AdapterName);
            _enc.PacketReady += OnPacket;
        }

        if (rtmpUrl != null && streamKey != null)
        {
            _rtmp = new RtmpClient(cfg.RtmpQueueCapacity, cfg.RtmpChunkSize);
            await _rtmp.ConnectAsync(rtmpUrl, streamKey, ct);
            Telemetry.BytesSent = () => _rtmp.BytesSent;
            if (cfg.AdaptiveBitrate && cfg.Rate != RateControl.CQP && _enc != null)
            {
                _abrCur = _abrTarget = cfg.BitrateKbps; _abrLastDrops = _rtmp.FramesDropped;
                _abrTimer = new Timer(AbrTick, null, 2000, 2000);
            }
        }

        _t0 = Clock.NowHns();

        bool wantAudio = cfg.AudioLoopback || cfg.AudioMic;
        if (wantAudio && (encode || cfg.MeterWhenIdle))
        {
            try
            {
                _audio = new AudioMixer();
                _audio.PcmReady += OnPcm;
                _audio.Start(cfg.AudioLoopback, cfg.AudioMic, cfg.AudioBufferMs, cfg.LoopbackDeviceId, cfg.MicDeviceId, cfg.AudioSampleRate, cfg.Mixer);
            }
            catch (Exception ex)
            {
                Log.Error("Audio", ex);
                AudioWarning = ex.Message;
                try { _audio?.Dispose(); } catch { }
                _audio = null;
            }
        }

        if (_audio != null && _rtmp != null)
        {
            try
            {
                _aac = new AacEncoder(_audio.SampleRate, cfg.AudioBitrateKbps);
                _aac.FrameReady += (raw, ms) =>
                {
                    var r = _rtmp;
                    if (r != null && r.State == RtmpState.Publishing) r.Enqueue(8, ms, FlvMuxer.BuildAacTag(raw, false));
                };
            }
            catch (Exception ex)
            {
                Log.Error("AAC (live)", ex);
                AudioWarning = "audio du live indisponible : " + ex.Message;
                try { _aac?.Dispose(); } catch { }
                _aac = null;
            }
        }

        if (recordPath != null)
        {
            bool hasAudio = _audio != null;
            _splitBytes = (long)(Math.Max(0.1, cfg.SplitSizeGb) * 1024 * 1024 * 1024);
            _recFactory = (part, baseHns) => CreateRecorder(PartPath(recordPath, part), encW, encH, cfg, hasAudio, baseHns);
            _mp4 = _recFactory(1, 0);
        }

        Log.Write("Session prête, frames abonnées");
        if (encode) _cap.FrameReady += OnFrame;
        Telemetry.Start();
    }

    private void OnFrame(GpuFrame f)
    {
        if (_stopping || _paused || _enc == null) { f.Release(); return; }
        long rel = f.TimestampHns - _t0 - _pauseAccum + _offsetHns;
        if (rel < 0 || rel <= _lastVideoRel) { f.Release(); return; }
        _lastVideoRel = rel; f.TimestampHns = rel;
        _enc.Encode(f);
    }

    private void OnPcm(ReadOnlyMemory<byte> pcm, long pts)
    {
        var a = _audio;
        if (a == null || _paused || _stopping) return;
        long rel = a.StartHns + pts - _t0 - _pauseAccum;
        if (rel < 0) return;

        var m = _mp4;
        if (m != null)
        {
            try { m.WriteAudioSample(pcm, rel); } catch (Exception ex) { if (!_pktErr) { _pktErr = true; Log.Error("WriteAudio", ex); } }
        }

        var aac = _aac; var r = _rtmp;
        if (aac != null && r != null && r.State == RtmpState.Publishing)
        {
            try
            {
                var f32 = MemoryMarshal.Cast<byte, float>(pcm.Span);
                if (_s16.Length != f32.Length * 2) _s16 = new byte[f32.Length * 2];
                for (int i = 0; i < f32.Length; i++)
                {
                    short v = (short)Math.Clamp((int)MathF.Round(f32[i] * 32767f), short.MinValue, short.MaxValue);
                    _s16[i * 2] = (byte)(v & 0xFF); _s16[i * 2 + 1] = (byte)((v >> 8) & 0xFF);
                }
                if (!_aacHdr) { r.Enqueue(8, 0, FlvMuxer.BuildAacTag(aac.AudioSpecificConfig, true)); _aacHdr = true; }
                aac.Encode(_s16, rel / 10_000);
            }
            catch (Exception ex) { if (!_pktErr) { _pktErr = true; Log.Error("AAC encode", ex); } }
        }
    }

    private void OnPacket(EncodedPacket p)
    {
        try { OnPacketCore(p); }
        catch (Exception ex) { if (!_pktErr) { _pktErr = true; Log.Error("OnPacket", ex); } }
    }

    private void OnPacketCore(EncodedPacket p)
    {
        if (_pktCount++ < 3) Log.Write($"Paquet #{_pktCount} {p.Data.Length} o key={p.KeyFrame}", 1);
        uint ms = (uint)Math.Max(0, p.PtsHns / 10_000);
        var span = p.Data.Span;
        Telemetry.AddBytes(span.Length);
        if (_rtmp != null && _rtmp.State == RtmpState.Publishing)
        {
            if (!_hdrSent && FlvMuxer.ExtractParams(span) is { } ps)
            {
                _rtmp.Enqueue(9, 0, FlvMuxer.BuildAvcSequenceHeader(ps.sps, ps.pps)); _hdrSent = true;
            }
            if (_hdrSent) _rtmp.Enqueue(9, ms, FlvMuxer.BuildVideoTag(span, p.KeyFrame));
        }

        var m = _mp4;
        if (m == null) return;

        if (_cfg.SplitEnabled && !_splitFailed && p.KeyFrame && m.BytesWritten >= _splitBytes && _recFactory != null)
        {
            try
            {
                var next = _recFactory(_part + 1, p.PtsHns);
                Mp4Recorder? old;
                lock (_mp4Lock) { old = _mp4; _mp4 = next; }
                _part++; m = next;
                Log.Write($"Découpage : nouveau fichier (partie {_part})");
                try { old?.Dispose(); } catch (Exception ex) { Log.Error("Finalisation partie précédente", ex); }
            }
            catch (Exception ex) { _splitFailed = true; Log.Error("Découpage désactivé", ex); }
        }
        m.WriteVideoSample(span, p.PtsHns, p.DurHns, p.KeyFrame);
    }

    private void AbrTick(object? _)
    {
        try
        {
            var r = _rtmp; var enc = _enc;
            if (r == null || enc == null || _stopping) return;
            long drops = r.FramesDropped; bool congested = drops > _abrLastDrops; _abrLastDrops = drops;
            int k = _abrCur;
            if (congested) { k = Math.Max(_abrTarget * 30 / 100, (int)(_abrCur * 0.85)); _abrStable = 0; }
            else if (++_abrStable >= 5 && _abrCur < _abrTarget) { k = Math.Min(_abrTarget, (int)(_abrCur * 1.10) + 50); _abrStable = 0; }
            if (k != _abrCur) { _abrCur = k; enc.SetBitrate(k); Log.Write($"Débit adaptatif → {k} kbps"); }
        }
        catch { }
    }

    public async ValueTask DisposeAsync()
    {
        _stopping = true;
        try { _abrTimer?.Dispose(); } catch { }

        if (_cap != null) _cap.Preview = null;
        _preview?.Dispose();
        _cap?.StopCapture();

        await Task.Run(() =>
        {
            try { _audio?.Dispose(); } catch { }
            try { _aac?.Dispose(); } catch { }
            try { _enc?.Drain(2500); } catch (Exception ex) { Log.Error("Drain encodeur", ex); }
            Mp4Recorder? m;
            lock (_mp4Lock) { m = _mp4; _mp4 = null; }
            try { m?.Dispose(); } catch (Exception ex) { Log.Error("Finalisation MP4", ex); }
            try { _enc?.Dispose(); } catch { }
            try { _cap?.Dispose(); } catch { }
        });

        if (_rtmp != null) await _rtmp.DisposeAsync();
        Telemetry.Dispose();
    }
}
