using System.Reflection;

namespace FrameCastStudio.App;

public static class AppInfo
{
    public const string Name = "FrameCast Studio";
    public const string ExeName = "FrameCastStudio.exe";

    public const string GitHubOwner = "ArizakiDev";
    public const string GitHubRepo = "FrameCast-Studio";
    public static string GitHubUrl => $"https://github.com/{GitHubOwner}/{GitHubRepo}";

    public static string CurrentVersion { get; } = ReadVersion();

    private static string ReadVersion()
    {
        var asm = typeof(AppInfo).Assembly;
        string? v = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (string.IsNullOrWhiteSpace(v)) v = asm.GetName().Version?.ToString(3);
        if (string.IsNullOrWhiteSpace(v)) return "0.0.0";
        int plus = v.IndexOf('+');
        return plus >= 0 ? v[..plus] : v;
    }
}
