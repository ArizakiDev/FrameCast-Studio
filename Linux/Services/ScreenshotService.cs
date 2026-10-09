namespace FrameCastStudio.Linux.Services;

public static class ScreenshotService
{
    /// <returns>(chemin du fichier, jeton de restauration éventuel) ou null en cas d'échec.</returns>
    public static async Task<(string Path, string? Token)?> TakeAsync(string ffmpeg, string folder, string format,
        string backend, bool cursor, string? restoreToken, bool copyToClipboard)
    {
        Directory.CreateDirectory(folder);
        string ext = format.ToLowerInvariant() switch { "jpeg" or "jpg" => "jpg", "bmp" => "bmp", _ => "png" };
        string file = Path.Combine(folder, $"Capture_{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.{ext}");
        string? newToken = null;

        if (CaptureSession.UsePortal(backend))
        {
            if (ProcessUtil.FindOnPath("gst-launch-1.0") == null)
                throw new InvalidOperationException("GStreamer est requis pour la capture d'écran sous Wayland (gstreamer1.0-pipewire).");
            await using var portal = new PortalScreenCast();
            var st = await portal.StartAsync(cursor, restoreToken);
            newToken = st.RestoreToken;
            string tmp = Path.Combine(Path.GetTempPath(), $"framecast-shot-{Guid.NewGuid():N}.png");
            var r = await ProcessUtil.RunAsync(ProcessUtil.FindOnPath("gst-launch-1.0")!, new[]
            {
                "-q", "pipewiresrc", $"path={st.NodeId}", "num-buffers=2", "!", "videoconvert", "!", "pngenc", "snapshot=true",
                "!", "filesink", $"location={tmp}",
            }, 15000);
            if (r.ExitCode != 0 || !File.Exists(tmp)) { Log.Write("Capture portail échouée : " + r.Stderr); return null; }
            if (ext == "png") File.Move(tmp, file, true);
            else
            {
                var c = await ProcessUtil.RunAsync(ffmpeg, new[] { "-hide_banner", "-loglevel", "error", "-y", "-i", tmp, file }, 15000);
                try { File.Delete(tmp); } catch { }
                if (c.ExitCode != 0) return null;
            }
        }
        else
        {
            string disp = Environment.GetEnvironmentVariable("DISPLAY") ?? ":0";
            var r = await ProcessUtil.RunAsync(ffmpeg, new[]
            {
                "-hide_banner", "-loglevel", "error", "-y", "-f", "x11grab", "-draw_mouse", cursor ? "1" : "0",
                "-i", disp, "-frames:v", "1", "-update", "1", file,
            }, 15000);
            if (r.ExitCode != 0 || !File.Exists(file)) { Log.Write("Capture X11 échouée : " + r.Stderr); return null; }
        }

        if (copyToClipboard) await CopyToClipboardAsync(file, ext);
        return (file, newToken);
    }

    private static async Task CopyToClipboardAsync(string file, string ext)
    {
        string mime = ext == "jpg" ? "image/jpeg" : ext == "bmp" ? "image/bmp" : "image/png";
        try
        {
            string? wl = ProcessUtil.FindOnPath("wl-copy");
            string? xc = ProcessUtil.FindOnPath("xclip");
            System.Diagnostics.ProcessStartInfo? psi = null;
            if (wl != null && !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY")))
            { psi = new(wl); psi.ArgumentList.Add("-t"); psi.ArgumentList.Add(mime); }
            else if (xc != null)
            { psi = new(xc); foreach (var x in new[] { "-selection", "clipboard", "-t", mime, "-i", file }) psi.ArgumentList.Add(x); }
            if (psi == null) return;
            psi.UseShellExecute = false;
            psi.RedirectStandardInput = wl != null && psi.FileName == wl;
            using var p = System.Diagnostics.Process.Start(psi);
            if (p == null) return;
            if (psi.RedirectStandardInput)
            {
                await using (var fs = File.OpenRead(file)) await fs.CopyToAsync(p.StandardInput.BaseStream);
                p.StandardInput.Close();
            }
            // xclip reste en arrière-plan pour servir le presse-papiers : ne pas l'attendre indéfiniment
            await Task.WhenAny(p.WaitForExitAsync(), Task.Delay(1500));
        }
        catch (Exception ex) { Log.Error("Presse-papiers", ex); }
    }
}
