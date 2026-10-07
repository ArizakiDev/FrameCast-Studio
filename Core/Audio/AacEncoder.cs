using System.Runtime.InteropServices;
using Vortice.MediaFoundation;

namespace FrameCastStudio.Core.Audio;

internal sealed class AacEncoder : IDisposable
{
    private const uint MFT_ENUM_FLAG_SYNCMFT = 0x1, MFT_ENUM_FLAG_SORTANDFILTER = 0x40;
    private static readonly Guid AacPayloadType = new("bfbabe79-7434-4d1c-94f0-72a3b9e17188");
    private static readonly Guid AacProfileLevel = new("7632f0e6-9538-4d61-acda-ea29c8c14456");

    private readonly int _sr;
    private IMFTransform _mft = null!;
    private long _inSamples, _frames, _baseMs = -1;
    private bool _disposed;

    public byte[] AudioSpecificConfig { get; }

    public event Action<byte[], uint>? FrameReady;

    public static int ClampKbps(int k) => k <= 112 ? 96 : k <= 144 ? 128 : k <= 176 ? 160 : 192;

    public AacEncoder(int sampleRate, int bitrateKbps)
    {
        _sr = sampleRate == 44100 ? 44100 : 48000;
        int idx = _sr == 44100 ? 4 : 3;
        AudioSpecificConfig = new[] { (byte)((2 << 3) | (idx >> 1)), (byte)(((idx & 1) << 7) | (2 << 3)) };

        MediaFactory.MFStartup().CheckError();
        var inInfo = new RegisterTypeInfo { GuidMajorType = MediaTypeGuids.Audio, GuidSubtype = AudioFormatGuids.Pcm };
        var outInfo = new RegisterTypeInfo { GuidMajorType = MediaTypeGuids.Audio, GuidSubtype = AudioFormatGuids.Aac };
        using var acts = MediaFactory.MFTEnumEx(TransformCategoryGuids.AudioEncoder,
            MFT_ENUM_FLAG_SYNCMFT | MFT_ENUM_FLAG_SORTANDFILTER, inInfo, outInfo);
        var list = acts.ToList();
        if (list.Count == 0) throw new NotSupportedException("Encodeur AAC introuvable (Media Foundation).");
        _mft = list[0].ActivateObject<IMFTransform>();

        using var outT = MediaFactory.MFCreateMediaType();
        outT.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Audio);
        outT.Set(MediaTypeAttributeKeys.Subtype, AudioFormatGuids.Aac);
        outT.Set(MediaTypeAttributeKeys.AudioSamplesPerSecond, (uint)_sr);
        outT.Set(MediaTypeAttributeKeys.AudioNumChannels, 2u);
        outT.Set(MediaTypeAttributeKeys.AudioBitsPerSample, 16u);
        outT.Set(MediaTypeAttributeKeys.AudioAvgBytesPerSecond, (uint)(ClampKbps(bitrateKbps) * 125));
        outT.Set(MediaTypeAttributeKeys.AudioBlockAlignment, 1u);
        outT.Set(AacPayloadType, 0u);
        outT.Set(AacProfileLevel, 0x29u);
        _mft.SetOutputType(0, outT, 0);

        using var inT = MediaFactory.MFCreateMediaType();
        inT.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Audio);
        inT.Set(MediaTypeAttributeKeys.Subtype, AudioFormatGuids.Pcm);
        inT.Set(MediaTypeAttributeKeys.AudioSamplesPerSecond, (uint)_sr);
        inT.Set(MediaTypeAttributeKeys.AudioNumChannels, 2u);
        inT.Set(MediaTypeAttributeKeys.AudioBitsPerSample, 16u);
        inT.Set(MediaTypeAttributeKeys.AudioBlockAlignment, 4u);
        inT.Set(MediaTypeAttributeKeys.AudioAvgBytesPerSecond, (uint)(_sr * 4));
        _mft.SetInputType(0, inT, 0);

        _mft.ProcessMessage(TMessageType.MessageNotifyBeginStreaming, 0);
        _mft.ProcessMessage(TMessageType.MessageNotifyStartOfStream, 0);
        Log.Write($"Encodeur AAC (live) prêt : {_sr} Hz, {ClampKbps(bitrateKbps)} kbps");
    }

    public void Encode(ReadOnlySpan<byte> pcm16, long ptsMs)
    {
        if (_disposed || pcm16.Length == 0) return;
        if (_baseMs < 0) _baseMs = ptsMs;
        int len = pcm16.Length;
        using (var buf = MediaFactory.MFCreateMemoryBuffer(len))
        {
            buf.Lock(out var ptr, out _, out _);
            try { Marshal.Copy(pcm16.ToArray(), 0, ptr, len); }
            finally { buf.Unlock(); }
            buf.CurrentLength = len;
            using var sample = MediaFactory.MFCreateSample();
            sample.AddBuffer(buf);
            sample.SampleTime = _inSamples * 10_000_000L / _sr;
            sample.SampleDuration = (len / 4) * 10_000_000L / _sr;
            _mft.ProcessInput(0, sample, 0);
            _inSamples += len / 4;
        }
        DrainOutputs();
    }

    private void DrainOutputs()
    {
        while (true)
        {
            using var ob = MediaFactory.MFCreateMemoryBuffer(4096);
            using var os = MediaFactory.MFCreateSample();
            os.AddBuffer(ob);
            var odb = new OutputDataBuffer { Sample = os };
            var r = _mft.ProcessOutput(0, 1, ref odb, out _);
            if (r.Failure) break;
            int n = ob.CurrentLength;
            if (n <= 0) break;
            var raw = new byte[n];
            ob.Lock(out var p, out _, out _);
            try { Marshal.Copy(p, raw, 0, n); }
            finally { ob.Unlock(); }
            uint ms = (uint)(_baseMs + _frames * 1024 * 1000 / _sr);
            _frames++;
            FrameReady?.Invoke(raw, ms);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _mft?.Dispose(); } catch { }
        try { MediaFactory.MFShutdown(); } catch { }
    }
}
