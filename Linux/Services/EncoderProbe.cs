using System.Text.RegularExpressions;

namespace FrameCastStudio.Linux.Services;

public enum EncoderKind { Software, Nvenc, Vaapi }

public sealed record EncoderChoice(string Name, EncoderKind Kind, string Label);

public sealed class EncoderCaps
{
    public HashSet<string> Listed { get; } = new();
    public HashSet<string> Working { get; } = new();
    public string? VaapiDevice { get; set; }
    public string FfmpegVersion { get; set; } = "?";
    public bool HasPulse { get; set; }
    public bool HasX11Grab { get; set; }

    public bool Works(string encoder) => Working.Contains(encoder);
    public bool Has(string encoder) => Listed.Contains(encoder);

    /// <param name="codec">H264 | HEVC | AV1</param>
    /// <param name="mode">Auto | GPU | CPU</param>
    public EncoderChoice? Pick(string codec, string mode)
    {
        string baseName = codec switch { "HEVC" => "hevc", "AV1" => "av1", _ => "h264" };
        string soft = codec switch { "HEVC" => "libx265", "AV1" => "libsvtav1", _ => "libx264" };

        EncoderChoice? hw = null;
        if (mode != "CPU")
        {
            if (Works(baseName + "_nvenc")) hw = new(baseName + "_nvenc", EncoderKind.Nvenc, $"NVENC {codec}");
            else if (Works(baseName + "_vaapi") && VaapiDevice != null) hw = new(baseName + "_vaapi", EncoderKind.Vaapi, $"VAAPI {codec}");
        }
        if (hw != null) return hw;
        if (mode == "GPU") return null;
        if (Has(soft)) return new(soft, EncoderKind.Software, $"Logiciel ({soft}) {codec}");
        if (codec == "AV1" && Has("libaom-av1")) return new("libaom-av1", EncoderKind.Software, "Logiciel (libaom-av1) AV1");
        return null;
    }
}

public static class EncoderProbe
{
    public static async Task<EncoderCaps> ProbeAsync(string ffmpeg)
    {
        var caps = new EncoderCaps();

        var ver = await ProcessUtil.RunAsync(ffmpeg, new[] { "-hide_banner", "-version" }, 8000);
        var m = Regex.Match(ver.Stdout, @"ffmpeg version (\S+)");
        if (m.Success) caps.FfmpegVersion = m.Groups[1].Value;

        var enc = await ProcessUtil.RunAsync(ffmpeg, new[] { "-hide_banner", "-encoders" }, 10000);
        foreach (var line in enc.Stdout.Split('\n'))
        {
            var parts = line.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2 && parts[0].Length == 6 && parts[0][0] == 'V') caps.Listed.Add(parts[1]);
        }

        var dev = await ProcessUtil.RunAsync(ffmpeg, new[] { "-hide_banner", "-devices" }, 8000);
        caps.HasPulse = Regex.IsMatch(dev.Stdout, @"\bpulse\b");
        caps.HasX11Grab = dev.Stdout.Contains("x11grab");

        caps.VaapiDevice = FindRenderNode();

        var tasks = new List<Task>();
        foreach (var b in new[] { "h264", "hevc", "av1" })
        {
            if (caps.Has(b + "_nvenc")) tasks.Add(TestAsync(ffmpeg, caps, b + "_nvenc", null));
            if (caps.Has(b + "_vaapi") && caps.VaapiDevice != null) tasks.Add(TestAsync(ffmpeg, caps, b + "_vaapi", caps.VaapiDevice));
        }
        await Task.WhenAll(tasks);
        foreach (var s in new[] { "libx264", "libx265", "libsvtav1", "libaom-av1" })
            if (caps.Has(s)) caps.Working.Add(s);

        Log.Write($"ffmpeg {caps.FfmpegVersion} — encodeurs matériels OK : " +
                  (caps.Working.Any(w => w.Contains("_")) ? string.Join(", ", caps.Working.Where(w => w.Contains("_")).OrderBy(w => w)) : "aucun"));
        return caps;
    }

    private static string? FindRenderNode()
    {
        try
        {
            if (!Directory.Exists("/dev/dri")) return null;
            return Directory.GetFiles("/dev/dri", "renderD*").OrderBy(f => f).FirstOrDefault();
        }
        catch { return null; }
    }

    private static async Task TestAsync(string ffmpeg, EncoderCaps caps, string encoder, string? vaapiDevice)
    {
        var args = new List<string> { "-hide_banner", "-loglevel", "error" };
        if (vaapiDevice != null) { args.Add("-vaapi_device"); args.Add(vaapiDevice); }
        args.AddRange(new[] { "-f", "lavfi", "-i", "color=c=black:s=320x240:r=30:d=0.5" });
        if (vaapiDevice != null) { args.Add("-vf"); args.Add("format=nv12,hwupload"); }
        args.AddRange(new[] { "-frames:v", "5", "-c:v", encoder, "-f", "null", "-" });
        var r = await ProcessUtil.RunAsync(ffmpeg, args, 12000);
        if (r.ExitCode == 0) lock (caps.Working) caps.Working.Add(encoder);
    }
}
