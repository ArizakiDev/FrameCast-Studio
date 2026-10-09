using System.Text;

namespace FrameCastStudio.Linux.Services;

public static class Log
{
    private static readonly object Gate = new();
    private static readonly Queue<string> Recent = new();

    /// <summary>Déclenché pour chaque ligne (peut venir de n'importe quel thread).</summary>
    public static event Action<string>? Line;

    public static void Write(string message)
    {
        string line = $"{DateTime.Now:HH:mm:ss} {message}";
        lock (Gate)
        {
            Recent.Enqueue(line);
            while (Recent.Count > 400) Recent.Dequeue();
            try
            {
                var fi = new FileInfo(Paths.LogFile);
                if (fi.Exists && fi.Length > 2_000_000)
                    File.Move(Paths.LogFile, Paths.LogFile + ".old", true);
                File.AppendAllText(Paths.LogFile, line + Environment.NewLine, Encoding.UTF8);
            }
            catch { }
        }
        try { Line?.Invoke(line); } catch { }
    }

    public static void Error(string context, Exception ex) => Write($"ERREUR {context}: {ex.GetType().Name}: {ex.Message}");

    public static string[] Snapshot()
    {
        lock (Gate) return Recent.ToArray();
    }
}
