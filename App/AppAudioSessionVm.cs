using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FrameCastStudio.Core.Audio;

namespace FrameCastStudio.App;

/// <summary>ViewModel wrapper around one AppAudioSession for XAML binding.</summary>
public sealed partial class AppAudioSessionVm : ObservableObject, IDisposable
{
    private readonly AppAudioSession _session;

    public string Name => _session.DisplayName;
    public string ProcessName => _session.ProcessName;

    [ObservableProperty] private double volume;
    [ObservableProperty] private bool muted;
    [ObservableProperty] private double peak;

    public AppAudioSessionVm(AppAudioSession session)
    {
        _session = session;
        volume = session.Volume;
        muted = session.Muted;
        peak = session.Peak;
    }

    partial void OnVolumeChanged(double value) => _session.Volume = (float)value;
    partial void OnMutedChanged(bool value) => _session.Muted = value;

    /// <summary>Called by the refresh timer to update the VU meter peak.</summary>
    public void RefreshPeak() => Peak = _session.Peak;

    [RelayCommand]
    private void ToggleMute() => Muted = !Muted;

    public void Dispose() => _session.Dispose();
}
