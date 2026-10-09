using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FrameCastStudio.Linux.Services;

namespace FrameCastStudio.Linux.ViewModels;

public partial class MainViewModel : ObservableObject
{
    // ---------------------------------------------------------------- listes de choix
    public int[] FpsChoices { get; } = { 15, 24, 30, 48, 60, 90, 120 };
    public int[] ScaleChoices { get; } = { 100, 90, 75, 66, 50, 33, 25 };
    public string[] Codecs { get; } = { "H264", "HEVC", "AV1" };
    public string[] EncoderModes { get; } = { "Auto", "GPU", "CPU" };
    public string[] RateModes { get; } = { "CBR", "VBR", "CQP" };
    public int[] GopChoices { get; } = { 1, 2, 3, 4, 5 };
    public string[] ColorSpaces { get; } = { "BT.709", "BT.601" };
    public string[] Containers { get; } = { "MP4", "MKV" };
    public string[] ScreenshotFormats { get; } = { "PNG", "JPEG", "BMP" };
    public int[] AudioBitrates { get; } = { 96, 128, 160, 192, 256, 320 };
    public int[] SampleRates { get; } = { 44100, 48000 };
    public int[] ReplayChoices { get; } = { 10, 15, 30, 60, 120, 300 };
    public string[] Backends { get; } = { "Auto", "X11", "Portal" };
    public string[] StreamServices { get; } = { "Twitch", "YouTube", "Kick", "Facebook", "Personnalisé" };

    private static readonly Dictionary<string, string> ServiceUrls = new()
    {
        ["Twitch"] = "rtmp://live.twitch.tv/app",
        ["YouTube"] = "rtmp://a.rtmp.youtube.com/live2",
        ["Kick"] = "rtmps://fa723fc1b171.global-contribute.live-video.net/app",
        ["Facebook"] = "rtmps://live-api-s.facebook.com:443/rtmp",
    };

    // ---------------------------------------------------------------- réglages persistés
    [ObservableProperty] [property: Persist] private int fps = 60;
    [ObservableProperty] [property: Persist] private int scalePercent = 100;
    [ObservableProperty] [property: Persist] private bool captureCursor = true;
    [ObservableProperty] [property: Persist] private bool cropEnabled;
    [ObservableProperty] [property: Persist] private decimal cropX;
    [ObservableProperty] [property: Persist] private decimal cropY;
    [ObservableProperty] [property: Persist] private decimal cropW = 1280;
    [ObservableProperty] [property: Persist] private decimal cropH = 720;
    [ObservableProperty] [property: Persist] private string captureBackend = "Auto";
    [ObservableProperty] [property: Persist] private string? portalRestoreToken;

    [ObservableProperty] [property: Persist] private string outputFolder = Paths.DefaultVideos;
    [ObservableProperty] [property: Persist] private string screenshotFolder = Paths.DefaultPictures;
    [ObservableProperty] [property: Persist] private bool copyScreenshotToClipboard = true;
    [ObservableProperty] [property: Persist] private string screenshotFormat = "PNG";
    [ObservableProperty] [property: Persist] private string outputFormat = "MP4";
    [ObservableProperty] [property: Persist] private bool useTimestampInFilename = true;
    [ObservableProperty] [property: Persist] private string filenamePrefixRec = "FrameCast";
    [ObservableProperty] [property: Persist] private bool openFolderAfterRec;
    [ObservableProperty] [property: Persist] private bool fragmentedMp4;
    [ObservableProperty] [property: Persist] private bool splitFileEnabled;
    [ObservableProperty] [property: Persist] private decimal splitFileSizeGb = 4;

    [ObservableProperty] [property: Persist] [NotifyPropertyChangedFor(nameof(ShowQp))] [NotifyPropertyChangedFor(nameof(ShowBitrate))]
    private string rateMode = "CBR";
    [ObservableProperty] [property: Persist] private string codec = "H264";
    [ObservableProperty] [property: Persist] private string encoderMode = "Auto";
    [ObservableProperty] [property: Persist] private decimal bitrateKbps = 6000;
    [ObservableProperty] [property: Persist] private double qp = 22;
    [ObservableProperty] [property: Persist] private int gopSeconds = 2;
    [ObservableProperty] [property: Persist] private bool bFramesEnabled;
    [ObservableProperty] [property: Persist] private bool lowLatency = true;
    [ObservableProperty] [property: Persist] private bool enableVbvBuffer;
    [ObservableProperty] [property: Persist] private string colorSpace = "BT.709";

