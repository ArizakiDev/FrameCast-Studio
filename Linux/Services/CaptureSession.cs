using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace FrameCastStudio.Linux.Services;

public sealed record SessionStats(string Fps, string Bitrate, string Size, string Elapsed, long Dropped, string Speed);

/// <summary>Une session = un processus ffmpeg (+ gst-launch pour Wayland) qui enregistre, diffuse et/ou alimente le buffer de replay.</summary>
public sealed class CaptureSession : IAsyncDisposable
{
    private readonly EncoderCaps _caps;
    private readonly string _ffmpeg;
    private Process? _ff, _gst;
    private PortalScreenCast? _portal;
    private Task? _pump, _readOut, _readErr, _cleaner;
    private CancellationTokenSource _cts = new();
    private readonly Queue<string> _errTail = new();
    private string? _replayDir;
    private SessionConfig? _cfg;
    private volatile bool _stopping;
    private long _startTicks;

    public string? RecordPath { get; private set; }
    public string Summary { get; private set; } = "";
    public bool IsRunning => _ff is { HasExited: false };
    public string? NewRestoreToken { get; private set; }

    public event Action<SessionStats>? Stats;
    public event Action<string>? Ended;   // message d'erreur ou "" si arrêt normal

    public CaptureSession(string ffmpeg, EncoderCaps caps) { _ffmpeg = ffmpeg; _caps = caps; }

    // ------------------------------------------------------------------ démarrage

    public static bool IsWaylandSession =>
        string.Equals(Environment.GetEnvironmentVariable("XDG_SESSION_TYPE"), "wayland", StringComparison.OrdinalIgnoreCase)
        || !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY"));

    public static bool UsePortal(string backend) => backend switch
    {
        "Portal" => true,
        "X11" => false,
        _ => IsWaylandSession || string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DISPLAY")),
    };

