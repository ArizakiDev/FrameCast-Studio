using System.Runtime.InteropServices;
using Vortice.MediaFoundation;

namespace FrameCastStudio.Core.Recording;

public sealed class Mp4Recorder : IDisposable
{
    private IMFSinkWriter _writer = null!;
    private int _videoStreamIdx;
    private int _audioStreamIdx = -1;
    private readonly int _audioBitrateKbps;
    private readonly bool _hevc;
    private static readonly Guid HevcSubtype = new("43564548-0000-0010-8000-00aa00389b71");

    public long BaseHns { get; }

    public long BytesWritten => Interlocked.Read(ref _bytes);
    private long _bytes;
    private readonly object _lock = new();
    private bool _disposed;
    private readonly bool _hasAudio;
    private readonly int _sampleRate, _channels;

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int FinalizeDelegate(IntPtr pThis);

    public Mp4Recorder(string path, int width, int height, int fps, int bitrateKbps,
                       bool hasAudio = true, int sampleRate = 48000, int channels = 2, int audioBitrateKbps = 128,
                       bool fragmented = false, long baseHns = 0, bool hevc = false)
    {
        _hevc = hevc; _hasAudio = hasAudio; _sampleRate = sampleRate; _channels = channels;
        _audioBitrateKbps = audioBitrateKbps; BaseHns = baseHns;
        Initialize(path, width, height, fps, bitrateKbps, audioBitrateKbps, fragmented);
    }

    private void Initialize(string path, int width, int height, int fps, int bitrateKbps, int audioBitrateKbps, bool fragmented)
    {

        MediaFactory.MFStartup();

        using var attrs = MediaFactory.MFCreateAttributes(3);
        attrs.Set(SinkWriterAttributeKeys.ReadwriteEnableHardwareTransforms, 1u);
        if (fragmented)
        {

            attrs.Set(new Guid("150ff23f-4abc-478b-ac4f-e1916fba1ca5"), new Guid("9ba876f1-419f-4b77-a1e0-35959d9d4004"));
        }
        _writer = MediaFactory.MFCreateSinkWriterFromURL(path, null, attrs);

        using var videoType = MediaFactory.MFCreateMediaType();
        videoType.Set(MediaTypeAttributeKeys.MajorType,           MediaTypeGuids.Video);
        videoType.Set(MediaTypeAttributeKeys.Subtype,             _hevc ? HevcSubtype : VideoFormatGuids.H264);
        videoType.Set(MediaTypeAttributeKeys.AvgBitrate,          (uint)(bitrateKbps * 1000));
        videoType.Set(MediaTypeAttributeKeys.InterlaceMode,       (uint)VideoInterlaceMode.Progressive);
        videoType.Set(MediaTypeAttributeKeys.FrameSize,           Pack64(width,  height));
        videoType.Set(MediaTypeAttributeKeys.FrameRate,           Pack64(fps,    1));
        videoType.Set(MediaTypeAttributeKeys.PixelAspectRatio,    Pack64(1,      1));
        _videoStreamIdx = _writer.AddStream(videoType);
        _writer.SetInputMediaType(_videoStreamIdx, videoType, null);

        if (_hasAudio)
        {

            using var outAudio = MediaFactory.MFCreateMediaType();
            outAudio.Set(MediaTypeAttributeKeys.MajorType,              MediaTypeGuids.Audio);
            outAudio.Set(MediaTypeAttributeKeys.Subtype,                AudioFormatGuids.Aac);
            outAudio.Set(MediaTypeAttributeKeys.AudioSamplesPerSecond,  (uint)_sampleRate);
            outAudio.Set(MediaTypeAttributeKeys.AudioNumChannels,       (uint)_channels);
            outAudio.Set(MediaTypeAttributeKeys.AudioAvgBytesPerSecond, (uint)(audioBitrateKbps * 1000 / 8));
            outAudio.Set(MediaTypeAttributeKeys.AudioBitsPerSample,     16u);
            outAudio.Set(MediaTypeAttributeKeys.AudioBlockAlignment,    (uint)(_channels * 2));
            _audioStreamIdx = _writer.AddStream(outAudio);

            using var inAudio = MediaFactory.MFCreateMediaType();
            inAudio.Set(MediaTypeAttributeKeys.MajorType,              MediaTypeGuids.Audio);

            inAudio.Set(MediaTypeAttributeKeys.Subtype,                AudioFormatGuids.Pcm);
            inAudio.Set(MediaTypeAttributeKeys.AudioSamplesPerSecond,  (uint)_sampleRate);
            inAudio.Set(MediaTypeAttributeKeys.AudioNumChannels,       (uint)_channels);
            inAudio.Set(MediaTypeAttributeKeys.AudioBitsPerSample,     16u);
            inAudio.Set(MediaTypeAttributeKeys.AudioBlockAlignment,    (uint)(_channels * 2));
            inAudio.Set(MediaTypeAttributeKeys.AudioAvgBytesPerSecond, (uint)(_sampleRate * _channels * 2));
            _writer.SetInputMediaType(_audioStreamIdx, inAudio, null);
        }

        _writer.BeginWriting();
        FrameCastStudio.Core.Log.Write($"Mp4Recorder prêt : {path} {width}x{height}@{fps} audio={_hasAudio} fragmenté={fragmented} base={BaseHns / 10_000} ms");
    }