    [ObservableProperty] [property: Persist] private bool audioLoopback = true;
    [ObservableProperty] [property: Persist] private bool audioMic = true;
    [ObservableProperty] [property: Persist] private string loopbackDeviceId = "";
    [ObservableProperty] [property: Persist] private string micDeviceId = "";
    [ObservableProperty] [property: Persist] private int audioBitrateKbps = 128;
    [ObservableProperty] [property: Persist] private int audioSampleRate = 48000;
    [ObservableProperty] [property: Persist] private double masterGainDb;
    [ObservableProperty] [property: Persist] private bool masterMute;
    [ObservableProperty] [property: Persist] private bool limiterOn = true;
    [ObservableProperty] [property: Persist] private double limiterCeilingDb = -1;
    [ObservableProperty] [property: Persist] private bool duckingOn;
    [ObservableProperty] [property: Persist] private double duckThresholdDb = -35;
    [ObservableProperty] [property: Persist] private double duckAmountDb = 12;
    [ObservableProperty] [property: Persist] private double duckAttackMs = 30;
    [ObservableProperty] [property: Persist] private double duckReleaseMs = 500;

    [ObservableProperty] [property: Persist] private string selectedService = "Twitch";
    [ObservableProperty] [property: Persist] private string ingestUrl = "rtmp://live.twitch.tv/app";
    [ObservableProperty] [property: Persist] private string streamKey = "";
    [ObservableProperty] [property: Persist] private bool recordWhileLive;

    [ObservableProperty] [property: Persist] private bool replayEnabled;
    [ObservableProperty] [property: Persist] private int replaySeconds = 30;
    [ObservableProperty] [property: Persist] private bool autoCheckUpdates = true;

    public bool ShowQp => RateMode == "CQP";
    public bool ShowBitrate => RateMode != "CQP";

    public StripVm Loop { get; } = new();
    public StripVm Mic { get; } = new();

    [Persist]
    public string LoopJson
    {
        get => JsonSerializer.Serialize(Loop.ToCfg());
        set { var c = Parse(value); if (c != null) Loop.Apply(c); }
    }

    [Persist]
    public string MicJson
    {
        get => JsonSerializer.Serialize(Mic.ToCfg());
        set { var c = Parse(value); if (c != null) Mic.Apply(c); }
    }

    private static StripCfg? Parse(string? json)
    {
        try { return string.IsNullOrWhiteSpace(json) ? null : JsonSerializer.Deserialize<StripCfg>(json); } catch { return null; }
    }

