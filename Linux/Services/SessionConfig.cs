namespace FrameCastStudio.Linux.Services;

public sealed class StripCfg
{
    public double GainDb { get; set; }
    public bool Mute { get; set; }
    public double Pan { get; set; }
    public double DelayMs { get; set; }
    public bool HighPass { get; set; }
    public double HighPassHz { get; set; } = 80;
    public bool Gate { get; set; }
    public double GateThresholdDb { get; set; } = -45;
    public double GateAttackMs { get; set; } = 5;
    public double GateReleaseMs { get; set; } = 150;
    public bool Comp { get; set; }
    public double CompThresholdDb { get; set; } = -18;
    public double CompRatio { get; set; } = 3;
    public double CompAttackMs { get; set; } = 10;
    public double CompReleaseMs { get; set; } = 120;
    public double CompMakeupDb { get; set; }
}

/// <summary>Photographie immuable des réglages au moment du démarrage d'une session.</summary>
public sealed class SessionConfig
{
    /// <summary>rec | live | replay</summary>
    public string Mode { get; set; } = "rec";

    public int Fps { get; set; } = 60;
    public int ScalePercent { get; set; } = 100;
    public bool CaptureCursor { get; set; } = true;
    public bool CropEnabled { get; set; }
    public int CropX { get; set; }
    public int CropY { get; set; }
    public int CropW { get; set; }
    public int CropH { get; set; }

    public string Codec { get; set; } = "H264";
    public string EncoderMode { get; set; } = "Auto";
    public string RateMode { get; set; } = "CBR";
    public int BitrateKbps { get; set; } = 6000;
    public int Qp { get; set; } = 22;
    public int GopSeconds { get; set; } = 2;
    public bool BFrames { get; set; }
    public bool LowLatency { get; set; } = true;
    public bool Vbv { get; set; }
    public string ColorSpace { get; set; } = "BT.709";

    public string Container { get; set; } = "MP4";
    public bool Fragmented { get; set; }
    public bool Split { get; set; }
    public double SplitGb { get; set; } = 4;
    public string OutputFolder { get; set; } = "";
    public string Prefix { get; set; } = "FrameCast";
    public bool Timestamp { get; set; } = true;

    public bool AudioLoopback { get; set; } = true;
    public bool AudioMic { get; set; } = true;
    public string LoopbackId { get; set; } = "@DEFAULT_MONITOR@";
    public string MicId { get; set; } = "@DEFAULT_SOURCE@";
    public int AudioBitrateKbps { get; set; } = 128;
    public int AudioSampleRate { get; set; } = 48000;
    public StripCfg Loop { get; set; } = new();
    public StripCfg Mic { get; set; } = new();
    public double MasterGainDb { get; set; }
    public bool MasterMute { get; set; }
    public bool Limiter { get; set; } = true;
    public double LimiterCeilingDb { get; set; } = -1;
    public bool Ducking { get; set; }
    public double DuckThresholdDb { get; set; } = -35;
    public double DuckAmountDb { get; set; } = 12;
    public double DuckAttackMs { get; set; } = 30;
    public double DuckReleaseMs { get; set; } = 500;

    public bool ReplayEnabled { get; set; }
    public int ReplaySeconds { get; set; } = 30;

    public string IngestUrl { get; set; } = "";
    public string StreamKey { get; set; } = "";
    public bool RecordWhileLive { get; set; }

    /// <summary>Sauvegardé après autorisation du portail pour ne plus redemander l'écran à partager.</summary>
    public string? PortalRestoreToken { get; set; }
    /// <summary>Auto | X11 | Portal</summary>
    public string CaptureBackend { get; set; } = "Auto";
}