    public void WriteVideoSample(ReadOnlySpan<byte> data, long ptsHns, long durationHns, bool keyFrame)
    {
        lock (_lock)
        {
            if (_disposed) return;
            long relPts = ptsHns - BaseHns;
            if (relPts < 0) return;
            Interlocked.Add(ref _bytes, data.Length);
            WriteSampleInternal(_videoStreamIdx, data.ToArray(), relPts, durationHns,
                keyFrame ? 1u : 0u);
        }
    }

    public void WriteAudioSample(ReadOnlyMemory<byte> pcm, long ptsHns)
    {
        if (!_hasAudio || _audioStreamIdx < 0) return;
        lock (_lock)
        {
            if (_disposed) return;
            long relPts  = ptsHns - BaseHns;
            if (relPts < 0) return;
            long dur     = (long)((pcm.Length / (double)(_sampleRate * _channels * 4)) * 10_000_000);
            Interlocked.Add(ref _bytes, (long)(dur / 10_000_000.0 * _audioBitrateKbps * 125));
            var f32 = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(pcm.Span);
            var s16 = new byte[f32.Length * 2];
            for (int i = 0; i < f32.Length; i++)
            {
                short v = (short)Math.Clamp((int)MathF.Round(f32[i] * 32767f), short.MinValue, short.MaxValue);
                s16[i * 2] = (byte)(v & 0xFF); s16[i * 2 + 1] = (byte)((v >> 8) & 0xFF);
            }
            WriteSampleInternal(_audioStreamIdx, s16, relPts, dur, 0u);
        }
    }

    private void WriteSampleInternal(int streamIdx, byte[] data, long pts, long dur, uint cleanPoint)
    {
        using var buf = MediaFactory.MFCreateMemoryBuffer(data.Length);
        buf.Lock(out var ptr, out _, out _);
        try { Marshal.Copy(data, 0, ptr, data.Length); }
        finally { buf.Unlock(); }
        buf.CurrentLength = data.Length;

        using var sample = MediaFactory.MFCreateSample();
        sample.AddBuffer(buf);
        sample.SampleTime     = pts;
        sample.SampleDuration = dur;
        if (cleanPoint != 0) sample.Set(SampleAttributeKeys.CleanPoint, cleanPoint);
        _writer.WriteSample(streamIdx, sample);
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;

            try { CallComFinalize(_writer.NativePointer); } catch {  }
            _writer.Dispose();
            try { MediaFactory.MFShutdown(); } catch { }
        }
    }

    private static ulong Pack64(int hi, int lo) => ((ulong)(uint)hi << 32) | (uint)lo;

    private static unsafe void CallComFinalize(IntPtr pUnk)
    {
        var vtable = *(IntPtr**)pUnk;
        var fn = (delegate* unmanaged[Stdcall]<IntPtr, int>)vtable[11];
        int hr = fn(pUnk);
        Marshal.ThrowExceptionForHR(hr);
    }
}
