using System.Runtime.InteropServices;
using Vortice.MediaFoundation;
using FrameCastStudio.Core.Capture;

namespace FrameCastStudio.Core.Encoding;

public enum Codec { H264, HEVC, AV1 }
public enum RateControl { CBR, VBR, CQP }

public sealed record EncoderSettings(
    int Width, int Height, int Fps, Codec Codec = Codec.H264,
    RateControl Rate = RateControl.CBR, int BitrateKbps = 8000, int Qp = 22, int GopSeconds = 2,
    int BFrames = 0, bool LowLatency = true, bool Bt2020 = false,
    bool EnableVbv = false, int VbvMs = 2000);

public readonly record struct EncodedPacket(ReadOnlyMemory<byte> Data, long PtsHns, long DurHns, bool KeyFrame);

public sealed class HardwareEncoder : IDisposable
{
    private static readonly Guid Av1Guid = new("31305641-0000-0010-8000-00AA00389B71");
    private const uint MFT_ENUM_FLAG_HARDWARE = 0x4, MFT_ENUM_FLAG_SORTANDFILTER = 0x40;
    private IMFTransform _mft = null!;
    private IMFDXGIDeviceManager _dxgiMgr = null!;
    private IMFMediaEventGenerator _events = null!;
    private readonly EncoderSettings _s;
    private readonly Vortice.Direct3D11.ID3D11Device _dev;
    private readonly Queue<GpuFrame> _pending = new();
    private int _needInput; private long _lastPts; private int _outCount;
    private ICodecApi? _codecApi;
    private readonly ManualResetEventSlim _drainDone = new(false);
    private CancellationTokenSource _cts = new();
    private Task? _loop;
    public string AdapterName { get; private set; } = "";
    public byte[] CodecPrivate { get; private set; } = Array.Empty<byte>();

    public event Action<EncodedPacket>? PacketReady;

    public HardwareEncoder(Vortice.Direct3D11.ID3D11Device device, EncoderSettings s) { _dev = device; _s = s; }

