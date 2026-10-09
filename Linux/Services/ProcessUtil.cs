using System.Diagnostics;
using System.Runtime.InteropServices;

namespace FrameCastStudio.Linux.Services;

public static class ProcessUtil
{
    [DllImport("libc", SetLastError = true)]
    private static extern int kill(int pid, int sig);

    public const int SIGINT = 2;
    public const int SIGTERM = 15;

    public static void Signal(Process p, int sig)
    {
        try { if (!p.HasExited) kill(p.Id, sig); } catch { }
    }

    public static bool Exists(string file) => FindOnPath(file) != null;

    public static string? FindOnPath(string name)
    {
        if (name.Contains('/')) return File.Exists(name) ? name : null;
        string? path = Environment.GetEnvironmentVariable("PATH");
        if (path == null) return null;
        foreach (var dir in path.Split(':', StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                string full = Path.Combine(dir, name);
                if (File.Exists(full)) return full;
            }
            catch { }
        }
        return null;
    }

    public sealed record Result(int ExitCode, string Stdout, string Stderr, bool TimedOut, bool Started);

    /// <summary>Lance un programme, attend sa fin (avec délai) et renvoie sa sortie. Ne lève jamais d'exception.</summary>
    public static async Task<Result> RunAsync(string file, IEnumerable<string> args, int timeoutMs = 10000,
        IDictionary<string, string>? env = null)
    {
        var psi = new ProcessStartInfo(file)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = false,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        if (env != null) foreach (var kv in env) psi.Environment[kv.Key] = kv.Value;

        Process? p = null;
        try
        {
            p = Process.Start(psi);
            if (p == null) return new Result(-1, "", "", false, false);
            var so = p.StandardOutput.ReadToEndAsync();
            var se = p.StandardError.ReadToEndAsync();
            using var cts = new CancellationTokenSource(timeoutMs);
            try { await p.WaitForExitAsync(cts.Token); }
            catch (OperationCanceledException)
            {
                try { p.Kill(true); } catch { }
                return new Result(-1, "", "", true, true);
            }
            return new Result(p.ExitCode, await so, await se, false, true);
        }
        catch (Exception ex)
        {
            return new Result(-1, "", ex.Message, false, false);
        }
        finally { p?.Dispose(); }
    }
}
