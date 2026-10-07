using System.Buffers;
using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace FrameCastStudio.Core.Update;

public sealed record ReleaseInfo(string Version, string HtmlUrl, string? AssetUrl, string? AssetName, long AssetSize,
                                 string? Sha256, string? ChecksumUrl, string Notes);

public enum UpdateCheckStatus { UpToDate, Available, NotConfigured, Error }

public sealed record UpdateCheckResult(UpdateCheckStatus Status, ReleaseInfo? Release = null, string Message = "");

public sealed record StagedUpdate(string Version, string StageDir, string SourceDir);

public sealed record UpdateOutcome(bool Success, string Version, string Message);

public sealed class GitHubUpdater
{
    public const string PlaceholderOwner = "YOUR_GITHUB_USERNAME";

    private readonly string _owner, _repo, _exeName;

    public GitHubUpdater(string owner, string repo, string exeName)
    {
        _owner = owner?.Trim() ?? ""; _repo = repo?.Trim() ?? ""; _exeName = exeName;
    }

    public bool IsConfigured =>
        _owner.Length > 0 && !string.Equals(_owner, PlaceholderOwner, StringComparison.Ordinal) && _repo.Length > 0;

    public static string UpdateDir { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FrameCastStudio", "update");

    private static readonly HttpClient Api = BuildClient(TimeSpan.FromSeconds(20), json: true);
    private static readonly HttpClient Dl = BuildClient(Timeout.InfiniteTimeSpan, json: false);

    private static HttpClient BuildClient(TimeSpan timeout, bool json)
    {
        var c = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = true, MaxAutomaticRedirections = 8 }) { Timeout = timeout };
        c.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("FrameCastStudio-Updater", "1.0"));
        if (json)
        {
            c.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
            c.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
        }
        return c;
    }

    public static bool TryParseVersion(string? text, out Version version)
    {
        version = new Version(0, 0, 0, 0);
        if (string.IsNullOrWhiteSpace(text)) return false;
        string s = text.Trim().TrimStart('v', 'V');
        int cut = s.IndexOfAny(new[] { '-', '+', ' ' });
        if (cut >= 0) s = s[..cut];
        var parts = s.Split('.');
        if (parts.Length is 0 or > 4) return false;
        var n = new int[4];
        for (int i = 0; i < parts.Length; i++)
            if (!int.TryParse(parts[i], NumberStyles.None, CultureInfo.InvariantCulture, out n[i])) return false;
        version = new Version(n[0], n[1], n[2], n[3]);
        return true;
    }

    private static string ArchToken() =>
        RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "win-arm64" : "win-x64";

    public async Task<UpdateCheckResult> CheckAsync(string currentVersion, CancellationToken ct = default)
    {
        if (!IsConfigured)
            return new(UpdateCheckStatus.NotConfigured, null, "Mises à jour non configurées (renseigne GitHubOwner dans App/AppInfo.cs).");
        if (!TryParseVersion(currentVersion, out var vCur))
            return new(UpdateCheckStatus.Error, null, $"Version actuelle illisible : « {currentVersion} ».");

        try
        {
            using var resp = await Api.GetAsync($"https://api.github.com/repos/{_owner}/{_repo}/releases/latest", ct).ConfigureAwait(false);
            if (resp.StatusCode == HttpStatusCode.NotFound)
                return Fail("Aucune release publiée sur GitHub (ou dépôt introuvable / privé : il doit être public).");
            if (resp.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests)
                return Fail("Limite de requêtes GitHub atteinte : réessaie dans quelques minutes.");
            if (!resp.IsSuccessStatusCode)
                return Fail($"GitHub a répondu {(int)resp.StatusCode} {resp.ReasonPhrase}.");

            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
            var root = doc.RootElement;
            string tag = Str(root, "tag_name");
            if (!TryParseVersion(tag, out var vLatest))
                return Fail($"Tag de release illisible : « {tag} » (attendu : v1.2.3).");

            string latest = tag.Trim().TrimStart('v', 'V');
            string htmlUrl = Str(root, "html_url");
            string notes = Str(root, "body");

            string? assetUrl = null, assetName = null, sha = null, checksumUrl = null; long size = 0;
            if (root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
            {
                string arch = ArchToken();
                foreach (var a in assets.EnumerateArray())
                {
                    string name = Str(a, "name");
                    if (name.Contains(arch, StringComparison.OrdinalIgnoreCase) && name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                    {
                        assetName = name; assetUrl = Str(a, "browser_download_url");
                        size = a.TryGetProperty("size", out var sz) && sz.TryGetInt64(out var sv) ? sv : 0;
                        string digest = Str(a, "digest");
                        if (digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase)) sha = digest[7..].Trim().ToLowerInvariant();
                        break;
                    }
                }
                if (assetName != null)
                    foreach (var a in assets.EnumerateArray())
                        if (string.Equals(Str(a, "name"), assetName + ".sha256", StringComparison.OrdinalIgnoreCase))
                        { checksumUrl = Str(a, "browser_download_url"); break; }
            }

            if (vLatest <= vCur)
                return new(UpdateCheckStatus.UpToDate, null, $"À jour (version {currentVersion}).");

            return new(UpdateCheckStatus.Available,
                new ReleaseInfo(latest, htmlUrl, string.IsNullOrEmpty(assetUrl) ? null : assetUrl, assetName, size, sha, checksumUrl, notes),
                $"Nouvelle version disponible : {latest}");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (OperationCanceledException) { return Fail("GitHub ne répond pas (délai dépassé)."); }
        catch (HttpRequestException) { return Fail("Impossible de joindre GitHub (connexion indisponible ?)."); }
        catch (Exception ex) { Log.Error("Updater.Check", ex); return Fail("Vérification impossible : " + ex.Message); }

        static UpdateCheckResult Fail(string m) => new(UpdateCheckStatus.Error, null, m);
        static string Str(JsonElement e, string prop) =>
            e.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String ? (v.GetString() ?? "") : "";
    }

    public async Task<StagedUpdate> DownloadAndStageAsync(ReleaseInfo info, IProgress<double>? progress, CancellationToken ct = default)
    {
        if (info.AssetUrl is null)
            throw new InvalidOperationException($"La release {info.Version} ne contient pas de package .zip pour cette architecture (attendu : *{ArchToken()}*.zip).");
        if (!TryParseVersion(info.Version, out var vRelease))
            throw new InvalidOperationException("Version de release illisible : " + info.Version);

        string stage = Path.Combine(UpdateDir, SafeName(info.Version));
        string extracted = Path.Combine(stage, "extracted");
        string marker = Path.Combine(stage, "ready.txt");

        if (File.Exists(marker) && FindSourceDir(extracted, _exeName) is { } already)
        {
            Log.Write($"Mise à jour {info.Version} : paquet déjà prêt");
            progress?.Report(1);
            return new StagedUpdate(info.Version, stage, already);
        }

        ForceDelete(stage);
        Directory.CreateDirectory(stage);
        string zip = Path.Combine(stage, "package.zip"), part = zip + ".part";

        try
        {
            string? expected = info.Sha256 ?? await FetchExpectedSha256Async(info, ct).ConfigureAwait(false);
            EnsureFreeSpace(stage, Math.Max(info.AssetSize, 50L << 20) * 3);

            string actual = await DownloadAsync(info.AssetUrl, part, info.AssetSize, progress, ct).ConfigureAwait(false);

            long got = new FileInfo(part).Length;
            if (info.AssetSize > 0 && got != info.AssetSize)
                throw new InvalidDataException($"Téléchargement incomplet ({got} / {info.AssetSize} octets).");
            if (expected is null) Log.Write("Mise à jour : aucune empreinte SHA-256 publiée, contrôle de taille + contenu uniquement");
            else if (!string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("L'empreinte SHA-256 du paquet ne correspond pas : téléchargement corrompu ou paquet modifié.");

            File.Move(part, zip, true);

            using (var za = ZipFile.OpenRead(zip))
            {
                bool hasExe = za.Entries.Any(e => string.Equals(Path.GetFileName(e.FullName), _exeName, StringComparison.OrdinalIgnoreCase));
                if (!hasExe) throw new InvalidDataException($"Le package ne contient pas {_exeName}.");
            }

            ZipFile.ExtractToDirectory(zip, extracted, overwriteFiles: true);
            string source = FindSourceDir(extracted, _exeName)
                ?? throw new InvalidDataException($"{_exeName} introuvable après extraction.");

            var fvi = FileVersionInfo.GetVersionInfo(Path.Combine(source, _exeName));
            if (TryParseVersion(fvi.FileVersion ?? fvi.ProductVersion, out var vPkg) && vPkg < vRelease)
                throw new InvalidDataException($"La release est étiquetée {info.Version} mais l'exécutable du package est en version {vPkg.ToString(3)} : republie un zip reconstruit (publish.bat).");

            File.WriteAllText(marker, $"{info.Version}\n{actual}\n");
            try { File.Delete(zip); } catch { }
            Log.Write($"Mise à jour {info.Version} : paquet vérifié et extrait");
            return new StagedUpdate(info.Version, stage, source);
        }
        catch
        {
            ForceDelete(stage);
            throw;
        }
    }

    private async Task<string?> FetchExpectedSha256Async(ReleaseInfo info, CancellationToken ct)
    {
        if (info.ChecksumUrl is null) return null;
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(20));
            string txt = await Dl.GetStringAsync(info.ChecksumUrl, cts.Token).ConfigureAwait(false);
            foreach (var line in txt.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var tok = line.Trim().Split(' ', '\t', '*')[0].Trim().ToLowerInvariant();
                if (tok.Length == 64 && tok.All(Uri.IsHexDigit)) return tok;
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) { Log.Write("Empreinte SHA-256 illisible : " + ex.Message); }
        return null;
    }

    private static async Task<string> DownloadAsync(string url, string dest, long expectedSize, IProgress<double>? progress, CancellationToken ct)
    {
        using var resp = await Dl.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        long total = resp.Content.Headers.ContentLength ?? expectedSize;

        await using var src = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        await using var dst = new FileStream(dest, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using var idle = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var buf = ArrayPool<byte>.Shared.Rent(1 << 17);
        try
        {
            long done = 0; double lastReported = -1;
            while (true)
            {
                idle.CancelAfter(TimeSpan.FromSeconds(45));
                int n;
                try { n = await src.ReadAsync(buf.AsMemory(), idle.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                { throw new IOException("Téléchargement interrompu : aucune donnée reçue depuis 45 s."); }
                if (n == 0) break;
                sha.AppendData(buf, 0, n);
                await dst.WriteAsync(buf.AsMemory(0, n), ct).ConfigureAwait(false);
                done += n;
                if (total > 0 && progress != null)
                {
                    double p = Math.Min(0.99, (double)done / total);
                    if (p - lastReported >= 0.01) { lastReported = p; progress.Report(p); }
                }
            }
            await dst.FlushAsync(ct).ConfigureAwait(false);
        }
        finally { ArrayPool<byte>.Shared.Return(buf); }
        progress?.Report(1);
        return Convert.ToHexString(sha.GetHashAndReset()).ToLowerInvariant();
    }

    public void ApplyStaged(StagedUpdate staged, string installDir, int processId, bool relaunch, bool allowElevation)
    {
        installDir = Path.TrimEndingDirectorySeparator(Path.GetFullPath(installDir));
        ValidateInstallDir(installDir, _exeName);
        if (!File.Exists(Path.Combine(staged.SourceDir, _exeName)))
            throw new FileNotFoundException("Le paquet préparé a disparu : relance la vérification.", staged.SourceDir);

        bool needsAdmin = !IsDirectoryWritable(installDir);
        if (needsAdmin && !allowElevation)
            throw new UnauthorizedAccessException("Le dossier d'installation n'est pas modifiable sans droits administrateur.");

        Directory.CreateDirectory(UpdateDir);
        string backup = Path.Combine(UpdateDir, "backup");
        EnsureFreeSpace(UpdateDir, DirSize(installDir) + (100L << 20));

        string script = Path.Combine(UpdateDir, "apply_" + Guid.NewGuid().ToString("N")[..8] + ".cmd");
        string logPath = Path.Combine(Path.GetDirectoryName(UpdateDir)!, "update.log");
        string resultPath = UpdateState.ResultPath;
        File.WriteAllText(script, BuildScript(processId, _exeName, installDir, staged.SourceDir, backup, staged.StageDir, logPath, resultPath,
                                              relaunch, needsAdmin, staged.Version), new UTF8Encoding(false));
        Log.Write($"Mise à jour {staged.Version} : script lancé ({script}) admin={needsAdmin} relance={relaunch}");

        var psi = needsAdmin
            ? new ProcessStartInfo(script) { UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden }
            : new ProcessStartInfo("cmd.exe")
            {
                Arguments = $"/d /c \"\"{script}\"\"",
                UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = Path.GetTempPath(),
            };
        Process.Start(psi);
    }

    private static string BatEsc(string s) => s.Replace("%", "%%");

    internal static string BuildScript(int pid, string exe, string install, string source, string backup, string stage,
                                       string log, string result, bool relaunch, bool elevated, string version)
    {
        string body = $$"""
            @echo off
            setlocal EnableExtensions
            chcp 65001 >nul
            set "PID={{pid}}"
            set "EXE={{BatEsc(exe)}}"
            set "INSTALL={{BatEsc(install)}}"
            set "SRC={{BatEsc(source)}}"
            set "BACKUP={{BatEsc(backup)}}"
            set "STAGE={{BatEsc(stage)}}"
            set "LOG={{BatEsc(log)}}"
            set "RESULT={{BatEsc(result)}}"
            set "RELAUNCH={{(relaunch ? 1 : 0)}}"
            set "ELEVATED={{(elevated ? 1 : 0)}}"
            set "VER={{BatEsc(version)}}"
            set "RC=0"
            call :log "=== Mise a jour vers %VER% ==="

            rem --- 1) attendre la fermeture de l'application (45 s max, puis arret force)
            set /a N=0
            :wait
            tasklist /FI "PID eq %PID%" /FI "IMAGENAME eq %EXE%" /NH 2>nul | find /I "%EXE%" >nul
            if errorlevel 1 goto exited
            set /a N+=1
            if %N% GEQ 45 goto killit
            ping -n 2 127.0.0.1 >nul
            goto wait
            :killit
            call :log "Processus toujours actif apres 45 s : arret force"
            taskkill /F /PID %PID% >nul 2>&1
            ping -n 4 127.0.0.1 >nul
            :exited

            rem --- 2) sauvegarde de l'installation actuelle
            call :log "Sauvegarde de l'installation"
            if exist "%BACKUP%" rmdir /s /q "%BACKUP%" >nul 2>&1
            robocopy "%INSTALL%" "%BACKUP%" /E /R:2 /W:1 /NFL /NDL /NJH /NJS /NP >nul
            if errorlevel 8 goto nobackup

            rem --- 3) copie des nouveaux fichiers
            call :log "Copie des nouveaux fichiers"
            robocopy "%SRC%" "%INSTALL%" /E /IS /IT /R:5 /W:2 /NFL /NDL /NJH /NJS /NP >>"%LOG%" 2>&1
            set "RC=%ERRORLEVEL%"
            if %RC% GEQ 8 goto rollback
            if not exist "%INSTALL%\%EXE%" goto rollback
            set "OUTCOME=ok"
            goto finish

            :nobackup
            call :log "Sauvegarde impossible : installation laissee intacte"
            set "OUTCOME=failed:backup"
            goto finish

            :rollback
            call :log "ECHEC de la copie (code %RC%) : restauration de la sauvegarde"
            robocopy "%BACKUP%" "%INSTALL%" /E /IS /IT /R:5 /W:2 /NFL /NDL /NJH /NJS /NP >>"%LOG%" 2>&1
            set "OUTCOME=rollback:%RC%"

            :finish
            >"%RESULT%" echo %OUTCOME%
            call :log "Resultat : %OUTCOME%"
            if exist "%BACKUP%" rmdir /s /q "%BACKUP%" >nul 2>&1
            if exist "%STAGE%" rmdir /s /q "%STAGE%" >nul 2>&1
            if not "%RELAUNCH%"=="1" goto done
            call :log "Relance de l'application"
            if "%ELEVATED%"=="1" (
                start "" explorer.exe "%INSTALL%\%EXE%"
            ) else (
                start "" "%INSTALL%\%EXE%"
            )
            :done
            (goto) 2>nul & del "%~f0"

            :log
            >>"%LOG%" echo [%date% %time%] %~1
            exit /b 0
            """;
        return body.Replace("\r\n", "\n").Replace("\n", "\r\n") + "\r\n";
    }

    public static void Cleanup(string currentVersion)
    {
        try
        {
            if (!Directory.Exists(UpdateDir)) return;
            TryParseVersion(currentVersion, out var cur);
            foreach (var d in Directory.GetDirectories(UpdateDir))
            {
                string name = Path.GetFileName(d);
                if (name == "backup" || !TryParseVersion(name, out var v) || v <= cur) ForceDelete(d);
            }
            foreach (var f in Directory.GetFiles(UpdateDir, "apply_*.cmd"))
                if (DateTime.UtcNow - File.GetLastWriteTimeUtc(f) > TimeSpan.FromHours(6)) try { File.Delete(f); } catch { }
        }
        catch (Exception ex) { Log.Write("Nettoyage des mises à jour : " + ex.Message, 1); }
    }

    private static string? FindSourceDir(string root, string exe)
    {
        if (!Directory.Exists(root)) return null;
        if (File.Exists(Path.Combine(root, exe))) return root;
        foreach (var d in Directory.GetDirectories(root))
            if (File.Exists(Path.Combine(d, exe))) return d;
        return null;
    }

    private static string SafeName(string s) =>
        new string(s.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c).ToArray());

    internal static void ForceDelete(string dir)
    {
        try
        {
            if (!Directory.Exists(dir)) return;
            foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
                try { File.SetAttributes(f, FileAttributes.Normal); } catch { }
            Directory.Delete(dir, true);
        }
        catch { }
    }

    private static bool IsDirectoryWritable(string dir)
    {
        try
        {
            string probe = Path.Combine(dir, ".write_test_" + Guid.NewGuid().ToString("N")[..8]);
            File.WriteAllText(probe, "x");
            File.Delete(probe);
            return true;
        }
        catch { return false; }
    }

    private static void ValidateInstallDir(string dir, string exe)
    {
        if (!File.Exists(Path.Combine(dir, exe)))
            throw new InvalidOperationException($"{exe} introuvable dans « {dir} » (lancement en mode développement ?).");
        string? root = Path.GetPathRoot(dir);
        if (root != null && string.Equals(dir, Path.TrimEndingDirectorySeparator(root), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Le dossier d'installation ne peut pas être la racine d'un disque.");
        string work = Path.TrimEndingDirectorySeparator(UpdateDir);
        if (string.Equals(work, dir, StringComparison.OrdinalIgnoreCase) ||
            work.StartsWith(dir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Le dossier d'installation ne peut pas contenir le dossier de travail des mises à jour.");
        if (DirSize(dir, maxFiles: 20_000, maxBytes: 4L << 30) < 0)
            throw new InvalidOperationException("Le dossier d'installation est anormalement volumineux : l'application doit vivre dans son propre dossier.");
    }

    private static long DirSize(string dir, int maxFiles = int.MaxValue, long maxBytes = long.MaxValue)
    {
        long bytes = 0; int files = 0;
        var opt = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };
        foreach (var f in new DirectoryInfo(dir).EnumerateFiles("*", opt))
        {
            bytes += f.Length;
            if (++files > maxFiles || bytes > maxBytes) return -1;
        }
        return bytes;
    }

    private static void EnsureFreeSpace(string path, long needed)
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(path));
            if (string.IsNullOrEmpty(root)) return;
            var di = new DriveInfo(root);
            if (di.IsReady && di.AvailableFreeSpace < needed)
                throw new IOException($"Espace disque insuffisant sur {root} (il faut environ {needed >> 20} Mo libres).");
        }
        catch (IOException) { throw; }
        catch {  }
    }
}

public static class UpdateState
{
    private static string Dir => GitHubUpdater.UpdateDir;
    private static string PendingPath => Path.Combine(Dir, "pending.txt");
    private static string FailedPath => Path.Combine(Dir, "failed.txt");
    public static string ResultPath => Path.Combine(Dir, "result.txt");

    public static void MarkPending(string version)
    {
        Directory.CreateDirectory(Dir);
        try { File.Delete(ResultPath); } catch { }
        File.WriteAllText(PendingPath, version);
    }

    public static void ClearPending()
    {
        try { File.Delete(PendingPath); } catch { }
        try { File.Delete(ResultPath); } catch { }
    }

    public static bool IsBlocked(string version)
    {
        try { return File.Exists(FailedPath) && string.Equals(File.ReadAllText(FailedPath).Trim(), version, StringComparison.OrdinalIgnoreCase); }
        catch { return false; }
    }

    public static void Unblock() { try { File.Delete(FailedPath); } catch { } }

    public static UpdateOutcome? ConsumeOutcome(string currentVersion)
    {
        try
        {
            if (!File.Exists(PendingPath)) { try { File.Delete(ResultPath); } catch { } return null; }
            string target = File.ReadAllText(PendingPath).Trim();
            string result = File.Exists(ResultPath) ? File.ReadAllText(ResultPath).Trim() : "";
            try { File.Delete(PendingPath); } catch { }
            try { File.Delete(ResultPath); } catch { }

            if (GitHubUpdater.TryParseVersion(currentVersion, out var cur) && GitHubUpdater.TryParseVersion(target, out var tgt) && cur >= tgt)
            {
                Unblock();
                return new UpdateOutcome(true, target, $"Mise à jour vers la version {target} installée.");
            }

            Directory.CreateDirectory(Dir);
            File.WriteAllText(FailedPath, target);
            string why = result switch
            {
                var r when r.StartsWith("rollback", StringComparison.OrdinalIgnoreCase) => "la copie des fichiers a échoué, l'ancienne version a été restaurée",
                var r when r.StartsWith("failed:backup", StringComparison.OrdinalIgnoreCase) => "la sauvegarde de l'installation était impossible, rien n'a été modifié",
                "" => "l'application n'a pas été remplacée (mise à jour annulée ou script interrompu)",
                var r => "résultat inattendu : " + r,
            };
            return new UpdateOutcome(false, target, $"La mise à jour vers {target} a échoué : {why}. Détails : update.log.");
        }
        catch (Exception ex) { Log.Error("UpdateState", ex); return null; }
    }
}
