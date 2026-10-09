using System.Net.Http.Headers;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;

namespace FrameCastStudio.Linux.Services;

public sealed record UpdateInfo(string Tag, Version Version, string Notes, string PageUrl, string? AssetName, string? AssetUrl, string? Sha256);

/// <summary>Vérifie les releases GitHub. Les AppImage peuvent se remplacer eux-mêmes ; les autres formats ouvrent la page de la release.</summary>
public static class Updater
{
    private const string Api = "https://api.github.com/repos/ArizakiDev/FrameCast-Studio/releases/latest";
    private static readonly HttpClient Http = Make();

    public static string CurrentVersionText { get; } = ReadVersion();
    public static Version Current { get; } = Version.TryParse(CurrentVersionText, out var v) ? v : new Version(1, 1, 0);
    public static string? AppImagePath => Environment.GetEnvironmentVariable("APPIMAGE") is { Length: > 0 } p && File.Exists(p) ? p : null;
    public static string ArchTag => RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "linux-arm64" : "linux-x64";

    private static HttpClient Make()
    {
        var h = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        h.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("FrameCastStudio-Linux", "1.1"));
        h.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return h;
    }

    private static string ReadVersion()
    {
        var asm = Assembly.GetExecutingAssembly();
        string? v = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (string.IsNullOrWhiteSpace(v)) v = asm.GetName().Version?.ToString(3);
        v ??= "1.1.0";
        int plus = v.IndexOf('+');
        return plus >= 0 ? v[..plus] : v;
    }

    public static async Task<UpdateInfo?> CheckAsync()
    {
        using var resp = await Http.GetAsync(Api);
        if (!resp.IsSuccessStatusCode) throw new InvalidOperationException($"GitHub a répondu {(int)resp.StatusCode}.");
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        var root = doc.RootElement;
        if (root.TryGetProperty("prerelease", out var pre) && pre.GetBoolean()) return null;
        string tag = root.GetProperty("tag_name").GetString() ?? "";
        if (!Version.TryParse(tag.TrimStart('v', 'V').Split('-', '+')[0], out var ver)) return null;
        string notes = root.TryGetProperty("body", out var b) ? b.GetString() ?? "" : "";
        string page = root.TryGetProperty("html_url", out var u) ? u.GetString() ?? "" : "";

        string? an = null, au = null, sha = null;
        if (root.TryGetProperty("assets", out var assets))
        {
            bool wantAppImage = AppImagePath != null;
            foreach (var a in assets.EnumerateArray())
            {
                string name = a.GetProperty("name").GetString() ?? "";
                if (!name.Contains(ArchTag, StringComparison.OrdinalIgnoreCase)) continue;
                bool isAi = name.EndsWith(".AppImage", StringComparison.OrdinalIgnoreCase);
                if (wantAppImage != isAi) continue;
                an = name; au = a.GetProperty("browser_download_url").GetString();
                if (a.TryGetProperty("digest", out var d) && d.GetString() is { } ds && ds.StartsWith("sha256:")) sha = ds[7..];
                break;
            }
        }
        return ver > Current ? new UpdateInfo(tag, ver, notes, page, an, au, sha) : null;
    }

    public static bool CanSelfInstall(UpdateInfo u) => AppImagePath != null && u.AssetUrl != null;

    /// <summary>Télécharge le nouvel AppImage à côté de l'ancien puis le renomme par-dessus (autorisé sous Linux même en cours d'exécution).</summary>
    public static async Task InstallAppImageAsync(UpdateInfo u, IProgress<double>? progress)
    {
        string target = AppImagePath ?? throw new InvalidOperationException("Pas lancé depuis un AppImage.");
        string tmp = target + ".new";
        using (var resp = await Http.GetAsync(u.AssetUrl, HttpCompletionOption.ResponseHeadersRead))
        {
            resp.EnsureSuccessStatusCode();
            long total = resp.Content.Headers.ContentLength ?? -1;
            await using var src = await resp.Content.ReadAsStreamAsync();
            await using var dst = File.Create(tmp);
            var buf = new byte[1 << 16]; long done = 0; int n;
            while ((n = await src.ReadAsync(buf)) > 0)
            {
                await dst.WriteAsync(buf.AsMemory(0, n));
                done += n;
                if (total > 0) progress?.Report(done * 100.0 / total);
            }
        }
        if (u.Sha256 != null)
        {
            await using var fs = File.OpenRead(tmp);
            string hex = Convert.ToHexString(await SHA256.HashDataAsync(fs));
            if (!hex.Equals(u.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                File.Delete(tmp);
                throw new InvalidOperationException("Somme SHA-256 invalide : mise à jour annulée.");
            }
        }
        File.SetUnixFileMode(tmp, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                                  UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        File.Move(tmp, target, true);
    }
}
