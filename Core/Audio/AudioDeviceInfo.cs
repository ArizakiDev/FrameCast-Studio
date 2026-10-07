using NAudio.CoreAudioApi;

namespace FrameCastStudio.Core.Audio;

public sealed record AudioDeviceInfo(string Id, string Name, DataFlow Flow)
{
    public override string ToString() => Name;

    public static readonly AudioDeviceInfo Default = new("__DEFAULT__", "Défaut système", DataFlow.All);
}

public static class AudioDeviceService
{

    public static IReadOnlyList<AudioDeviceInfo> GetRenderDevices()
    {
        var list = new List<AudioDeviceInfo> { AudioDeviceInfo.Default };
        try
        {
            using var e = new MMDeviceEnumerator();
            foreach (var d in e.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
                list.Add(new AudioDeviceInfo(d.ID, d.FriendlyName, DataFlow.Render));
        }
        catch {  }
        return list;
    }

    public static IReadOnlyList<AudioDeviceInfo> GetCaptureDevices()
    {
        var list = new List<AudioDeviceInfo> { AudioDeviceInfo.Default };
        try
        {
            using var e = new MMDeviceEnumerator();
            foreach (var d in e.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active))
                list.Add(new AudioDeviceInfo(d.ID, d.FriendlyName, DataFlow.Capture));
        }
        catch { }
        return list;
    }

    internal static MMDevice? Resolve(MMDeviceEnumerator enumerator, string? id, DataFlow flow, Role role)
    {
        if (string.IsNullOrEmpty(id) || id == AudioDeviceInfo.Default.Id)
            return enumerator.GetDefaultAudioEndpoint(flow, role);

        try { return enumerator.GetDevice(id); }
        catch { return enumerator.GetDefaultAudioEndpoint(flow, role); }
    }
}
