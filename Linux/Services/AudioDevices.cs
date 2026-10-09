namespace FrameCastStudio.Linux.Services;

public sealed record AudioDevice(string Id, string Name, bool IsMonitor)
{
    public override string ToString() => Name;
}

public sealed class AudioDeviceList
{
    public List<AudioDevice> Loopbacks { get; } = new();
    public List<AudioDevice> Mics { get; } = new();
    public string DefaultLoopbackId { get; set; } = "@DEFAULT_MONITOR@";
    public string DefaultMicId { get; set; } = "@DEFAULT_SOURCE@";
    public bool PactlMissing { get; set; }
}

/// <summary>Énumère les sources audio via pactl (PulseAudio ou PipeWire-Pulse). Sans pactl, on retombe sur les périphériques par défaut.</summary>
public static class AudioDevices
{
    private static readonly Dictionary<string, string> CEnv = new() { ["LC_ALL"] = "C", ["LANG"] = "C" };

    public static async Task<AudioDeviceList> EnumerateAsync()
    {
        var list = new AudioDeviceList();
        string? pactl = ProcessUtil.FindOnPath("pactl");
        if (pactl == null)
        {
            list.PactlMissing = true;
            list.Loopbacks.Add(new AudioDevice(list.DefaultLoopbackId, "Sortie par défaut (moniteur)", true));
            list.Mics.Add(new AudioDevice(list.DefaultMicId, "Micro par défaut", false));
            return list;
        }

        var res = await ProcessUtil.RunAsync(pactl, new[] { "list", "sources" }, 6000, CEnv);
        if (res.ExitCode == 0)
        {
            string? name = null;
            foreach (var raw in res.Stdout.Split('\n'))
            {
                string line = raw.Trim();
                if (line.StartsWith("Name:")) name = line[5..].Trim();
                else if (line.StartsWith("Description:") && name != null)
                {
                    string desc = line[12..].Trim();
                    bool mon = name.EndsWith(".monitor", StringComparison.Ordinal);
                    (mon ? list.Loopbacks : list.Mics).Add(new AudioDevice(name, desc, mon));
                    name = null;
                }
            }
        }

        string defSink = await GetAsync(pactl, "get-default-sink", "Default Sink:");
        string defSrc = await GetAsync(pactl, "get-default-source", "Default Source:");
        if (!string.IsNullOrEmpty(defSink)) list.DefaultLoopbackId = defSink + ".monitor";
        if (!string.IsNullOrEmpty(defSrc) && !defSrc.EndsWith(".monitor")) list.DefaultMicId = defSrc;

        if (list.Loopbacks.Count == 0) list.Loopbacks.Add(new AudioDevice("@DEFAULT_MONITOR@", "Sortie par défaut (moniteur)", true));
        if (list.Mics.Count == 0) list.Mics.Add(new AudioDevice("@DEFAULT_SOURCE@", "Micro par défaut", false));
        return list;
    }

    private static async Task<string> GetAsync(string pactl, string sub, string infoKey)
    {
        var r = await ProcessUtil.RunAsync(pactl, new[] { sub }, 4000, CEnv);
        if (r.ExitCode == 0 && !string.IsNullOrWhiteSpace(r.Stdout)) return r.Stdout.Trim();

        // Anciennes versions : "pactl info"
        r = await ProcessUtil.RunAsync(pactl, new[] { "info" }, 4000, CEnv);
        if (r.ExitCode != 0) return "";
        foreach (var raw in r.Stdout.Split('\n'))
        {
            string line = raw.Trim();
            if (line.StartsWith(infoKey)) return line[infoKey.Length..].Trim();
        }
        return "";
    }
}
