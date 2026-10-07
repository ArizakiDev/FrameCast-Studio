using NAudio.CoreAudioApi;
using System.Diagnostics;

namespace FrameCastStudio.Core.Audio;

/// <summary>Represents one active Windows audio session (= one app).</summary>
public sealed class AppAudioSession : IDisposable
{
    private readonly AudioSessionControl _ctrl;
    private bool _disposed;

    public string ProcessName { get; }
    public int ProcessId { get; }
    public string DisplayName { get; }

    internal AppAudioSession(AudioSessionControl ctrl)
    {
        _ctrl = ctrl;
        ProcessId = (int)ctrl.GetProcessID;
        try
        {
            var p = Process.GetProcessById(ProcessId);
            ProcessName = p.ProcessName;
            DisplayName = string.IsNullOrWhiteSpace(ctrl.DisplayName) ? p.ProcessName : ctrl.DisplayName;
        }
        catch
        {
            ProcessName = $"PID {ProcessId}";
            DisplayName = ProcessName;
        }
    }

    /// <summary>Volume 0.0–1.0</summary>
    public float Volume
    {
        get
        {
            try { return _ctrl.SimpleAudioVolume.Volume; } catch { return 1f; }
        }
        set
        {
            try { _ctrl.SimpleAudioVolume.Volume = Math.Clamp(value, 0f, 1f); } catch { }
        }
    }

    public bool Muted
    {
        get { try { return _ctrl.SimpleAudioVolume.Mute; } catch { return false; } }
        set { try { _ctrl.SimpleAudioVolume.Mute = value; } catch { } }
    }

    /// <summary>Peak level 0.0–1.0 for the VU meter.</summary>
    public float Peak
    {
        get { try { return _ctrl.AudioMeterInformation.MasterPeakValue; } catch { return 0f; } }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _ctrl.Dispose(); } catch { }
    }
}
