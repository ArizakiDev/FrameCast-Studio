namespace FrameCastStudio.Core.Audio;

public sealed record StripSettings
{
    public float GainDb { get; init; } = 0f;
    public bool Mute { get; init; }
    public float Pan { get; init; }
    public int DelayMs { get; init; }

    public bool HighPass { get; init; }
    public float HighPassHz { get; init; } = 80f;

    public bool Gate { get; init; }
    public float GateThresholdDb { get; init; } = -45f;
    public float GateAttackMs { get; init; } = 5f;
    public float GateReleaseMs { get; init; } = 150f;

    public bool Comp { get; init; }
    public float CompThresholdDb { get; init; } = -18f;
    public float CompRatio { get; init; } = 3f;
    public float CompAttackMs { get; init; } = 10f;
    public float CompReleaseMs { get; init; } = 120f;
    public float CompMakeupDb { get; init; } = 0f;
}

public sealed record MixerSettings
{
    public StripSettings Loopback { get; init; } = new();
    public StripSettings Mic { get; init; } = new();

    public float MasterGainDb { get; init; } = 0f;
    public bool MasterMute { get; init; }
    public bool Limiter { get; init; } = true;
    public float LimiterCeilingDb { get; init; } = -1f;

    public bool Ducking { get; init; }
    public float DuckThresholdDb { get; init; } = -35f;
    public float DuckAmountDb { get; init; } = 12f;
    public float DuckAttackMs { get; init; } = 30f;
    public float DuckReleaseMs { get; init; } = 500f;

    public bool Monitor { get; init; }
    public string? MonitorDeviceId { get; init; }
}

public sealed class MixerLevels
{
    public volatile float LoopDb = -90f, MicDb = -90f, MasterLDb = -90f, MasterRDb = -90f;
    public volatile float LoopGrDb, MicGrDb, DuckGrDb, LimGrDb;
    public volatile bool LoopGateOpen = true, MicGateOpen = true;
}