    public async Task StartAsync(SessionConfig cfg)
    {
        _cfg = cfg;
        var enc = _caps.Pick(cfg.Codec, cfg.EncoderMode)
                  ?? throw new InvalidOperationException(cfg.EncoderMode == "GPU"
                      ? "Aucun encodeur matériel fonctionnel pour ce codec (NVENC/VAAPI). Choisis « Auto » ou « CPU »."
                      : $"Aucun encodeur disponible pour {cfg.Codec} dans ce ffmpeg.");

        if (cfg.Mode == "live" && cfg.Codec != "H264")
            Log.Write("Attention : la plupart des plateformes RTMP (Twitch, YouTube…) n'acceptent que le H.264.");
        bool wantRecord = cfg.Mode == "rec" || (cfg.Mode == "live" && cfg.RecordWhileLive);
        bool wantReplay = cfg.Mode == "replay" || cfg.ReplayEnabled;
        string? live = null;
        if (cfg.Mode == "live")
        {
            if (string.IsNullOrWhiteSpace(cfg.IngestUrl)) throw new InvalidOperationException("URL d'ingest vide.");
            live = cfg.IngestUrl.TrimEnd('/') + (string.IsNullOrWhiteSpace(cfg.StreamKey) ? "" : "/" + cfg.StreamKey.Trim());
        }

        if (wantRecord)
        {
            Directory.CreateDirectory(cfg.OutputFolder);
            string ext = cfg.Container == "MKV" ? ".mkv" : ".mp4";
            string name = cfg.Prefix + (cfg.Timestamp ? "_" + DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss") : "");
            string path = Path.Combine(cfg.OutputFolder, name + ext);
            for (int i = 2; File.Exists(path); i++) path = Path.Combine(cfg.OutputFolder, $"{name}_{i}{ext}");
            RecordPath = path;
        }
        if (wantReplay)
        {
            _replayDir = Path.Combine(Path.GetTempPath(), "framecast-replay-" + Environment.ProcessId);
            try { if (Directory.Exists(_replayDir)) Directory.Delete(_replayDir, true); } catch { }
            Directory.CreateDirectory(_replayDir);
        }

        // --- source vidéo ---
        VideoInput vin;
        string? gstArgsNote = null;
        if (UsePortal(cfg.CaptureBackend))
        {
            await RequireGstreamerAsync();
            _portal = new PortalScreenCast();
            var st = await _portal.StartAsync(cfg.CaptureCursor, cfg.PortalRestoreToken, _cts.Token);
            NewRestoreToken = st.RestoreToken;
            int w = (st.Width > 0 ? st.Width : 1920) & ~1;
            int h = (st.Height > 0 ? st.Height : 1080) & ~1;
            vin = new VideoInput(true, "", w, h);
            gstArgsNote = $"PipeWire nœud {st.NodeId} ({w}x{h})";
            StartGst(st.NodeId, w, h, cfg.Fps);
        }
        else
        {
            string disp = Environment.GetEnvironmentVariable("DISPLAY") ?? "";
            if (string.IsNullOrEmpty(disp))
                throw new InvalidOperationException("Aucun affichage X11 (DISPLAY vide). Utilise la capture « Portail » dans les réglages.");
            if (!_caps.HasX11Grab) throw new InvalidOperationException("Ce ffmpeg n'a pas la capture X11 (x11grab).");
            vin = new VideoInput(false, disp, 0, 0);
        }

        if ((cfg.AudioLoopback || cfg.AudioMic) && !_caps.HasPulse)
        {
            Log.Write("Ce ffmpeg n'a pas l'entrée PulseAudio : audio désactivé.");
            cfg.AudioLoopback = false; cfg.AudioMic = false;
        }

        var cmd = FfmpegCommand.Build(cfg, enc, vin, RecordPath, _replayDir, live, enc.Kind == EncoderKind.Vaapi ? _caps.VaapiDevice : null);
        Summary = cmd.Summary + (gstArgsNote != null ? " · " + gstArgsNote : "");
        Log.Write("ffmpeg " + string.Join(" ", cmd.Args.Select(Redact(cfg))));

        var psi = new ProcessStartInfo(_ffmpeg)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var x in cmd.Args) psi.ArgumentList.Add(x);

        _ff = Process.Start(psi) ?? throw new InvalidOperationException("Impossible de lancer ffmpeg.");
        _startTicks = Stopwatch.GetTimestamp();
        _readOut = Task.Run(ReadProgress);
        _readErr = Task.Run(ReadStderr);
        if (_gst != null) _pump = Task.Run(Pump);
        _ff.EnableRaisingEvents = true;
        _ff.Exited += (_, _) => OnFfmpegExited();

        // On laisse 2 s à ffmpeg pour échouer proprement (mauvaise option, encodeur absent...)
        await Task.Delay(2000);
        if (_ff.HasExited)
        {
            string err = TailText();
            await CleanupAsync();
            throw new InvalidOperationException("ffmpeg s'est arrêté immédiatement :\n" + err);
        }

        if (_replayDir != null) _cleaner = Task.Run(() => CleanReplayAsync(cfg.ReplaySeconds));
    }

    private static Func<string, string> Redact(SessionConfig cfg) => s =>
        string.IsNullOrWhiteSpace(cfg.StreamKey) ? s : s.Replace(cfg.StreamKey.Trim(), "****");

    // ------------------------------------------------------------------ Wayland : gst-launch -> ffmpeg

    private static async Task RequireGstreamerAsync()
    {
        if (ProcessUtil.FindOnPath("gst-launch-1.0") == null || ProcessUtil.FindOnPath("gst-inspect-1.0") == null)
            throw new InvalidOperationException(GstHelp("GStreamer n'est pas installé."));
        var r = await ProcessUtil.RunAsync(ProcessUtil.FindOnPath("gst-inspect-1.0")!, new[] { "pipewiresrc" }, 8000);
        if (r.ExitCode != 0) throw new InvalidOperationException(GstHelp("Le plugin GStreamer PipeWire est absent."));
    }

    private static string GstHelp(string head) => head + "\nInstalle-le puis relance :\n" +
        "  Debian/Ubuntu/Mint : sudo apt install gstreamer1.0-pipewire gstreamer1.0-plugins-base gstreamer1.0-plugins-good\n" +
        "  Fedora            : sudo dnf install pipewire-gstreamer gstreamer1-plugins-good\n" +
        "  Arch/Manjaro      : sudo pacman -S gst-plugin-pipewire gst-plugins-good\n" +
        "  openSUSE          : sudo zypper install gstreamer-plugin-pipewire gstreamer-plugins-good";

