using System.Diagnostics;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace FrameCastStudio.Core.Audio;

public sealed class AudioMixer : IDisposable
{
    public int SampleRate { get; private set; } = 48000;
    public WaveFormat Target => WaveFormat.CreateIeeeFloatWaveFormat(SampleRate, 2);
    public MixerLevels Levels { get; } = new();

    public long StartHns { get; private set; }
    public bool MonitorBlocked { get; private set; }
    public event Action<ReadOnlyMemory<byte>, long>? PcmReady;

    private WasapiCapture? _loop, _mic;
    private Strip? _sLoop, _sMic;
    private Thread? _thread;
    private volatile bool _run;
    private volatile MixerSettings _cfg = new();
    private string? _loopDeviceId;
    private readonly object _monLock = new();
    private WasapiOut? _monOut;
    private volatile BufferedWaveProvider? _monBuf;
    private string? _monId;
    private bool _errLogged;
    private readonly Ducker _ducker = new();
    private readonly Limiter _limiter = new();

    private sealed class Strip
    {
        public BufferedWaveProvider Buf = null!;
        public ISampleProvider Src = null!;
        public float[] In = Array.Empty<float>();
        public int BytesPerSec;
        public readonly Biquad2 Hp = new();
        public readonly DelayLine Dl = new();
        public readonly Gate Gt = new();
        public readonly Compressor Cp = new();
    }

    public void Start(bool loopback, bool mic, int latencyMs, string? loopbackDeviceId, string? micDeviceId,
                      int sampleRate, MixerSettings cfg)
    {
        SampleRate = sampleRate is 44100 or 48000 ? sampleRate : 48000;
        _cfg = cfg;
        using (var e = new MMDeviceEnumerator())
        {
            if (loopback)
            {
                var dev = AudioDeviceService.Resolve(e, loopbackDeviceId, DataFlow.Render, Role.Multimedia);
                _loopDeviceId = dev?.ID;
                _loop = dev != null ? new WasapiLoopbackCapture(dev) : new WasapiLoopbackCapture();
                _sLoop = MakeStrip(_loop);
            }
            if (mic)
            {
                var dev = AudioDeviceService.Resolve(e, micDeviceId, DataFlow.Capture, Role.Communications);
                _mic = dev != null
                    ? new WasapiCapture(dev, true, latencyMs)
                    : new WasapiCapture(e.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications), true, latencyMs);
                _sMic = MakeStrip(_mic);
            }
        }
        int n = (SampleRate / 50) * 2;
        if (_sLoop != null) _sLoop.In = new float[n];
        if (_sMic != null) _sMic.In = new float[n];

