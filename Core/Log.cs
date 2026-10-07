namespace FrameCastStudio.Core;

public static class Log
{
    private static readonly object L = new();
    private static bool _rotationChecked;

    public static int Level;
    public static readonly string PathFile = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FrameCastStudio", "log.txt");

    public static void Write(string msg, int level = 0)
    {
        if (level > Level) return;
        try
        {
            lock (L)
            {
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(PathFile)!);
                if (!_rotationChecked)
                {
                    _rotationChecked = true;
                    var fi = new FileInfo(PathFile);
                    if (fi.Exists && fi.Length > 1_000_000) File.Move(PathFile, PathFile + ".old", true);
                }
                File.AppendAllText(PathFile, $"{DateTime.Now:HH:mm:ss.fff} {msg}{Environment.NewLine}");
            }
        }
        catch { }
    }

    public static void Error(string where, Exception ex) => Write($"ERREUR [{where}] {ex}");
}