    private void StartGst(uint node, int w, int h, int fps)
    {
        var psi = new ProcessStartInfo(ProcessUtil.FindOnPath("gst-launch-1.0")!)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var x in new[]
        {
            "-q", "pipewiresrc", $"path={node}", "do-timestamp=true", "keepalive-time=500", "resend-last=true",
            "!", "videoconvert", "!", "videoscale", "!", "videorate",
            "!", $"video/x-raw,format=I420,width={w},height={h},framerate={fps}/1",
            "!", "queue", "max-size-buffers=8", "leaky=downstream",
            "!", "fdsink", "fd=1", "sync=false",
        }) psi.ArgumentList.Add(x);
        _gst = Process.Start(psi) ?? throw new InvalidOperationException("Impossible de lancer gst-launch-1.0.");
        _ = Task.Run(async () =>
        {
            try { string? l; while ((l = await _gst.StandardError.ReadLineAsync()) != null) Log.Write("gst: " + l); } catch { }
        });
    }

    private async Task Pump()
    {
        try
        {
            await _gst!.StandardOutput.BaseStream.CopyToAsync(_ff!.StandardInput.BaseStream, 1 << 20, CancellationToken.None);
        }
        catch { }
        finally { try { _ff!.StandardInput.Close(); } catch { } }
    }

    // ------------------------------------------------------------------ lecture des sorties ffmpeg

    private async Task ReadStderr()
    {
        try
        {
            string? l;
            while ((l = await _ff!.StandardError.ReadLineAsync()) != null)
            {
                lock (_errTail) { _errTail.Enqueue(l); while (_errTail.Count > 40) _errTail.Dequeue(); }
                Log.Write("ffmpeg: " + l);
            }
        }
        catch { }
    }

    private string TailText() { lock (_errTail) return string.Join("\n", _errTail); }

    private async Task ReadProgress()
    {
        var kv = new Dictionary<string, string>();
        try
        {
            string? l;
            while ((l = await _ff!.StandardOutput.ReadLineAsync()) != null)
            {
                int i = l.IndexOf('=');
                if (i <= 0) continue;
                string k = l[..i], v = l[(i + 1)..].Trim();
                kv[k] = v;
                if (k != "progress") continue;
                try { Stats?.Invoke(ToStats(kv)); } catch { }
                kv.Clear();
            }
        }
        catch { }
    }

    private SessionStats ToStats(Dictionary<string, string> kv)
    {
        string Get(string k) => kv.TryGetValue(k, out var v) ? v : "";
        string fps = double.TryParse(Get("fps"), NumberStyles.Float, CultureInfo.InvariantCulture, out var f) ? f.ToString("0.0") : "–";
        string br = Get("bitrate");
        br = br.EndsWith("kbits/s") && double.TryParse(br[..^7], NumberStyles.Float, CultureInfo.InvariantCulture, out var kb)
            ? $"{kb:0} kbps" : "–";
        string size = long.TryParse(Get("total_size"), out var bytes) && bytes > 0 ? FormatSize(bytes) : "–";
        long.TryParse(Get("drop_frames"), out var drop);
        var el = TimeSpan.FromSeconds(Stopwatch.GetElapsedTime(_startTicks).TotalSeconds);
        return new SessionStats(fps, br, size, el.ToString(@"hh\:mm\:ss"), drop, Get("speed"));
    }

    public static string FormatSize(long b) =>
        b >= 1L << 30 ? $"{b / (double)(1L << 30):0.00} Go" : b >= 1L << 20 ? $"{b / (double)(1L << 20):0.0} Mo" : $"{b / 1024.0:0} Ko";

    // ------------------------------------------------------------------ fin de session

    private void OnFfmpegExited()
    {
        if (_stopping) return;
        string err = "";
        try { if (_ff!.ExitCode != 0) err = "ffmpeg s'est arrêté (code " + _ff.ExitCode + ").\n" + TailText(); } catch { }
        if (err == "" && _gst is { HasExited: true } && !_stopping) err = "La capture d'écran s'est interrompue (partage terminé ?).";
        _ = Task.Run(async () => { await CleanupAsync(); Ended?.Invoke(err); });
    }

    public async Task StopAsync()
    {
        if (_ff == null) return;
        _stopping = true;
        try
        {
            if (_gst != null)
            {
                ProcessUtil.Signal(_gst, ProcessUtil.SIGINT);
                await Task.Delay(300);
                try { if (!_gst.HasExited) _gst.Kill(true); } catch { }
                // la pompe ferme stdin de ffmpeg => fin de flux propre
            }
            else
            {
                try { await _ff.StandardInput.WriteAsync("q"); await _ff.StandardInput.FlushAsync(); } catch { }
            }

            if (!await WaitExitAsync(_ff, 12000))
            {
                ProcessUtil.Signal(_ff, ProcessUtil.SIGINT);
                if (!await WaitExitAsync(_ff, 8000)) { try { _ff.Kill(true); } catch { } }
            }
        }
        catch (Exception ex) { Log.Error("Arrêt de la session", ex); }
        await CleanupAsync(keepReplay: true);
        Ended?.Invoke("");
    }

    private static async Task<bool> WaitExitAsync(Process p, int ms)
    {
        using var cts = new CancellationTokenSource(ms);
        try { await p.WaitForExitAsync(cts.Token); return true; } catch (OperationCanceledException) { return p.HasExited; }
    }

    private async Task CleanupAsync(bool keepReplay = false)
    {
        try { _cts.Cancel(); } catch { }
        try { if (_gst is { HasExited: false }) _gst.Kill(true); } catch { }
        if (_portal != null) { try { await _portal.CloseAsync(); } catch { } _portal = null; }
        if (!keepReplay) DeleteReplayDir();
    }

    private void DeleteReplayDir()
    {
        try { if (_replayDir != null && Directory.Exists(_replayDir)) Directory.Delete(_replayDir, true); } catch { }
    }

    // ------------------------------------------------------------------ replay

    private async Task CleanReplayAsync(int seconds)
    {
        int keep = Math.Max(4, seconds / 2 + 4);
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(2000, _cts.Token);
                var files = Directory.GetFiles(_replayDir!, "seg_*.mkv").OrderBy(f => f, StringComparer.Ordinal).ToList();
                for (int i = 0; i < files.Count - keep; i++) { try { File.Delete(files[i]); } catch { } }
            }
            catch (OperationCanceledException) { break; }
            catch { }
        }
    }

    /// <summary>Assemble les N dernières secondes du buffer dans un MP4 sans ré-encoder.</summary>
    public async Task<string?> SaveReplayAsync()
    {
        if (_replayDir == null || _cfg == null || !Directory.Exists(_replayDir)) return null;
        int need = Math.Max(2, (int)Math.Ceiling(_cfg.ReplaySeconds / 2.0) + 1);
        var files = Directory.GetFiles(_replayDir, "seg_*.mkv").OrderBy(f => f, StringComparer.Ordinal).ToList();
        if (files.Count < 2) return null;
        var take = files.Skip(Math.Max(0, files.Count - need)).ToList();

        Directory.CreateDirectory(_cfg.OutputFolder);
        string outPath = Path.Combine(_cfg.OutputFolder, $"Replay_{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.mp4");
        string list = Path.Combine(_replayDir, "concat_" + Guid.NewGuid().ToString("N")[..6] + ".txt");
        var sb = new StringBuilder();
        foreach (var f in take) sb.Append("file '").Append(f.Replace("'", "'\\''")).Append("'\n");
        await File.WriteAllTextAsync(list, sb.ToString());

        var r = await ProcessUtil.RunAsync(_ffmpeg, new[]
        {
            "-hide_banner", "-loglevel", "error", "-y", "-f", "concat", "-safe", "0", "-i", list,
            "-c", "copy", "-movflags", "+faststart", outPath,
        }, 60000);
        try { File.Delete(list); } catch { }
        if (r.ExitCode != 0) { Log.Write("Sauvegarde du replay échouée : " + r.Stderr); return null; }
        return outPath;
    }

    public async ValueTask DisposeAsync()
    {
        if (IsRunning) await StopAsync();
        await CleanupAsync();
        _ff?.Dispose(); _gst?.Dispose();
    }
}
