namespace FrameCastStudio.Linux.Services;

public static class FfmpegLocator
{
    public static string? FilePath { get; private set; }
    public static bool Bundled { get; private set; }

    /// <summary>Ordre : variable FRAMECAST_FFMPEG, ffmpeg embarqué à côté de l'exécutable, puis le PATH.</summary>
    public static string? Find()
    {
        string? env = Environment.GetEnvironmentVariable("FRAMECAST_FFMPEG");
        if (!string.IsNullOrWhiteSpace(env) && File.Exists(env)) { FilePath = env; Bundled = false; return env; }

        string baseDir = AppContext.BaseDirectory;
        foreach (var cand in new[]
        {
            System.IO.Path.Combine(baseDir, "ffmpeg", "ffmpeg"),
            System.IO.Path.Combine(baseDir, "ffmpeg"),
        })
        {
            if (!File.Exists(cand)) continue;
            EnsureExecutable(cand);
            FilePath = cand; Bundled = true;
            return cand;
        }

        string? sys = ProcessUtil.FindOnPath("ffmpeg");
        FilePath = sys; Bundled = false;
        return sys;
    }

    private static void EnsureExecutable(string file)
    {
        try
        {
            var mode = File.GetUnixFileMode(file);
            var want = mode | UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute;
            if (want != mode) File.SetUnixFileMode(file, want);
        }
        catch { }
    }
}