    public void Initialize()
    {
        MediaFactory.MFStartup().CheckError();

        var subtype = _s.Codec switch { Codec.H264 => VideoFormatGuids.H264, Codec.HEVC => VideoFormatGuids.Hevc, _ => Av1Guid };
        var inInfo = new RegisterTypeInfo { GuidMajorType = MediaTypeGuids.Video, GuidSubtype = VideoFormatGuids.NV12 };
        var outInfo = new RegisterTypeInfo { GuidMajorType = MediaTypeGuids.Video, GuidSubtype = subtype };

        using var acts = MediaFactory.MFTEnumEx(TransformCategoryGuids.VideoEncoder,
            MFT_ENUM_FLAG_HARDWARE | MFT_ENUM_FLAG_SORTANDFILTER, inInfo, outInfo);
        var actList = acts.ToList();
        if (actList.Count == 0) throw new NotSupportedException($"Aucun encodeur matériel {_s.Codec}.");
        Exception? last = null;
        foreach (var act in actList)
        {
            string name = "HW";
            try
            {
                name = act.GetString(TransformAttributeKeys.MftFriendlyNameAttribute) ?? "HW";
                FrameCastStudio.Core.Log.Write("Encodeur candidat : " + name);
                _mft = act.ActivateObject<IMFTransform>();

                using (var attrs = _mft.Attributes)
                    attrs.Set(TransformAttributeKeys.TransformAsyncUnlock, 1u);

                _dxgiMgr = MediaFactory.MFCreateDXGIDeviceManager();
                _dxgiMgr.ResetDevice(_dev);
                _mft.ProcessMessage(TMessageType.MessageSetD3DManager, (nuint)_dxgiMgr.NativePointer);

                using var outT = MediaFactory.MFCreateMediaType();
                outT.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video);
                outT.Set(MediaTypeAttributeKeys.Subtype, subtype);
                outT.Set(MediaTypeAttributeKeys.AvgBitrate, (uint)(_s.BitrateKbps * 1000));
                outT.Set(MediaTypeAttributeKeys.InterlaceMode, (uint)VideoInterlaceMode.Progressive);
                Pack(outT, MediaTypeAttributeKeys.FrameSize, _s.Width, _s.Height);
                Pack(outT, MediaTypeAttributeKeys.FrameRate, _s.Fps, 1);
                Pack(outT, MediaTypeAttributeKeys.PixelAspectRatio, 1, 1);
                outT.Set(MediaTypeAttributeKeys.Mpeg2Profile, _s.Codec == Codec.H264 ? 100u : 1u);
                if (_s.Bt2020)
                {

                    outT.Set(new Guid("dbfbe4d7-0740-4ee0-8192-850ab0e21935"), 9u);
                    outT.Set(new Guid("3e23d450-2c75-4d25-a00e-b91670d12327"), 4u);
                    outT.Set(new Guid("5fb0fce9-be5c-4935-a811-ec838f8eed93"), 5u);
                    outT.Set(new Guid("c21b8ee5-b956-4071-8daf-325edf5cab11"), 2u);
                }
                _mft.SetOutputType(0, outT, 0);

                using var inT = MediaFactory.MFCreateMediaType();
                inT.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video);
                inT.Set(MediaTypeAttributeKeys.Subtype, VideoFormatGuids.NV12);
                inT.Set(MediaTypeAttributeKeys.InterlaceMode, (uint)VideoInterlaceMode.Progressive);
                Pack(inT, MediaTypeAttributeKeys.FrameSize, _s.Width, _s.Height);
                Pack(inT, MediaTypeAttributeKeys.FrameRate, _s.Fps, 1);
                _mft.SetInputType(0, inT, 0);

                AdapterName = name;
                last = null;
                break;
            }
            catch (Exception ex)
            {
                last = ex;
                FrameCastStudio.Core.Log.Error("Encodeur " + name, ex);
                try { _mft?.Dispose(); } catch { }
                try { _dxgiMgr?.Dispose(); } catch { }
                _mft = null!; _dxgiMgr = null!;
            }
        }
        if (last != null || _mft is null)
            throw new NotSupportedException("Aucun encodeur matériel utilisable avec cette carte graphique : " + (last?.Message ?? "?"), last);

        ApplyCodecApi();

        _mft.ProcessMessage(TMessageType.MessageNotifyBeginStreaming, 0);
        _mft.ProcessMessage(TMessageType.MessageNotifyStartOfStream, 0);
        _events = _mft.QueryInterface<IMFMediaEventGenerator>();
        _loop = Task.Factory.StartNew(EventLoop, _cts.Token, TaskCreationOptions.LongRunning, TaskScheduler.Default);
    }

    private void ApplyCodecApi()
    {
        var codec = (ICodecApi)Marshal.GetObjectForIUnknown(_mft.NativePointer);
        _codecApi = codec;

        void TrySet(Guid api, object v)
        {
            try   { var var_ = new PropVariant(v); codec.SetValue(ref api, ref var_); }
            catch (Exception) {  }
        }

        TrySet(CodecApiGuids.LowLatencyMode,  _s.LowLatency);
        TrySet(CodecApiGuids.RateControlMode, _s.Rate switch
        {
            RateControl.CBR => 0u,
            RateControl.VBR => 1u,
            _               => 3u,
        });
        if (_s.Rate != RateControl.CQP)
            TrySet(CodecApiGuids.MeanBitRate, (uint)(_s.BitrateKbps * 1000));
        if (_s.EnableVbv && _s.Rate != RateControl.CQP)
            TrySet(CodecApiGuids.BufferSize, (uint)Math.Clamp(_s.BitrateKbps * 1000L * _s.VbvMs / 1000, 100_000L, 400_000_000L));
        if (_s.Rate == RateControl.CQP)
        {
            ulong q = (ulong)Math.Clamp(_s.Qp, 0, 51);
            TrySet(CodecApiGuids.DefaultQp, q | (q << 16) | (q << 32));
            TrySet(CodecApiGuids.CommonQuality, (uint)Math.Clamp(100 - _s.Qp * 100 / 51, 0, 100));
        }
        TrySet(CodecApiGuids.GopSize,    (uint)Math.Max(1, _s.Fps * _s.GopSeconds));
        TrySet(CodecApiGuids.MaxBFrames, (uint)Math.Max(0, _s.BFrames));
    }

    public void SetBitrate(int kbps)
    {
        var api = _codecApi; if (api is null) return;
        try { var g = CodecApiGuids.MeanBitRate; var v = new PropVariant((uint)(kbps * 1000)); api.SetValue(ref g, ref v); }
        catch (Exception ex) { FrameCastStudio.Core.Log.Write("SetBitrate ignoré : " + ex.Message, 1); }
    }

    public void Drain(int timeoutMs)
    {
        if (_mft is null || _events is null) return;
        try { _mft.ProcessMessage(TMessageType.MessageCommandDrain, 0); } catch { return; }
        bool ok = _drainDone.Wait(timeoutMs);
        FrameCastStudio.Core.Log.Write("Encodeur : drain " + (ok ? "terminé" : "timeout"), 1);
    }

    public void Encode(GpuFrame frame)
    {
        lock (_pending) _pending.Enqueue(frame);
        Pump();
    }

    private void Pump()
    {
        while (true)
        {
            GpuFrame? f;
            lock (_pending) { if (_needInput == 0 || !_pending.TryDequeue(out f)) return; _needInput--; }
            using var buf = MediaFactory.MFCreateDXGISurfaceBuffer(typeof(Vortice.Direct3D11.ID3D11Texture2D).GUID, f.Texture, 0, false);
            using var sample = MediaFactory.MFCreateSample();
            sample.AddBuffer(buf);
            sample.SampleTime = f.TimestampHns;
            sample.SampleDuration = 10_000_000L / _s.Fps;
            _mft.ProcessInput(0, sample, 0);
            f.Release();
        }
    }

    private void EventLoop()
    {
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                using var ev = _events.GetEvent(0);
                switch (EventId(ev))
                {
                    case 601:  Interlocked.Increment(ref _needInput); Pump(); break;
                    case 602:  DrainOutput(); break;
                    case 603:  _drainDone.Set(); break;
                }
            }
            catch when (_cts.IsCancellationRequested) { break; }
            catch {  }
        }
    }

    private static int EventId(IMFMediaEvent e)
    {
        var t = e.GetType();
        object? v = t.GetProperty("EventType")?.GetValue(e) ?? t.GetMethod("GetEventType", Type.EmptyTypes)?.Invoke(e, null)
                    ?? t.GetProperty("Type")?.GetValue(e);
        return Convert.ToInt32(v);
    }

    private void DrainOutput()
    {
        var odb = new OutputDataBuffer();
        _mft.ProcessOutput(0, 1, ref odb, out _);
        if (odb.Sample is null) return;
        using var sample = odb.Sample;
        using var mb = sample.ConvertToContiguousBuffer();
        mb.Lock(out var p, out _, out var len);
        try
        {
            var data = new byte[len]; Marshal.Copy(p, data, 0, len);
            bool key = false;
            try { key = sample.GetUInt32(SampleAttributeKeys.CleanPoint) != 0; } catch { }
            long pts, dur;
            try { pts = sample.SampleTime; } catch { pts = _lastPts; }
            try { dur = sample.SampleDuration; } catch { dur = 10_000_000L / _s.Fps; }
            _lastPts = pts;
            if (_outCount++ < 3) FrameCastStudio.Core.Log.Write($"Sortie encodeur #{_outCount} : {len} o key={key}");
            PacketReady?.Invoke(new EncodedPacket(data, pts, dur, key));
        }
        finally { mb.Unlock(); }
    }

    private static void Pack(IMFAttributes a, Guid key, int hi, int lo) =>
        a.Set(key, ((ulong)(uint)hi << 32) | (uint)lo);

    public void Dispose()
    {
        _cts.Cancel();
        lock (_pending) { while (_pending.TryDequeue(out var pf)) pf.Release(); }
        try { _mft.ProcessMessage(TMessageType.MessageNotifyEndOfStream, 0); } catch { }
        _events?.Dispose(); _mft?.Dispose(); _dxgiMgr?.Dispose();
        MediaFactory.MFShutdown();
    }
}
