using CommunityToolkit.Mvvm.ComponentModel;
using FrameCastStudio.Linux.Services;

namespace FrameCastStudio.Linux.ViewModels;

/// <summary>Voie de mixage (sortie système ou micro) : gain, pan, retard, filtre, gate et compresseur.</summary>
public partial class StripVm : ObservableObject
{
    [ObservableProperty] private double gainDb;
    [ObservableProperty] private bool mute;
    [ObservableProperty] private double pan;
    [ObservableProperty] private double delayMs;
    [ObservableProperty] private bool highPass;
    [ObservableProperty] private double highPassHz = 80;
    [ObservableProperty] private bool gate;
    [ObservableProperty] private double gateThresholdDb = -45;
    [ObservableProperty] private double gateAttackMs = 5;
    [ObservableProperty] private double gateReleaseMs = 150;
    [ObservableProperty] private bool comp;
    [ObservableProperty] private double compThresholdDb = -18;
    [ObservableProperty] private double compRatio = 3;
    [ObservableProperty] private double compAttackMs = 10;
    [ObservableProperty] private double compReleaseMs = 120;
    [ObservableProperty] private double compMakeupDb;

    public StripCfg ToCfg() => new()
    {
        GainDb = GainDb, Mute = Mute, Pan = Pan, DelayMs = DelayMs, HighPass = HighPass, HighPassHz = HighPassHz,
        Gate = Gate, GateThresholdDb = GateThresholdDb, GateAttackMs = GateAttackMs, GateReleaseMs = GateReleaseMs,
        Comp = Comp, CompThresholdDb = CompThresholdDb, CompRatio = CompRatio, CompAttackMs = CompAttackMs,
        CompReleaseMs = CompReleaseMs, CompMakeupDb = CompMakeupDb,
    };

    public void Apply(StripCfg c)
    {
        GainDb = c.GainDb; Mute = c.Mute; Pan = c.Pan; DelayMs = c.DelayMs; HighPass = c.HighPass; HighPassHz = c.HighPassHz;
        Gate = c.Gate; GateThresholdDb = c.GateThresholdDb; GateAttackMs = c.GateAttackMs; GateReleaseMs = c.GateReleaseMs;
        Comp = c.Comp; CompThresholdDb = c.CompThresholdDb; CompRatio = c.CompRatio; CompAttackMs = c.CompAttackMs;
        CompReleaseMs = c.CompReleaseMs; CompMakeupDb = c.CompMakeupDb;
    }
}