    // ---------------------------------------------------------------- état d'exécution
    [ObservableProperty] private string status = "Initialisation…";
    [ObservableProperty] private string errorDetail = "";
    [ObservableProperty] private string encoderInfo = "—";
    [ObservableProperty] private string environmentInfo = "";
    [ObservableProperty] private string statFps = "–";
    [ObservableProperty] private string statBitrate = "–";
    [ObservableProperty] private string statSize = "–";
    [ObservableProperty] private string statElapsed = "00:00:00";
    [ObservableProperty] private string statDropped = "0";
    [ObservableProperty] private string statSpeed = "";
    [ObservableProperty] private string logText = "";
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(CanStart))] private bool isBusy;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(CanStart))] [NotifyPropertyChangedFor(nameof(IsStopped))] private bool isRecording;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(CanStart))] [NotifyPropertyChangedFor(nameof(IsStopped))] private bool isLive;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(CanStart))] [NotifyPropertyChangedFor(nameof(IsStopped))] private bool isReplayOnly;
    [ObservableProperty] private bool ffmpegMissing;
    [ObservableProperty] private bool canSaveReplay;

    public bool IsStopped => !IsRecording && !IsLive && !IsReplayOnly;
    public bool CanStart => IsStopped && !IsBusy && !FfmpegMissing;
    public bool IsRunning => !IsStopped;

    public ObservableCollection<AudioDevice> LoopbackDevices { get; } = new();
    public ObservableCollection<AudioDevice> MicDevices { get; } = new();
    public ObservableCollection<SettingsProfile> Profiles { get; } = new();
    [ObservableProperty] private SettingsProfile? selectedProfile;
    [ObservableProperty] private string newProfileName = "";

    [ObservableProperty] private string currentVersion = Updater.CurrentVersionText;
    [ObservableProperty] private string updateStatus = "Non vérifié";
    [ObservableProperty] private bool updateAvailable;
    [ObservableProperty] private bool updateBusy;
    [ObservableProperty] private double updateProgress;
    [ObservableProperty] private string updateNotes = "";
    public string CliHelp { get; } =
        "Raccourcis globaux : sous Wayland, une application ne peut pas capter les touches du système. " +
        "Lie plutôt ces commandes à des raccourcis dans les réglages de ton bureau (GNOME, KDE, Hyprland, Sway…) :\n" +
        "  FrameCastStudio --toggle-record     démarre / arrête l'enregistrement\n" +
        "  FrameCastStudio --toggle-live       démarre / arrête le direct\n" +
        "  FrameCastStudio --save-replay       sauvegarde le buffer de replay\n" +
        "  FrameCastStudio --screenshot        capture d'écran\n" +
        "  FrameCastStudio --stop              arrête la session en cours\n" +
        "(avec l'AppImage : ./FrameCastStudio-….AppImage --toggle-record)";

    // ---------------------------------------------------------------- interne
    private readonly SettingsStore _settings;
    private string? _ffmpeg;
    private EncoderCaps? _caps;
    private CaptureSession? _session;
    private UpdateInfo? _update;
    private readonly List<SettingsProfile> _all = new();
    private bool _applyingProfile;

    /// <summary>Fournis par la fenêtre : ouvre le sélecteur de dossier système.</summary>
    public Func<Task<string?>>? PickFolderAsync { get; set; }
    public event Action? ShowRequested;

    public MainViewModel()
    {
        var names = typeof(MainViewModel).GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.GetCustomAttribute<PersistAttribute>() != null).Select(p => p.Name);
        _settings = new SettingsStore(this, names);
        _settings.Load();

        PropertyChanged += (_, e) =>
        {
            _settings.Touch(e.PropertyName);
            if (e.PropertyName == nameof(SelectedService) && !_applyingProfile
                && ServiceUrls.TryGetValue(SelectedService, out var url)) IngestUrl = url;
            if (e.PropertyName is nameof(Codec) or nameof(EncoderMode) && IsStopped && _caps != null) UpdateEncoderInfo();
            if (e.PropertyName is nameof(IsRecording) or nameof(IsLive) or nameof(IsReplayOnly)) OnPropertyChanged(nameof(IsRunning));
        };
        Loop.PropertyChanged += (_, _) => _settings.Touch(nameof(LoopJson));
        Mic.PropertyChanged += (_, _) => _settings.Touch(nameof(MicJson));

        Log.Line += l => Dispatcher.UIThread.Post(() =>
        {
            var lines = (LogText + l + "\n").Split('\n');
            LogText = lines.Length > 260 ? string.Join("\n", lines[^260..]) : string.Join("\n", lines);
        });

        ReloadProfiles();
    }

    public async Task InitializeAsync()
    {
        _ffmpeg = FfmpegLocator.Find();
        if (_ffmpeg == null)
        {
            FfmpegMissing = true;
            OnPropertyChanged(nameof(CanStart));
            Status = "ffmpeg introuvable";
            ErrorDetail = "ffmpeg n'est ni embarqué ni installé. Installe-le (sudo apt install ffmpeg / dnf install ffmpeg / pacman -S ffmpeg) " +
                          "ou définis FRAMECAST_FFMPEG=/chemin/vers/ffmpeg.";
            return;
        }

        Status = "Analyse du système…";
        var devTask = AudioDevices.EnumerateAsync();
        _caps = await EncoderProbe.ProbeAsync(_ffmpeg);
        await ApplyDevicesAsync(await devTask);

        string session = CaptureSession.IsWaylandSession ? "Wayland" : "X11";
        string capture = CaptureSession.UsePortal(CaptureBackend) ? "portail + PipeWire" : "x11grab";
        string hw = string.Join(", ", _caps.Working.Where(w => w.Contains('_')).OrderBy(w => w));
        EnvironmentInfo = $"Session {session} · capture {capture} · ffmpeg {_caps.FfmpegVersion}" +
                          (FfmpegLocator.Bundled ? " (embarqué)" : " (système)") +
                          (hw.Length > 0 ? $" · GPU : {hw}" : " · encodage logiciel uniquement");
        UpdateEncoderInfo();
        Status = "Prêt";
        OnPropertyChanged(nameof(CanStart));

        if (AutoCheckUpdates) _ = CheckUpdatesAsync(silent: true);
    }

    private void UpdateEncoderInfo()
    {
        var pick = _caps?.Pick(Codec, EncoderMode);
        EncoderInfo = pick?.Label ?? "aucun encodeur disponible";
    }

    private async Task ApplyDevicesAsync(AudioDeviceList list)
    {
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            LoopbackDevices.Clear(); foreach (var d in list.Loopbacks) LoopbackDevices.Add(d);
            MicDevices.Clear(); foreach (var d in list.Mics) MicDevices.Add(d);
            if (string.IsNullOrEmpty(LoopbackDeviceId) || LoopbackDevices.All(d => d.Id != LoopbackDeviceId)) LoopbackDeviceId = list.DefaultLoopbackId;
            if (string.IsNullOrEmpty(MicDeviceId) || MicDevices.All(d => d.Id != MicDeviceId)) MicDeviceId = list.DefaultMicId;
            if (list.PactlMissing) Log.Write("pactl introuvable : périphériques audio par défaut uniquement.");
        });
    }

    [RelayCommand]
    private async Task RefreshDevices() => await ApplyDevicesAsync(await AudioDevices.EnumerateAsync());

    // ---------------------------------------------------------------- démarrage / arrêt
    private SessionConfig BuildConfig(string mode) => new()
    {
        Mode = mode, Fps = Fps, ScalePercent = ScalePercent, CaptureCursor = CaptureCursor,
        CropEnabled = CropEnabled, CropX = (int)CropX, CropY = (int)CropY, CropW = (int)CropW, CropH = (int)CropH,
        Codec = Codec, EncoderMode = EncoderMode, RateMode = RateMode, BitrateKbps = (int)BitrateKbps, Qp = (int)Math.Round(Qp),
        GopSeconds = GopSeconds, BFrames = BFramesEnabled, LowLatency = LowLatency, Vbv = EnableVbvBuffer, ColorSpace = ColorSpace,
        Container = OutputFormat, Fragmented = FragmentedMp4, Split = SplitFileEnabled, SplitGb = (double)SplitFileSizeGb,
        OutputFolder = OutputFolder, Prefix = string.IsNullOrWhiteSpace(FilenamePrefixRec) ? "FrameCast" : FilenamePrefixRec,
        Timestamp = UseTimestampInFilename,
        AudioLoopback = AudioLoopback, AudioMic = AudioMic, LoopbackId = LoopbackDeviceId, MicId = MicDeviceId,
        AudioBitrateKbps = AudioBitrateKbps, AudioSampleRate = AudioSampleRate,
        Loop = Loop.ToCfg(), Mic = Mic.ToCfg(), MasterGainDb = MasterGainDb, MasterMute = MasterMute,
        Limiter = LimiterOn, LimiterCeilingDb = LimiterCeilingDb, Ducking = DuckingOn,
        DuckThresholdDb = DuckThresholdDb, DuckAmountDb = DuckAmountDb, DuckAttackMs = DuckAttackMs, DuckReleaseMs = DuckReleaseMs,
        ReplayEnabled = ReplayEnabled, ReplaySeconds = ReplaySeconds,
        IngestUrl = IngestUrl, StreamKey = StreamKey, RecordWhileLive = RecordWhileLive,
        PortalRestoreToken = PortalRestoreToken, CaptureBackend = CaptureBackend,
    };

    private async Task StartAsync(string mode)
    {
        if (IsBusy || _session != null || _ffmpeg == null || _caps == null) return;
        IsBusy = true; ErrorDetail = "";
        Status = mode == "live" ? "Connexion…" : "Démarrage…";
        try
        {
            if (mode == "live" && string.IsNullOrWhiteSpace(StreamKey)) throw new InvalidOperationException("Renseigne ta clé de stream (onglet Direct).");
            var cfg = BuildConfig(mode);
            var s = new CaptureSession(_ffmpeg, _caps);
            s.Stats += st => Dispatcher.UIThread.Post(() =>
            {
                StatFps = st.Fps; StatBitrate = st.Bitrate; StatSize = st.Size; StatElapsed = st.Elapsed;
                StatDropped = st.Dropped.ToString(); StatSpeed = st.Speed;
            });
            s.Ended += err => Dispatcher.UIThread.Post(() => OnSessionEnded(s, err));
            await s.StartAsync(cfg);
            _session = s;
            if (!string.IsNullOrEmpty(s.NewRestoreToken)) PortalRestoreToken = s.NewRestoreToken;

            IsRecording = mode == "rec"; IsLive = mode == "live"; IsReplayOnly = mode == "replay";
            CanSaveReplay = mode == "replay" || ReplayEnabled;
            Status = mode switch { "live" => "● EN DIRECT", "replay" => "Buffer de replay actif", _ => "● Enregistrement" };
            EncoderInfo = s.Summary;
        }
        catch (OperationCanceledException) { Status = "Annulé"; }
        catch (Exception ex)
        {
            Status = "Échec du démarrage";
            ErrorDetail = ex.Message;
            Log.Error("Démarrage", ex);
        }
        finally { IsBusy = false; }
    }

    private void OnSessionEnded(CaptureSession s, string err)
    {
        if (_session != s) return;
        string? file = s.RecordPath;
        _session = null;
        IsRecording = IsLive = IsReplayOnly = false;
        CanSaveReplay = false;
        UpdateEncoderInfo();
        if (!string.IsNullOrEmpty(err)) { Status = "Session interrompue"; ErrorDetail = err; }
        else
        {
            Status = file != null && File.Exists(file) ? "Terminé : " + Path.GetFileName(file) : "Arrêté";
            if (OpenFolderAfterRec && file != null) OpenPath(Path.GetDirectoryName(file)!);
        }
    }

    [RelayCommand] private Task StartRecord() => StartAsync("rec");
    [RelayCommand] private Task StartLive() => StartAsync("live");
    [RelayCommand] private Task StartReplay() => StartAsync("replay");

    [RelayCommand]
    private async Task Stop()
    {
        var s = _session;
        if (s == null || IsBusy) return;
        IsBusy = true; Status = "Arrêt en cours…";
        try { await s.StopAsync(); }
        catch (Exception ex) { Log.Error("Arrêt", ex); }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private async Task SaveReplay()
    {
        var s = _session;
        if (s == null) return;
        Status = "Sauvegarde du replay…";
        string? path = await s.SaveReplayAsync();
        Status = path != null ? "Replay enregistré : " + Path.GetFileName(path) : "Replay indisponible (attends quelques secondes)";
    }

    [RelayCommand]
    private async Task Screenshot()
    {
        if (_ffmpeg == null || IsBusy) return;
        try
        {
            Status = "Capture d'écran…";
            var r = await ScreenshotService.TakeAsync(_ffmpeg, ScreenshotFolder, ScreenshotFormat, CaptureBackend,
                CaptureCursor, PortalRestoreToken, CopyScreenshotToClipboard);
            if (r == null) { Status = "Capture échouée"; return; }
            if (!string.IsNullOrEmpty(r.Value.Token)) PortalRestoreToken = r.Value.Token;
            Status = "Capture : " + Path.GetFileName(r.Value.Path);
        }
        catch (Exception ex) { Status = "Capture échouée"; ErrorDetail = ex.Message; Log.Error("Capture d'écran", ex); }
    }

    /// <summary>Commandes reçues depuis la ligne de commande (raccourcis du bureau).</summary>
    public void HandleCommand(string cmd)
    {
        Dispatcher.UIThread.Post(async () =>
        {
            switch (cmd)
            {
                case "toggle-record": if (IsRecording) await Stop(); else if (CanStart) await StartRecord(); break;
                case "toggle-live": if (IsLive) await Stop(); else if (CanStart) await StartLive(); break;
                case "save-replay": if (CanSaveReplay) await SaveReplay(); break;
                case "screenshot": await Screenshot(); break;
                case "stop": if (IsRunning) await Stop(); break;
                case "show": ShowRequested?.Invoke(); break;
            }
        });
    }

    public async Task ShutdownAsync()
    {
        _settings.SaveNow();
        if (_session != null) { try { await _session.StopAsync(); } catch { } }
    }

    // ---------------------------------------------------------------- dossiers
    [RelayCommand]
    private async Task BrowseOutput()
    {
        var p = PickFolderAsync == null ? null : await PickFolderAsync();
        if (!string.IsNullOrEmpty(p)) OutputFolder = p;
    }

    [RelayCommand]
    private async Task BrowseScreenshots()
    {
        var p = PickFolderAsync == null ? null : await PickFolderAsync();
        if (!string.IsNullOrEmpty(p)) ScreenshotFolder = p;
    }

    [RelayCommand] private void OpenOutput() { Directory.CreateDirectory(OutputFolder); OpenPath(OutputFolder); }
    [RelayCommand] private void OpenLogs() => OpenPath(Paths.DataDir);

    private static void OpenPath(string path)
    {
        try
        {
            var psi = new ProcessStartInfo("xdg-open") { UseShellExecute = false, CreateNoWindow = true };
            psi.ArgumentList.Add(path);
            Process.Start(psi);
        }
        catch (Exception ex) { Log.Error("xdg-open", ex); }
    }

    // ---------------------------------------------------------------- profils
    private void ReloadProfiles()
    {
        _all.Clear();
        _all.AddRange(ProfileCatalog.BuiltIn());
        _all.AddRange(ProfileCatalog.LoadCustom());
        Profiles.Clear();
        foreach (var p in _all) Profiles.Add(p);
    }

    [RelayCommand]
    private void ApplyProfile()
    {
        if (SelectedProfile == null) return;
        _applyingProfile = true;
        try
        {
            var type = GetType();
            foreach (var (k, v) in SelectedProfile.Values)
            {
                var pi = type.GetProperty(k, BindingFlags.Public | BindingFlags.Instance);
                if (pi == null || !pi.CanWrite) continue;
                try { pi.SetValue(this, v.Deserialize(pi.PropertyType)); } catch { }
            }
        }
        finally { _applyingProfile = false; }
        UpdateEncoderInfo();
        Status = "Profil appliqué : " + SelectedProfile.Name;
    }

    [RelayCommand]
    private void SaveProfile()
    {
        string name = NewProfileName.Trim();
        if (name.Length == 0) { Status = "Donne un nom au profil."; return; }
        var p = new SettingsProfile { Name = name, Description = "Profil personnalisé" };
        var type = GetType();
        foreach (var k in ProfileCatalog.Keys)
        {
            var pi = type.GetProperty(k, BindingFlags.Public | BindingFlags.Instance);
            if (pi != null) p.Values[k] = JsonSerializer.SerializeToElement(pi.GetValue(this), pi.PropertyType);
        }
        _all.RemoveAll(x => !x.BuiltIn && x.Name == name);
        _all.Add(p);
        ProfileCatalog.SaveCustom(_all);
        ReloadProfiles();
        SelectedProfile = Profiles.FirstOrDefault(x => x.Name == name && !x.BuiltIn);
        NewProfileName = "";
        Status = "Profil enregistré : " + name;
    }

    [RelayCommand]
    private void DeleteProfile()
    {
        if (SelectedProfile is not { BuiltIn: false } p) return;
        _all.Remove(p);
        ProfileCatalog.SaveCustom(_all);
        ReloadProfiles();
    }

    // ---------------------------------------------------------------- mises à jour
    [RelayCommand]
    private Task CheckUpdates() => CheckUpdatesAsync(silent: false);

    private async Task CheckUpdatesAsync(bool silent)
    {
        if (UpdateBusy) return;
        UpdateBusy = true;
        if (!silent) UpdateStatus = "Vérification…";
        try
        {
            _update = await Updater.CheckAsync();
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                UpdateAvailable = _update != null;
                UpdateNotes = _update?.Notes ?? "";
                UpdateStatus = _update != null ? $"Version {_update.Tag} disponible (actuelle : {CurrentVersion})" : "À jour";
            });
        }
        catch (Exception ex)
        {
            if (!silent) UpdateStatus = "Vérification impossible : " + ex.Message;
            Log.Error("Mises à jour", ex);
        }
        finally { UpdateBusy = false; }
    }

    [RelayCommand]
    private async Task InstallUpdate()
    {
        if (_update == null || UpdateBusy) return;
        if (!Updater.CanSelfInstall(_update)) { OpenPath(_update.PageUrl); return; }
        UpdateBusy = true;
        try
        {
            UpdateStatus = "Téléchargement…";
            await Updater.InstallAppImageAsync(_update, new Progress<double>(p => UpdateProgress = p));
            UpdateStatus = "Mise à jour installée : relance FrameCast Studio pour l'utiliser.";
            UpdateAvailable = false;
        }
        catch (Exception ex) { UpdateStatus = "Échec : " + ex.Message; Log.Error("Installation de la mise à jour", ex); }
        finally { UpdateBusy = false; }
    }
}
