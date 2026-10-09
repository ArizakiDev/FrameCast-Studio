namespace FrameCastStudio.Linux.Services;

public static class Paths
{
    public static string Home => Environment.GetEnvironmentVariable("HOME") is { Length: > 0 } h
        ? h : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    public static string ConfigDir { get; } = Ensure(Path.Combine(
        Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") is { Length: > 0 } c ? c : Path.Combine(Home, ".config"),
        "FrameCastStudio"));

    public static string DataDir { get; } = Ensure(Path.Combine(
        Environment.GetEnvironmentVariable("XDG_DATA_HOME") is { Length: > 0 } d ? d : Path.Combine(Home, ".local", "share"),
        "FrameCastStudio"));

    public static string SettingsFile => Path.Combine(ConfigDir, "settings.json");
    public static string ProfilesFile => Path.Combine(ConfigDir, "profiles.json");
    public static string LogFile => Path.Combine(DataDir, "framecast.log");

    public static string DefaultVideos => UserDir(Environment.SpecialFolder.MyVideos, "Videos");
    public static string DefaultPictures => UserDir(Environment.SpecialFolder.MyPictures, "Pictures");

    private static string UserDir(Environment.SpecialFolder f, string fallbackName)
    {
        string p = "";
        try { p = Environment.GetFolderPath(f); } catch { }
        if (string.IsNullOrWhiteSpace(p)) p = Path.Combine(Home, fallbackName);
        return Path.Combine(p, "FrameCast");
    }

    public static string RuntimeDir
    {
        get
        {
            string? x = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");
            return !string.IsNullOrEmpty(x) && Directory.Exists(x) ? x : Path.GetTempPath();
        }
    }

    private static string Ensure(string p)
    {
        try { Directory.CreateDirectory(p); } catch { }
        return p;
    }
}