        StartHns = Clock.NowHns();
        _run = true;
        _loop?.StartRecording(); _mic?.StartRecording();
        _thread = new Thread(Run) { IsBackground = true, Priority = ThreadPriority.AboveNormal, Name = "AudioMixer" };
        _thread.Start();
        ApplyMonitor(cfg);
    }

    public void Update(MixerSettings cfg)
    {
        _cfg = cfg;
        ApplyMonitor(cfg);
    }

    private Strip MakeStrip(WasapiCapture cap)
    {
        var buf = new BufferedWaveProvider(cap.WaveFormat) { DiscardOnBufferOverflow = true };
        cap.DataAvailable += (_, a) => buf.AddSamples(a.Buffer, 0, a.BytesRecorded);
        var wf = cap.WaveFormat;
        ISampleProvider sp = wf.SampleRate == SampleRate && wf.Channels == 2
            ? buf.ToSampleProvider()
            : new MediaFoundationResampler(buf, Target).ToSampleProvider();
        return new Strip { Buf = buf, Src = sp, BytesPerSec = Math.Max(1, wf.AverageBytesPerSecond) };
    }

    private void Run()
    {
        int frames = SampleRate / 50, n = frames * 2;
        var mix = new float[n];
        var bytes = new byte[n * 4];
        var sw = Stopwatch.StartNew();
        long chunk = 0;
        while (_run)
        {
            try { ProcessChunk(_cfg, mix, n); }
            catch (Exception ex) { if (!_errLogged) { _errLogged = true; Log.Error("AudioMixer.ProcessChunk", ex); } Array.Clear(mix); }

            Buffer.BlockCopy(mix, 0, bytes, 0, bytes.Length);
            try { PcmReady?.Invoke(bytes.AsMemory().ToArray(), chunk * 200_000L); } catch { }
            var mb = _monBuf;
            if (mb != null && _cfg.Monitor)
            {
                try
                {
                    if (mb.BufferedBytes > Target.AverageBytesPerSecond / 4) mb.ClearBuffer();
                    mb.AddSamples(bytes, 0, bytes.Length);
                }
                catch { }
            }

            chunk++;
            int wait = (int)(chunk * 20 - sw.ElapsedMilliseconds);
            if (wait > 0) Thread.Sleep(wait);
        }
    }

    private float[] RunStrip(Strip s, StripSettings p, int n, out float peakDb)
    {
        if (s.Buf.BufferedBytes > s.BytesPerSec / 4) s.Buf.ClearBuffer();
        int r = s.Src.Read(s.In, 0, n);
        if (r < n) Array.Clear(s.In, r, n - r);
        var b = s.In;

        s.Dl.Process(b, n, p.DelayMs * SampleRate / 1000);
        if (p.HighPass) { s.Hp.Set(p.HighPassHz, SampleRate); s.Hp.Process(b, n); }
        if (p.Gate) s.Gt.Process(b, n, SampleRate, p.GateThresholdDb, p.GateAttackMs, p.GateReleaseMs); else s.Gt.Reset();
        if (p.Comp) s.Cp.Process(b, n, SampleRate, p.CompThresholdDb, p.CompRatio, p.CompAttackMs, p.CompReleaseMs, p.CompMakeupDb); else s.Cp.Reset();

        float g = p.Mute ? 0f : Db.ToLin(p.GainDb);
        float gl = g * (p.Pan <= 0f ? 1f : 1f - p.Pan), gr = g * (p.Pan >= 0f ? 1f : 1f + p.Pan);
        float pk = 0f;
        for (int i = 0; i + 1 < n; i += 2)
        {
            b[i] *= gl; b[i + 1] *= gr;
            pk = MathF.Max(pk, MathF.Max(MathF.Abs(b[i]), MathF.Abs(b[i + 1])));
        }
        peakDb = Db.ToDb(pk);
        return b;
    }

    private static float Fall(float prev, float now) => MathF.Max(now, prev - 1.2f);

    private void ProcessChunk(MixerSettings c, float[] mix, int n)
    {
        float[]? lo = null, mi = null;
        float loDb = -90f, miDb = -90f;
        if (_sLoop != null) { lo = RunStrip(_sLoop, c.Loopback, n, out loDb); Levels.LoopGateOpen = _sLoop.Gt.Open; Levels.LoopGrDb = _sLoop.Cp.GrDb; }
        if (_sMic != null)  { mi = RunStrip(_sMic, c.Mic, n, out miDb);       Levels.MicGateOpen = _sMic.Gt.Open;   Levels.MicGrDb = _sMic.Cp.GrDb; }

        float duckGr = 0f;
        if (c.Ducking && lo != null && mi != null)
            duckGr = _ducker.Process(lo, mi, n, SampleRate, c.DuckThresholdDb, c.DuckAmountDb, c.DuckAttackMs, c.DuckReleaseMs);
        else _ducker.Reset();

        for (int i = 0; i < n; i++) mix[i] = (lo != null ? lo[i] : 0f) + (mi != null ? mi[i] : 0f);

        float mg = c.MasterMute ? 0f : Db.ToLin(c.MasterGainDb);
        if (mg != 1f) for (int i = 0; i < n; i++) mix[i] *= mg;

        float limGr = 0f;
        if (c.Limiter) limGr = _limiter.Process(mix, n, SampleRate, c.LimiterCeilingDb);
        float pl = 0f, pr = 0f;
        for (int i = 0; i + 1 < n; i += 2)
        {
            mix[i] = Math.Clamp(mix[i], -1f, 1f); mix[i + 1] = Math.Clamp(mix[i + 1], -1f, 1f);
            pl = MathF.Max(pl, MathF.Abs(mix[i])); pr = MathF.Max(pr, MathF.Abs(mix[i + 1]));
        }

        Levels.LoopDb = Fall(Levels.LoopDb, loDb);
        Levels.MicDb = Fall(Levels.MicDb, miDb);
        Levels.MasterLDb = Fall(Levels.MasterLDb, Db.ToDb(pl));
        Levels.MasterRDb = Fall(Levels.MasterRDb, Db.ToDb(pr));
        Levels.DuckGrDb = duckGr; Levels.LimGrDb = limGr;
    }

    private void ApplyMonitor(MixerSettings c)
    {
        lock (_monLock)
        {
            bool want = c.Monitor && !string.IsNullOrEmpty(c.MonitorDeviceId);
            if (!want) { StopMonitor(); MonitorBlocked = false; return; }
            if (_monOut != null && _monId == c.MonitorDeviceId) return;
            StopMonitor();
            try
            {
                using var e = new MMDeviceEnumerator();
                var dev = AudioDeviceService.Resolve(e, c.MonitorDeviceId, DataFlow.Render, Role.Multimedia);
                if (dev == null) return;
                if (_loop != null && dev.ID == _loopDeviceId)
                {

                    MonitorBlocked = true;
                    Log.Write("Monitoring bloqué : même périphérique que le loopback (risque de larsen).");
                    return;
                }
                MonitorBlocked = false;
                var buf = new BufferedWaveProvider(Target) { DiscardOnBufferOverflow = true, ReadFully = true };
                var o = new WasapiOut(dev, AudioClientShareMode.Shared, true, 80);
                o.Init(buf); o.Play();
                _monBuf = buf; _monOut = o; _monId = c.MonitorDeviceId;
            }
            catch (Exception ex) { Log.Error("Monitoring", ex); StopMonitor(); }
        }
    }

    private void StopMonitor()
    {
        _monBuf = null;
        try { _monOut?.Stop(); } catch { }
        try { _monOut?.Dispose(); } catch { }
        _monOut = null; _monId = null;
    }

    public void Dispose()
    {
        _run = false;
        try { _thread?.Join(500); } catch { }
        try { _loop?.StopRecording(); } catch { }
        try { _mic?.StopRecording(); } catch { }
        try { _loop?.Dispose(); } catch { }
        try { _mic?.Dispose(); } catch { }
        lock (_monLock) StopMonitor();
    }
}
