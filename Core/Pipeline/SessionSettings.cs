using FrameCastStudio.Core.Audio;
using FrameCastStudio.Core.Encoding;

namespace FrameCastStudio.Core.Pipeline;

public sealed record SessionSettings
{

    public int Fps { get; init; } = 60;
    public int ScalePercent { get; init; } = 100;
    public bool CaptureCursor { get; init; } = true;
    public bool DisableWgcBorder { get; init; } = true;
    public double CaptureOffsetMs { get; init; }

    public Codec Codec { get; init; } = Codec.H264;
    public RateControl Rate { get; init; } = RateControl.CBR;
    public int BitrateKbps { get; init; } = 6000;
    public int Qp { get; init; } = 22;
    public int GopSeconds { get; init; } = 2;
    public int BFrames { get; init; } = 0;
    public bool LowLatency { get; init; } = true;
    public bool Bt2020 { get; init; }
    public bool AdaptiveBitrate { get; init; }
    public bool EnableVbv { get; init; }
    public int VbvBufferMs { get; init; } = 2000;

    public bool FragmentedMp4 { get; init; }
    public bool SplitEnabled { get; init; }
    public double SplitSizeGb { get; init; } = 4.0;

    public bool AudioLoopback { get; init; } = true;
    public bool AudioMic { get; init; } = true;
    public int AudioBufferMs { get; init; } = 20;
    public string? LoopbackDeviceId { get; init; } = null;
    public string? MicDeviceId { get; init; } = null;
    public int AudioBitrateKbps { get; init; } = 128;
    public int AudioSampleRate { get; init; } = 48000;
    public bool MeterWhenIdle { get; init; }
    public MixerSettings Mixer { get; init; } = new();

    public int RtmpQueueCapacity { get; init; } = 240;
    public int RtmpChunkSize { get; init; } = 4096;

    public System.Diagnostics.ProcessPriorityClass ProcessPriority { get; init; } = System.Diagnostics.ProcessPriorityClass.High;

    public EncoderSettings ToEncoderSettings(int width, int height) => new(
        width, height, Fps, Codec, Rate, BitrateKbps, Qp, GopSeconds, BFrames, LowLatency, Bt2020, EnableVbv, VbvBufferMs);
}
