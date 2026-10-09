using System.Globalization;
using System.Text;

namespace FrameCastStudio.Linux.Services;

public sealed record VideoInput(bool IsPipe, string Display, int Width, int Height);

public sealed record BuiltCommand(List<string> Args, string Summary, bool HasAudio);

/// <summary>Construit la ligne de commande ffmpeg complète (entrées, filtres audio/vidéo, encodeurs, sorties tee).</summary>
public static class FfmpegCommand
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    private static string N(double v) => v.ToString("0.######", Inv);
    private static double Lin(double db) => Math.Pow(10, db / 20.0);
    private static double Clamp(double v, double lo, double hi) => Math.Min(hi, Math.Max(lo, v));

    public static BuiltCommand Build(SessionConfig c, EncoderChoice enc, VideoInput vin,
        string? recordPath, string? replayDir, string? liveUrl, string? vaapiDevice = null)
    {
        var a = new List<string>
        {
            "-hide_banner", "-loglevel", "warning", "-nostats", "-progress", "pipe:1", "-stats_period", "1", "-y",
        };

        // --- Entrée vidéo ---
        if (enc.Kind == EncoderKind.Vaapi && vaapiDevice is { } vd)
        {
            a.Add("-vaapi_device"); a.Add(vd);
        }
        a.AddRange(new[] { "-thread_queue_size", "512" });
        if (vin.IsPipe)
        {
            a.AddRange(new[] { "-f", "rawvideo", "-pix_fmt", "yuv420p", "-video_size", $"{vin.Width}x{vin.Height}",
                "-framerate", c.Fps.ToString(Inv), "-use_wallclock_as_timestamps", "1", "-i", "pipe:0" });
        }
        else
        {
            a.AddRange(new[] { "-f", "x11grab", "-framerate", c.Fps.ToString(Inv), "-draw_mouse", c.CaptureCursor ? "1" : "0" });
            string disp = string.IsNullOrEmpty(vin.Display) ? ":0" : vin.Display;
            if (c.CropEnabled && c.CropW > 1 && c.CropH > 1)
            {
                a.Add("-video_size"); a.Add($"{c.CropW & ~1}x{c.CropH & ~1}");
                disp += $"+{c.CropX},{c.CropY}";
            }
            a.Add("-i"); a.Add(disp);
        }

        // --- Entrées audio ---
        int nextIdx = 1;
        int loopIdx = -1, micIdx = -1;
        if (c.AudioLoopback)
        {
            a.AddRange(new[] { "-thread_queue_size", "1024", "-f", "pulse", "-i", c.LoopbackId });
            loopIdx = nextIdx++;
        }
        if (c.AudioMic)
        {
            a.AddRange(new[] { "-thread_queue_size", "1024", "-f", "pulse", "-i", c.MicId });
            micIdx = nextIdx++;
        }
        bool hasAudio = loopIdx >= 0 || micIdx >= 0;

        string? graph = hasAudio ? BuildAudioGraph(c, loopIdx, micIdx) : null;
        if (graph != null) { a.Add("-filter_complex"); a.Add(graph); }

        a.Add("-map"); a.Add("0:v:0");
        if (hasAudio) { a.Add("-map"); a.Add("[aout]"); }

        // --- Filtre vidéo ---
        a.Add("-vf"); a.Add(BuildVideoFilter(c, enc, vin));

        // --- Encodeur vidéo ---
        AddVideoEncoder(a, c, enc);
        a.AddRange(new[] { "-flags", "+global_header" });
        if (c.ColorSpace == "BT.709")
            a.AddRange(new[] { "-colorspace", "bt709", "-color_primaries", "bt709", "-color_trc", "bt709", "-color_range", "tv" });
        // hvc1 (compatibilité Apple) uniquement pour un MP4 seul : le FLV du direct refuse ce tag
        if (c.Codec == "HEVC" && liveUrl == null && c.Container != "MKV") a.AddRange(new[] { "-tag:v", "hvc1" });

        // --- Audio ---
        if (hasAudio)
        {
            a.AddRange(new[] { "-c:a", "aac", "-b:a", $"{c.AudioBitrateKbps}k", "-ar", c.AudioSampleRate.ToString(Inv), "-ac", "2" });
        }
        a.AddRange(new[] { "-max_muxing_queue_size", "2048" });

        // --- Sorties (muxer tee) ---
        var outs = new List<string>();
        bool mkv = c.Container == "MKV";
        if (recordPath != null)
        {
            string fmt = mkv ? "matroska" : "mp4";
            string movOpt = !mkv
                ? (c.Fragmented ? ":movflags=+frag_keyframe+empty_moov+default_base_moof" : ":movflags=+faststart")
                : "";
            if (c.Split && c.RateMode != "CQP")
            {
                int total = c.BitrateKbps + (hasAudio ? c.AudioBitrateKbps : 0);
                int seg = (int)Clamp(c.SplitGb * 8_000_000.0 / Math.Max(500, total), 60, 86400);
                string ext = mkv ? ".mkv" : ".mp4";
                string pattern = Path.Combine(Path.GetDirectoryName(recordPath)!, Path.GetFileNameWithoutExtension(recordPath) + "_%03d" + ext);
                string fo = (!mkv && c.Fragmented) ? @":segment_format_options=movflags\=+frag_keyframe+empty_moov+default_base_moof" : "";
                outs.Add($"[f=segment:segment_time={seg}:segment_format={fmt}:reset_timestamps=1{fo}]{pattern}");
            }
            else outs.Add($"[f={fmt}{movOpt}]{recordPath}");
        }
        if (liveUrl != null)
            outs.Add($"[f=flv:flvflags=no_duration_filesize:onfail=ignore]{liveUrl}");
        if (replayDir != null)
            outs.Add($"[f=segment:segment_time=2:segment_format=matroska:reset_timestamps=1:onfail=ignore]{Path.Combine(replayDir, "seg_%05d.mkv")}");

        a.Add("-f"); a.Add("tee"); a.Add(string.Join("|", outs));

        string summary = $"{enc.Label} · {c.RateMode}" + (c.RateMode == "CQP" ? $" {c.Qp}" : $" {c.BitrateKbps} kbps") + $" · {c.Fps} fps";
        return new BuiltCommand(a, summary, hasAudio);
    }

    // ---------------------------------------------------------------- vidéo

    private static string BuildVideoFilter(SessionConfig c, EncoderChoice enc, VideoInput vin)
    {
        var f = new List<string>();
        if (vin.IsPipe && c.CropEnabled && c.CropW > 1 && c.CropH > 1)
            f.Add($"crop={c.CropW & ~1}:{c.CropH & ~1}:{c.CropX}:{c.CropY}");
        f.Add($"fps={c.Fps}");
        string matrix = c.ColorSpace == "BT.709" ? "bt709" : "bt601";
        int p = Math.Max(10, Math.Min(100, c.ScalePercent));
        string size = p == 100
            ? "trunc(iw/2)*2:trunc(ih/2)*2"
            : $"trunc(iw*{p}/200)*2:trunc(ih*{p}/200)*2";
        f.Add($"scale={size}:flags=bicubic:out_color_matrix={matrix}:out_range=tv");
        f.Add(enc.Kind == EncoderKind.Vaapi ? "format=nv12,hwupload" : "format=yuv420p");
        return string.Join(",", f);
    }

    private static void AddVideoEncoder(List<string> a, SessionConfig c, EncoderChoice enc)
    {
        int k = Math.Max(500, c.BitrateKbps);
        int gop = Math.Max(1, c.Fps * Math.Max(1, c.GopSeconds));
        int bf = c.BFrames ? 2 : 0;
        string rate = c.Mode == "live" && c.RateMode == "CQP" ? "CBR" : c.RateMode;
        int buf = k * 2;

        a.Add("-c:v"); a.Add(enc.Name);
        switch (enc.Kind)
        {
            case EncoderKind.Software:
                if (enc.Name == "libx264" || enc.Name == "libx265")
                {
                    a.AddRange(new[] { "-preset", "veryfast" });
                    if (c.LowLatency) a.AddRange(new[] { "-tune", "zerolatency" });
                    if (rate == "CQP") a.AddRange(new[] { "-crf", c.Qp.ToString(Inv) });
                    else if (rate == "CBR")
                    {
                        a.AddRange(new[] { "-b:v", $"{k}k", "-minrate", $"{k}k", "-maxrate", $"{k}k", "-bufsize", $"{buf}k" });
                        if (enc.Name == "libx264") a.AddRange(new[] { "-x264-params", "nal-hrd=cbr:force-cfr=1" });
                    }
                    else
                    {
                        a.AddRange(new[] { "-b:v", $"{k}k" });
                        if (c.Vbv) a.AddRange(new[] { "-maxrate", $"{k * 3 / 2}k", "-bufsize", $"{buf}k" });
                    }
                    a.AddRange(new[] { "-bf", bf.ToString(Inv) });
                }
                else if (enc.Name == "libsvtav1")
                {
                    a.AddRange(new[] { "-preset", "10" });
                    if (rate == "CQP") a.AddRange(new[] { "-crf", Math.Min(63, c.Qp).ToString(Inv) });
                    else a.AddRange(new[] { "-b:v", $"{k}k" });
                }
                else // libaom-av1
                {
                    a.AddRange(new[] { "-cpu-used", "8", "-row-mt", "1" });
                    if (rate == "CQP") a.AddRange(new[] { "-crf", Math.Min(63, c.Qp).ToString(Inv), "-b:v", "0" });
                    else a.AddRange(new[] { "-b:v", $"{k}k" });
                }
                break;

            case EncoderKind.Nvenc:
                a.AddRange(new[] { "-preset", "p4", "-tune", c.LowLatency ? "ll" : "hq" });
                if (rate == "CQP") a.AddRange(new[] { "-rc", "constqp", "-qp", c.Qp.ToString(Inv) });
                else if (rate == "CBR") a.AddRange(new[] { "-rc", "cbr", "-b:v", $"{k}k", "-maxrate", $"{k}k", "-bufsize", $"{buf}k" });
                else a.AddRange(new[] { "-rc", "vbr", "-b:v", $"{k}k", "-maxrate", $"{k * 3 / 2}k", "-bufsize", $"{buf}k" });
                a.AddRange(new[] { "-bf", bf.ToString(Inv) });
                break;

            case EncoderKind.Vaapi:
                if (rate == "CQP") a.AddRange(new[] { "-rc_mode", "CQP", "-qp", c.Qp.ToString(Inv) });
                else if (rate == "CBR") a.AddRange(new[] { "-rc_mode", "CBR", "-b:v", $"{k}k", "-maxrate", $"{k}k" });
                else a.AddRange(new[] { "-rc_mode", "VBR", "-b:v", $"{k}k", "-maxrate", $"{k * 3 / 2}k" });
                a.AddRange(new[] { "-bf", bf.ToString(Inv) });
                break;
        }
        a.AddRange(new[] { "-g", gop.ToString(Inv), "-keyint_min", gop.ToString(Inv) });
    }

    // ---------------------------------------------------------------- audio

    private static string Strip(StripCfg s, int rate)
    {
        var f = new List<string> { $"aresample={rate}:async=1:first_pts=0", "aformat=sample_fmts=fltp:channel_layouts=stereo" };
        if (s.HighPass) f.Add($"highpass=f={N(Clamp(s.HighPassHz, 20, 1000))}");
        if (s.Gate)
            f.Add($"agate=threshold={N(Clamp(Lin(s.GateThresholdDb), 0.000977, 1))}:attack={N(Clamp(s.GateAttackMs, 0.01, 9000))}:release={N(Clamp(s.GateReleaseMs, 0.01, 9000))}");
        if (s.Comp)
            f.Add($"acompressor=threshold={N(Clamp(Lin(s.CompThresholdDb), 0.000977, 1))}:ratio={N(Clamp(s.CompRatio, 1, 20))}" +
                  $":attack={N(Clamp(s.CompAttackMs, 0.01, 2000))}:release={N(Clamp(s.CompReleaseMs, 0.01, 9000))}:makeup={N(Clamp(Lin(s.CompMakeupDb), 1, 64))}");
        if (s.DelayMs >= 1) { string d = ((int)s.DelayMs).ToString(Inv); f.Add($"adelay={d}|{d}"); }
        if (Math.Abs(s.Pan) > 0.001) f.Add($"stereotools=balance_out={N(Clamp(s.Pan, -1, 1))}");
        f.Add(s.Mute ? "volume=0" : $"volume={N(Clamp(s.GainDb, -60, 24))}dB");
        return string.Join(",", f);
    }

    private static string BuildAudioGraph(SessionConfig c, int loopIdx, int micIdx)
    {
        var sb = new StringBuilder();
        int rate = c.AudioSampleRate;
        string? loopLabel = null, micLabel = null;

        if (loopIdx >= 0) { sb.Append($"[{loopIdx}:a]{Strip(c.Loop, rate)}[loop];"); loopLabel = "[loop]"; }
        if (micIdx >= 0) { sb.Append($"[{micIdx}:a]{Strip(c.Mic, rate)}[mic];"); micLabel = "[mic]"; }

        string mixed;
        if (loopLabel != null && micLabel != null)
        {
            if (c.Ducking)
            {
                double ratio = Clamp(1 + c.DuckAmountDb / 3.0, 2, 20);
                sb.Append("[mic]asplit=2[micmain][micsc];");
                sb.Append($"[loop][micsc]sidechaincompress=threshold={N(Clamp(Lin(c.DuckThresholdDb), 0.000977, 1))}:ratio={N(ratio)}" +
                          $":attack={N(Clamp(c.DuckAttackMs, 0.01, 2000))}:release={N(Clamp(c.DuckReleaseMs, 0.01, 9000))}[loopd];");
                sb.Append("[loopd][micmain]amix=inputs=2:duration=longest:normalize=0[mix];");
            }
            else sb.Append("[loop][mic]amix=inputs=2:duration=longest:normalize=0[mix];");
            mixed = "[mix]";
        }
        else mixed = loopLabel ?? micLabel!;

        var master = new List<string> { c.MasterMute ? "volume=0" : $"volume={N(Clamp(c.MasterGainDb, -60, 24))}dB" };
        if (c.Limiter) master.Add($"alimiter=limit={N(Clamp(Lin(c.LimiterCeilingDb), 0.0625, 1))}:level=0:attack=5:release=50");
        sb.Append($"{mixed}{string.Join(",", master)}[aout]");
        return sb.ToString();
    }
}
