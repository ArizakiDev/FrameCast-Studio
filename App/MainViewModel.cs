using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Xaml.Controls;
using FrameCastStudio.Core.Audio;
using FrameCastStudio.Core.Capture;
using FrameCastStudio.Core.Discord;
using FrameCastStudio.Core.Encoding;
using FrameCastStudio.Core.Pipeline;
using FrameCastStudio.Core.Streaming;
using FrameCastStudio.Core.Update;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using WinRT;

namespace FrameCastStudio.App;

public sealed partial class MainViewModel : ObservableObject
{

    public string[] Services { get; } = { "Twitch", "YouTube", "Kick", "Personnalisé" };
    public string[] Codecs { get; } = { "H264", "HEVC", "AV1" };
    public string[] RateModes { get; } = { "CBR", "VBR", "CQP" };
    public int[] FpsOptions { get; } = { 24, 30, 60, 120 };
    public int[] ScaleOptions { get; } = { 100, 75, 50, 25 };
    public string[] PriorityOptions { get; } = { "Normale", "Au-dessus de la normale", "Haute" };
    public string[] OutputFormats { get; } = { "MP4" };
    public string[] AudioSampleRates { get; } = { "44100", "48000" };
    public string[] ColorSpaces { get; } = { "BT.709", "BT.2020" };
    public string[] LogLevels { get; } = { "Normal", "Verbose", "Débogage" };
    public string[] ScreenshotFormats { get; } = { "PNG", "JPEG", "BMP" };

    public ObservableCollection<CaptureSource> Sources { get; } = new();

    public ObservableCollection<AudioDeviceInfo> RenderDevices { get; } = new();
    public ObservableCollection<AudioDeviceInfo> CaptureDevices { get; } = new();
    [ObservableProperty] private AudioDeviceInfo? selectedRenderDevice;
    [ObservableProperty] private AudioDeviceInfo? selectedCaptureDevice;

    [ObservableProperty] private CaptureSource? selectedSource;
    [ObservableProperty] private int fps = 60;
    [ObservableProperty] private int scalePercent = 100;
    [ObservableProperty] private bool captureCursor = true;
    [ObservableProperty] private bool autoStartPreview = true;
    [ObservableProperty] private string outputFolder =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "FrameCast Studio");
    [ObservableProperty] private string screenshotFolder =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "FrameCast Studio");
    [ObservableProperty] private bool copyScreenshotToClipboard = true;

    [ObservableProperty] private string outputFormat = "MP4";
    [ObservableProperty] private bool useTimestampInFilename = true;
    [ObservableProperty] private string filenamePrefixRec = "FrameCast";
    [ObservableProperty] private bool openFolderAfterRec = false;
    [ObservableProperty] private bool splitFileEnabled = false;
    [ObservableProperty] private double splitFileSizeGb = 4.0;

    [ObservableProperty] private bool disableWgcBorder = true;
    [ObservableProperty] private double captureOffsetMs = 0;
    [ObservableProperty] private string screenshotFormat = "PNG";

    [ObservableProperty] private string codec = "H264";
    [ObservableProperty] private string rateMode = "CBR";
    [ObservableProperty] private double bitrateKbps = 6000;
    [ObservableProperty] private double qp = 22;
    [ObservableProperty] private double gopSeconds = 2;
    [ObservableProperty] private bool bFramesEnabled = false;
    [ObservableProperty] private bool lowLatency = true;
    [ObservableProperty] private bool enableVbvBuffer = false;
    [ObservableProperty] private double vbvBufferSizeMs = 2000;
    [ObservableProperty] private string colorSpace = "BT.709";
    [ObservableProperty] private bool adaptiveBitrate = false;

    [ObservableProperty] private bool audioLoopback = true;
    [ObservableProperty] private bool audioMic = true;
    [ObservableProperty] private double audioBufferMs = 20;
    [ObservableProperty] private bool monitorAudio = false;
    [ObservableProperty] private int audioBitrateKbps = 128;
    [ObservableProperty] private string audioSampleRate = "48000";

    [ObservableProperty] private double loopGainDb = 0;
    [ObservableProperty] private bool loopMute = false;
    [ObservableProperty] private double loopPan = 0;
    [ObservableProperty] private double loopDelayMs = 0;
    [ObservableProperty] private bool loopHighPass = false;
    [ObservableProperty] private double loopHighPassHz = 80;
    [ObservableProperty] private bool loopGate = false;
    [ObservableProperty] private double loopGateThresholdDb = -45;
    [ObservableProperty] private double loopGateAttackMs = 5;
    [ObservableProperty] private double loopGateReleaseMs = 150;
    [ObservableProperty] private bool loopComp = false;
    [ObservableProperty] private double loopCompThresholdDb = -18;
    [ObservableProperty] private double loopCompRatio = 3;
    [ObservableProperty] private double loopCompAttackMs = 10;
    [ObservableProperty] private double loopCompReleaseMs = 120;
    [ObservableProperty] private double loopCompMakeupDb = 0;
    [ObservableProperty] private double micGainDb = 0;
    [ObservableProperty] private bool micMute = false;
    [ObservableProperty] private double micPan = 0;
    [ObservableProperty] private double micDelayMs = 0;
    [ObservableProperty] private bool micHighPass = false;
    [ObservableProperty] private double micHighPassHz = 80;
    [ObservableProperty] private bool micGate = false;
    [ObservableProperty] private double micGateThresholdDb = -45;
    [ObservableProperty] private double micGateAttackMs = 5;
    [ObservableProperty] private double micGateReleaseMs = 150;
    [ObservableProperty] private bool micComp = false;
    [ObservableProperty] private double micCompThresholdDb = -18;
    [ObservableProperty] private double micCompRatio = 3;
    [ObservableProperty] private double micCompAttackMs = 10;
    [ObservableProperty] private double micCompReleaseMs = 120;
    [ObservableProperty] private double micCompMakeupDb = 0;
    [ObservableProperty] private double masterGainDb = 0;
    [ObservableProperty] private bool masterMute = false;
    [ObservableProperty] private bool limiterOn = true;
    [ObservableProperty] private double limiterCeilingDb = -1;
    [ObservableProperty] private bool duckingOn = false;
    [ObservableProperty] private double duckThresholdDb = -35;
    [ObservableProperty] private double duckAmountDb = 12;
    [ObservableProperty] private double duckAttackMs = 30;
    [ObservableProperty] private double duckReleaseMs = 500;
    [ObservableProperty] private bool meterWhenIdle = false;
    [ObservableProperty] private AudioDeviceInfo? selectedMonitorDevice;
    [ObservableProperty] private string renderDeviceId = "";
    [ObservableProperty] private string captureDeviceId = "";
    [ObservableProperty] private string monitorDeviceId = "";

    [ObservableProperty] private double loopMeter;
    [ObservableProperty] private double micMeter;
    [ObservableProperty] private double masterMeterL;
    [ObservableProperty] private double masterMeterR;
    [ObservableProperty] private string mixerInfo = "Mélangeur inactif (démarre avec un enregistrement, un direct, ou « Vumètres hors enregistrement »)";
    [ObservableProperty] private bool fragmentedMp4 = false;

    [ObservableProperty] private string selectedService = "Twitch";
    [ObservableProperty] private string ingestUrl = "rtmp://live.twitch.tv/app";
    [ObservableProperty] private string streamKey = "";
    [ObservableProperty] private double rtmpQueueCapacity = 240;
    [ObservableProperty] private double rtmpChunkSize = 4096;

    [ObservableProperty] private string processPriority = "Haute";
    [ObservableProperty] private bool minimizeToTray = false;
    [ObservableProperty] private bool startMinimized = false;
    [ObservableProperty] private bool autoStopAfterInactivity = false;
    [ObservableProperty] private double autoStopMinutes = 60;
    [ObservableProperty] private string logLevel = "Normal";
    [ObservableProperty] private bool discordRpcEnabled = false;
    [ObservableProperty] private string appLanguage = "Français";
    public string[] LanguageOptions { get; } = { "Français", "English" };

    private static readonly GitHubUpdater Updater = new(AppInfo.GitHubOwner, AppInfo.GitHubRepo, ResolveExeName());
    [ObservableProperty] private string currentVersion = AppInfo.CurrentVersion;
    [ObservableProperty] private string updateStatus = "Non vérifié";
    [ObservableProperty] private bool updateAvailable;
    [ObservableProperty] private bool updateReady;
    [ObservableProperty] private bool showDownloadButton;
    [ObservableProperty] private bool updateBusy;
    [ObservableProperty] private bool autoDownloadUpdates = true;
    [ObservableProperty] private bool installUpdateOnExit = true;
    [ObservableProperty] private double updateProgress;
    [ObservableProperty] private string updateNotes = "";
    public string VersionLabel => $"{AppInfo.Name} · version {AppInfo.CurrentVersion}";
    private ReleaseInfo? _pendingUpdate;
    private StagedUpdate? _staged;
    private readonly CancellationTokenSource _updateCts = new();
    private bool _applyStarted, _checking;
    private string? _stageFailedVersion;
    internal readonly DiscordRpcService Discord = new();

    public event Action? CloseRequested;

    private static string ResolveExeName()
    {
        string n = Path.GetFileName(Environment.ProcessPath ?? "");
        return n.Length > 0 && !n.Equals("dotnet.exe", StringComparison.OrdinalIgnoreCase) ? n : AppInfo.ExeName;
    }

    [ObservableProperty] private string encoderName = "—";
    [ObservableProperty] private string cpu = "0 %";
    [ObservableProperty] private string gpu = "0 %";
    [ObservableProperty] private string frameTime = "0 ms";
    [ObservableProperty] private string fpsInfo = "";
    [ObservableProperty] private string ram = "0 MB";
    [ObservableProperty] private string bitrate = "0 kbps";
    [ObservableProperty] private string status = "Inactif";
    [ObservableProperty] private double cpuValue;
    [ObservableProperty] private double gpuValue;
    [ObservableProperty] private bool isPreviewVisible = true;
    [ObservableProperty] private double previewWidth = 1280;
    [ObservableProperty] private double previewHeight = 720;
    [ObservableProperty] private Microsoft.UI.Xaml.Visibility previewShownVis = Microsoft.UI.Xaml.Visibility.Visible;
    [ObservableProperty] private Microsoft.UI.Xaml.Visibility previewHiddenVis = Microsoft.UI.Xaml.Visibility.Collapsed;
    [ObservableProperty] private Microsoft.UI.Xaml.Visibility qpVisible = Microsoft.UI.Xaml.Visibility.Collapsed;
    [ObservableProperty] private Microsoft.UI.Xaml.Visibility bitrateVisible = Microsoft.UI.Xaml.Visibility.Visible;

    [ObservableProperty] private bool isRecording;
    [ObservableProperty] private bool isLive;
    [ObservableProperty] private bool isBusy;
    [ObservableProperty] private bool isIdle = true;
    [ObservableProperty] private bool isPaused;
    [ObservableProperty] private string pauseLabel = "⏸ Pause";
    [ObservableProperty] private string pauseGlyph = "\uE769"; // ⏸ Pause glyph

    partial void OnIsPausedChanged(bool value) => PauseGlyph = value ? "\uE768" : "\uE769"; // Play / Pause

    partial void OnIsBusyChanged(bool value) => IsIdle = !value;
    partial void OnScreenshotBusyChanged(bool value) => NotScreenshotBusy = !value;
    partial void OnUpdateBusyChanged(bool value) => NotUpdateBusy = !value;
    partial void OnUpdateAvailableChanged(bool value) => RecalcUpdateButtons();
    partial void OnUpdateReadyChanged(bool value) => RecalcUpdateButtons();
    private void RecalcUpdateButtons() => ShowDownloadButton = UpdateAvailable && !UpdateReady && _pendingUpdate?.AssetUrl != null;
    [ObservableProperty] private bool notScreenshotBusy = true;
    [ObservableProperty] private bool notUpdateBusy = true;

    // App audio mixer panel
    partial void OnAppMixerExpandedChanged(bool v)
    {
        AppMixerCollapsed = !v ? Microsoft.UI.Xaml.Visibility.Visible : Microsoft.UI.Xaml.Visibility.Collapsed;
        AppMixerExpanded2 = v ? Microsoft.UI.Xaml.Visibility.Visible : Microsoft.UI.Xaml.Visibility.Collapsed;
    }
    [ObservableProperty] private Microsoft.UI.Xaml.Visibility appMixerCollapsed = Microsoft.UI.Xaml.Visibility.Collapsed;
    [ObservableProperty] private Microsoft.UI.Xaml.Visibility appMixerExpanded2 = Microsoft.UI.Xaml.Visibility.Visible;

    [RelayCommand]
    private void ToggleAppMixer()
    {
        AppMixerExpanded = !AppMixerExpanded;
        if (AppMixerExpanded) RefreshAppAudioSessions();
    }

    [RelayCommand]
    private void RefreshAppAudioSessions2() => RefreshAppAudioSessions();

    private static readonly string[] PersistedNames =
    {
        "Fps", "ScalePercent", "CaptureCursor", "AutoStartPreview", "OutputFolder", "ScreenshotFolder",
        "CopyScreenshotToClipboard", "OutputFormat", "UseTimestampInFilename", "FilenamePrefixRec", "OpenFolderAfterRec", "SplitFileEnabled",
        "SplitFileSizeGb", "FragmentedMp4", "DisableWgcBorder", "CaptureOffsetMs", "ScreenshotFormat", "Codec",
        "RateMode", "BitrateKbps", "Qp", "GopSeconds", "BFramesEnabled", "LowLatency",
        "ColorSpace", "AdaptiveBitrate", "EnableVbvBuffer", "VbvBufferSizeMs", "AudioLoopback", "AudioMic", "AudioBufferMs", "AudioBitrateKbps",
        "AudioSampleRate", "MeterWhenIdle", "SelectedService", "IngestUrl", "RtmpQueueCapacity", "RtmpChunkSize",
        "ProcessPriority", "MinimizeToTray", "StartMinimized", "AutoStopAfterInactivity", "AutoStopMinutes", "LogLevel",
        "RenderDeviceId", "CaptureDeviceId", "MonitorDeviceId", "MonitorAudio", "MasterGainDb", "MasterMute",
        "LimiterOn", "LimiterCeilingDb", "DuckingOn", "DuckThresholdDb", "DuckAmountDb", "DuckAttackMs",
        "DuckReleaseMs", "LoopGainDb", "LoopMute", "LoopPan", "LoopDelayMs", "LoopHighPass",
        "LoopHighPassHz", "LoopGate", "LoopGateThresholdDb", "LoopGateAttackMs", "LoopGateReleaseMs", "LoopComp",
        "LoopCompThresholdDb", "LoopCompRatio", "LoopCompAttackMs", "LoopCompReleaseMs", "LoopCompMakeupDb", "MicGainDb",
        "MicMute", "MicPan", "MicDelayMs", "MicHighPass", "MicHighPassHz", "MicGate",
        "MicGateThresholdDb", "MicGateAttackMs", "MicGateReleaseMs", "MicComp", "MicCompThresholdDb", "MicCompRatio",
        "MicCompAttackMs", "MicCompReleaseMs", "MicCompMakeupDb",
        "AutoDownloadUpdates", "InstallUpdateOnExit",
        "DiscordRpcEnabled", "AppLanguage"
    };
    private static readonly HashSet<string> MixerLiveNames = new()
    {
        "LoopGainDb", "LoopMute", "LoopPan", "LoopDelayMs", "LoopHighPass", "LoopHighPassHz",
        "LoopGate", "LoopGateThresholdDb", "LoopGateAttackMs", "LoopGateReleaseMs", "LoopComp", "LoopCompThresholdDb",
        "LoopCompRatio", "LoopCompAttackMs", "LoopCompReleaseMs", "LoopCompMakeupDb", "MicGainDb", "MicMute",
        "MicPan", "MicDelayMs", "MicHighPass", "MicHighPassHz", "MicGate", "MicGateThresholdDb",
        "MicGateAttackMs", "MicGateReleaseMs", "MicComp", "MicCompThresholdDb", "MicCompRatio", "MicCompAttackMs",
        "MicCompReleaseMs", "MicCompMakeupDb", "MasterGainDb", "MasterMute", "LimiterOn", "LimiterCeilingDb",
        "DuckingOn", "DuckThresholdDb", "DuckAmountDb", "DuckAttackMs", "DuckReleaseMs", "MonitorAudio",
        "MonitorDeviceId"
    };
    private readonly SettingsStore _store;
    private bool _autoStopping;

    public MainViewModel()
    {
        _store = new SettingsStore(this, PersistedNames);
        _store.Load();
        if (FilenamePrefixRec == "ScreenForge") FilenamePrefixRec = "FrameCast";
        FrameCastStudio.Core.Log.Level = Math.Max(0, Array.IndexOf(LogLevels, LogLevel));
        FrameCastStudio.Core.Log.Write("FrameCast Studio " + AppInfo.CurrentVersion + " démarré");
    }

    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        var n = e.PropertyName;
        if (n is null) return;
        _store.Touch(n);
        if (MixerLiveNames.Contains(n)) _session?.Mixer?.Update(BuildMixerSettings());
    }

    partial void OnLogLevelChanged(string value) => FrameCastStudio.Core.Log.Level = Math.Max(0, Array.IndexOf(LogLevels, value));
    partial void OnDiscordRpcEnabledChanged(bool value) => Discord.SetEnabled(value);
    partial void OnAppLanguageChanged(string value)
    {
        // Language change requires restart to take full effect.
        // We store the preference and apply strings at next launch.
    }
    partial void OnSelectedRenderDeviceChanged(AudioDeviceInfo? value) { if (value != null) RenderDeviceId = value.Id; }
    partial void OnSelectedCaptureDeviceChanged(AudioDeviceInfo? value) { if (value != null) CaptureDeviceId = value.Id; }
    partial void OnSelectedMonitorDeviceChanged(AudioDeviceInfo? value) { if (value != null) MonitorDeviceId = value.Id; }

    partial void OnMeterWhenIdleChanged(bool value) { if (_session != null && _mode == "preview") _ = SwitchSourceAsync(); }

    private MixerSettings BuildMixerSettings() => new()
    {
        Loopback = MakeStrip(LoopGainDb, LoopMute, LoopPan, LoopDelayMs, LoopHighPass, LoopHighPassHz, LoopGate, LoopGateThresholdDb, LoopGateAttackMs, LoopGateReleaseMs,
                             LoopComp, LoopCompThresholdDb, LoopCompRatio, LoopCompAttackMs, LoopCompReleaseMs, LoopCompMakeupDb),
        Mic = MakeStrip(MicGainDb, MicMute, MicPan, MicDelayMs, MicHighPass, MicHighPassHz, MicGate, MicGateThresholdDb, MicGateAttackMs, MicGateReleaseMs,
                        MicComp, MicCompThresholdDb, MicCompRatio, MicCompAttackMs, MicCompReleaseMs, MicCompMakeupDb),
        MasterGainDb = (float)MasterGainDb, MasterMute = MasterMute, Limiter = LimiterOn, LimiterCeilingDb = (float)LimiterCeilingDb,
        Ducking = DuckingOn, DuckThresholdDb = (float)DuckThresholdDb, DuckAmountDb = (float)DuckAmountDb,
        DuckAttackMs = (float)DuckAttackMs, DuckReleaseMs = (float)DuckReleaseMs,
        Monitor = MonitorAudio, MonitorDeviceId = string.IsNullOrEmpty(MonitorDeviceId) ? null : MonitorDeviceId,
    };

    private static StripSettings MakeStrip(double gain, bool mute, double pan, double delay, bool hp, double hpHz, bool gate, double gThr, double gAtt, double gRel,
                                           bool comp, double cThr, double cRatio, double cAtt, double cRel, double cMk) => new()
    {
        GainDb = (float)gain, Mute = mute, Pan = (float)pan, DelayMs = (int)delay,
        HighPass = hp, HighPassHz = (float)hpHz,
        Gate = gate, GateThresholdDb = (float)gThr, GateAttackMs = (float)gAtt, GateReleaseMs = (float)gRel,
        Comp = comp, CompThresholdDb = (float)cThr, CompRatio = (float)cRatio, CompAttackMs = (float)cAtt, CompReleaseMs = (float)cRel, CompMakeupDb = (float)cMk,
    };

    private static double Norm(float db) => Math.Clamp((db + 60.0) / 60.0, 0.0, 1.0);

    public void RefreshMeters()
    {
        var lv = _session?.Mixer?.Levels;
        if (lv is null)
        {
            LoopMeter = 0; MicMeter = 0; MasterMeterL = 0; MasterMeterR = 0;
            MixerInfo = "Mélangeur inactif : il démarre avec un enregistrement, un direct, ou « Vumètres actifs hors enregistrement » (onglet Audio).";
            return;
        }
        LoopMeter = Norm(lv.LoopDb); MicMeter = Norm(lv.MicDb);
        MasterMeterL = Norm(lv.MasterLDb); MasterMeterR = Norm(lv.MasterRDb);
        MixerInfo = $"Gate micro : {(lv.MicGateOpen ? "ouvert" : "fermé")} · Gate système : {(lv.LoopGateOpen ? "ouvert" : "fermé")}  |  "
                  + $"Compresseur : micro −{lv.MicGrDb:F1} dB, système −{lv.LoopGrDb:F1} dB  |  Ducking −{lv.DuckGrDb:F1} dB  |  Limiteur −{lv.LimGrDb:F1} dB"
                  + (_session?.Mixer?.MonitorBlocked == true ? "  |  ⚠ écoute bloquée : choisis un autre périphérique que le loopback (larsen)" : "");
    }

    private CaptureSession? _session;
    private string _mode = "";
    private IntPtr _panelNative;
    private bool _switchingSource;
    private string? _recPath;

    partial void OnSelectedServiceChanged(string value) => IngestUrl = value switch
    {
        "Twitch" => "rtmp://live.twitch.tv/app",
        "YouTube" => "rtmp://a.rtmp.youtube.com/live2",
        "Kick" => "rtmps://fa723fc1b171.global-contribute.live-video.net/app",
        _ => IngestUrl
    };

    partial void OnRateModeChanged(string value)
    {
        QpVisible = value == "CQP" ? Microsoft.UI.Xaml.Visibility.Visible : Microsoft.UI.Xaml.Visibility.Collapsed;
        BitrateVisible = value == "CQP" ? Microsoft.UI.Xaml.Visibility.Collapsed : Microsoft.UI.Xaml.Visibility.Visible;
    }

    partial void OnIsPreviewVisibleChanged(bool value)
    {
        if (_session != null) _session.PreviewEnabled = value;
        PreviewShownVis = value ? Microsoft.UI.Xaml.Visibility.Visible : Microsoft.UI.Xaml.Visibility.Collapsed;
        PreviewHiddenVis = value ? Microsoft.UI.Xaml.Visibility.Collapsed : Microsoft.UI.Xaml.Visibility.Visible;
    }

    partial void OnSelectedSourceChanged(CaptureSource? value)
    {
        if (_switchingSource || value is null || _session is null) return;
        _ = SwitchSourceAsync();
    }

    private async Task SwitchSourceAsync()
    {
        _switchingSource = true;
        try
        {
            var mode = _mode;
            if (_session != null) { var s = _session; _session = null; try { await s.DisposeAsync(); } catch { } }
            if (!string.IsNullOrEmpty(mode)) await StartAsync(mode);
        }
        finally { _switchingSource = false; }
    }

    public void AttachPreview(SwapChainPanel panel)
    {
        IntPtr unk = MarshalInspectable<object>.FromManaged(panel);
        try
        {
            var iid = PreviewRenderer.PanelNativeIid;
            Marshal.ThrowExceptionForHR(Marshal.QueryInterface(unk, in iid, out _panelNative));
        }
        finally { Marshal.Release(unk); }
    }

    public async Task InitializeAsync()
    {
        RefreshSources();
        RefreshAudioDevices();
        if (AutoStartPreview && SelectedSource != null)
            await StartAsync("preview");
        StartUpdateLoop();
    }

    [RelayCommand]
    private void RefreshSources()
    {
        var prevHandle = SelectedSource?.Handle;
        Sources.Clear();
        foreach (var m in CaptureSources.GetMonitors()) Sources.Add(m);
        foreach (var w in CaptureSources.GetWindows()) Sources.Add(w);
        var keep = Sources.FirstOrDefault(s => s.Handle == prevHandle) ?? Sources.FirstOrDefault();
        _switchingSource = true;
        SelectedSource = keep;
        _switchingSource = false;
    }

    [RelayCommand]
    private void RefreshAudioDevices()
    {
        var prevRender  = SelectedRenderDevice?.Id ?? RenderDeviceId;
        var prevCapture = SelectedCaptureDevice?.Id ?? CaptureDeviceId;
        var prevMonitor = SelectedMonitorDevice?.Id ?? MonitorDeviceId;

        RenderDevices.Clear();
        foreach (var d in AudioDeviceService.GetRenderDevices()) RenderDevices.Add(d);
        SelectedRenderDevice = RenderDevices.FirstOrDefault(d => d.Id == prevRender) ?? RenderDevices.FirstOrDefault();
        SelectedMonitorDevice = RenderDevices.FirstOrDefault(d => d.Id == prevMonitor) ?? RenderDevices.FirstOrDefault();

        CaptureDevices.Clear();
        foreach (var d in AudioDeviceService.GetCaptureDevices()) CaptureDevices.Add(d);
        SelectedCaptureDevice = CaptureDevices.FirstOrDefault(d => d.Id == prevCapture) ?? CaptureDevices.FirstOrDefault();
    }

    [RelayCommand] private Task StartRecord() => IsBusy ? Task.CompletedTask : StartAsync("rec");
    [RelayCommand] private Task StartLive() => IsBusy ? Task.CompletedTask : StartAsync("live");

    [RelayCommand]
    private void PauseResume()
    {
        if (_session is null) return;
        IsPaused = !IsPaused;
        _session.Paused = IsPaused;
        PauseLabel = IsPaused ? "▶ Reprendre" : "⏸ Pause";
        Status = IsPaused ? "En pause" : (_mode == "live" ? "En direct" : $"Enregistrement → {OutputFolder}");
    }

    [RelayCommand] private async Task Stop() => await StopAsync(keepPreview: true);

    [RelayCommand] private void ClosePreview() => IsPreviewVisible = false;
    [RelayCommand] private void ShowPreview() => IsPreviewVisible = true;

    private SessionSettings BuildSettings() => new()
    {
        Fps = Fps, ScalePercent = ScalePercent, CaptureCursor = CaptureCursor,
        Codec = Enum.Parse<Codec>(Codec), Rate = Enum.Parse<RateControl>(RateMode),
        BitrateKbps = (int)BitrateKbps, Qp = (int)Qp, GopSeconds = (int)GopSeconds,
        BFrames = BFramesEnabled ? 2 : 0, LowLatency = LowLatency,
        AudioLoopback = AudioLoopback, AudioMic = AudioMic, AudioBufferMs = (int)AudioBufferMs,
        AudioBitrateKbps = AudioBitrateKbps,
        AudioSampleRate = int.TryParse(AudioSampleRate, out var sr) ? sr : 48000,
        MeterWhenIdle = MeterWhenIdle, Mixer = BuildMixerSettings(),
        DisableWgcBorder = DisableWgcBorder, CaptureOffsetMs = CaptureOffsetMs,
        Bt2020 = ColorSpace == "BT.2020", AdaptiveBitrate = AdaptiveBitrate,
        EnableVbv = EnableVbvBuffer, VbvBufferMs = (int)VbvBufferSizeMs,
        FragmentedMp4 = FragmentedMp4, SplitEnabled = SplitFileEnabled, SplitSizeGb = SplitFileSizeGb,
        LoopbackDeviceId = SelectedRenderDevice?.Id == AudioDeviceInfo.Default.Id ? null : SelectedRenderDevice?.Id,
        MicDeviceId      = SelectedCaptureDevice?.Id == AudioDeviceInfo.Default.Id ? null : SelectedCaptureDevice?.Id,
        RtmpQueueCapacity = (int)RtmpQueueCapacity, RtmpChunkSize = (int)RtmpChunkSize,
        ProcessPriority = ProcessPriority switch
        {
            "Temps réel" => ProcessPriorityClass.RealTime,
            "Haute" => ProcessPriorityClass.High,
            "Au-dessus de la normale" => ProcessPriorityClass.AboveNormal,
            _ => ProcessPriorityClass.Normal,
        },
    };

    private async Task StartAsync(string mode)
    {
        if (SelectedSource is null) { Status = "Aucune source sélectionnée"; return; }
        try
        {

            if (_session != null) { var old = _session; _session = null; try { await old.DisposeAsync(); } catch { } }

            var item = D3DCaptureService.CreateItem(SelectedSource);
            var cfg = BuildSettings();

            try { Process.GetCurrentProcess().PriorityClass = cfg.ProcessPriority; } catch {  }

            string? path = null;
            if (mode == "rec")
            {
                Directory.CreateDirectory(OutputFolder);
                path = BuildRecordPath();
                _recPath = path;
            }
            bool live = mode == "live";

            _session = new CaptureSession();
            _mode = mode;
            IsPaused = false; PauseLabel = "⏸ Pause";
            IsRecording = mode == "rec"; IsLive = mode == "live"; IsBusy = IsRecording || IsLive;

            await _session.StartAsync(item, cfg, live ? IngestUrl : null, live ? StreamKey : null,
                path, _panelNative, IsPreviewVisible, CancellationToken.None);

            EncoderName = _session.EncoderName;
            Status = mode switch { "rec" => $"Enregistrement → {path}", "live" => "En direct", _ => "Aperçu" };
            Discord.UpdatePresence(
                mode switch { "rec" => "Recording", "live" => "Live Streaming", _ => "Previewing" },
                "FrameCast Studio"
            );
            if (mode != "preview" && _session.AudioWarning != null)
                Status += $" — ⚠ audio indisponible ({_session.AudioWarning})";
        }
        catch (Exception ex)
        {

            string errDetail = ex is System.Runtime.InteropServices.COMException com
                ? $"COM 0x{com.HResult:X8} — {com.Message}"
                : $"{ex.GetType().Name} — {ex.Message}";
            FrameCastStudio.Core.Log.Error("StartAsync(" + mode + ")", ex);
            await StopAsync(keepPreview: true);
            Status = "Erreur : " + errDetail;
        }
    }

    private async Task StopAsync(bool keepPreview)
    {
        var s = _session; bool wasRec = _mode == "rec"; var recPath = _recPath; _session = null; _mode = "";
        IsRecording = false; IsLive = false; IsBusy = false; IsPaused = false; PauseLabel = "⏸ Pause";
        if (s != null) { try { await s.DisposeAsync(); } catch { } }
        bool failed = Status.StartsWith("Erreur");
        if (!failed) Status = "Arrêté";
        Discord.UpdatePresence("Idle");
        if (wasRec && !failed && OpenFolderAfterRec && recPath != null)
        {
            try
            {
                string args = File.Exists(recPath) ? $"/select,\"{recPath}\"" : $"\"{OutputFolder}\"";
                Process.Start(new ProcessStartInfo("explorer.exe", args) { UseShellExecute = true });
            }
            catch { }
        }
        if (keepPreview && AutoStartPreview) await StartAsync("preview");
    }

    public async Task ShutdownAsync()
    {
        try { _updateCts.Cancel(); } catch { }
        await StopAsync(keepPreview: false);
        foreach (var s in AppAudioSessions) s.Dispose();
        AppAudioSessions.Clear();
        Discord.Dispose();
        _store.SaveNow();
    }

    private string BuildRecordPath()
    {
        string prefix = string.IsNullOrWhiteSpace(FilenamePrefixRec) ? "FrameCast" : FilenamePrefixRec.Trim();
        var bad = Path.GetInvalidFileNameChars();
        prefix = new string(prefix.Select(c => bad.Contains(c) ? '_' : c).ToArray());
        string name = UseTimestampInFilename ? $"{prefix}_{DateTime.Now:yyyyMMdd_HHmmss}" : prefix;
        string p = Path.Combine(OutputFolder, name + ".mp4");
        for (int i = 2; File.Exists(p); i++) p = Path.Combine(OutputFolder, $"{name}_{i}.mp4");
        return p;
    }

    [ObservableProperty] private bool screenshotBusy;

    // Per-app audio sessions
    public ObservableCollection<AppAudioSessionVm> AppAudioSessions { get; } = new();
    [ObservableProperty] private bool appMixerExpanded = true;

    public void RefreshAppAudioSessions()
    {
        var sessions = FrameCastStudio.Core.Audio.AppAudioSessionService.GetSessions();
        // dispose old
        foreach (var old in AppAudioSessions) old.Dispose();
        AppAudioSessions.Clear();
        foreach (var s in sessions)
            AppAudioSessions.Add(new AppAudioSessionVm(s));
    }

    public void RefreshAppAudioPeaks()
    {
        foreach (var s in AppAudioSessions) s.RefreshPeak();
    }

    [RelayCommand]
    private async Task Screenshot()
    {
        if (SelectedSource is null || ScreenshotBusy) return;
        ScreenshotBusy = true;
        var prevStatus = Status;
        try
        {
            Status = "Capture d'écran…";
            var r = await ScreenshotService.CaptureAsync(SelectedSource, ScreenshotFolder, CaptureCursor, CopyScreenshotToClipboard, ScreenshotFormat);
            Status = $"Capture enregistrée : {Path.GetFileName(r.FilePath)} ({r.Width}x{r.Height})";
        }
        catch (Exception ex)
        {
            Status = "Erreur capture : " + ex.Message;
        }
        finally { ScreenshotBusy = false; }
    }

    private void StartUpdateLoop()
    {
        if (Debugger.IsAttached) { UpdateStatus = "Mises à jour désactivées (débogueur attaché)."; return; }
        var outcome = UpdateState.ConsumeOutcome(CurrentVersion);
        if (outcome != null) FrameCastStudio.Core.Log.Write(outcome.Message);
        _ = Task.Run(() => GitHubUpdater.Cleanup(CurrentVersion));
        _ = UpdateLoopAsync(outcome?.Message);
    }

    private async Task UpdateLoopAsync(string? headline)
    {
        try
        {
            while (!_updateCts.IsCancellationRequested)
            {
                await CheckForUpdatesAsync(auto: true, headline);
                headline = null;
                await Task.Delay(TimeSpan.FromHours(6), _updateCts.Token);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { FrameCastStudio.Core.Log.Error("UpdateLoop", ex); }
    }

    [RelayCommand]
    private async Task CheckForUpdates() => await CheckForUpdatesAsync(auto: false);

    private async Task CheckForUpdatesAsync(bool auto, string? headline = null)
    {
        if (_checking || UpdateBusy) return;
        _checking = true;
        try
        {
            UpdateStatus = "Vérification…";
            var r = await Updater.CheckAsync(CurrentVersion, _updateCts.Token);
            string prefix = string.IsNullOrEmpty(headline) ? "" : headline + " ";
            switch (r.Status)
            {
                case UpdateCheckStatus.Available when r.Release is { } rel:
                    _pendingUpdate = rel;
                    UpdateNotes = rel.Notes.Length > 700 ? rel.Notes[..700] + "…" : rel.Notes;
                    UpdateAvailable = true;
                    RecalcUpdateButtons();
                    UpdateStatus = prefix + r.Message + (rel.AssetUrl is null ? " — aucun package pour cette architecture : ouvre la page de la release." : "");
                    if (auto && AutoDownloadUpdates && rel.AssetUrl != null && !UpdateState.IsBlocked(rel.Version) && _stageFailedVersion != rel.Version)
                        await StageUpdateAsync(silent: true);
                    break;
                case UpdateCheckStatus.Error:
                    UpdateStatus = prefix + r.Message;
                    break;
                default:
                    UpdateAvailable = false; UpdateReady = false; _pendingUpdate = null; _staged = null;
                    UpdateStatus = prefix + r.Message;
                    break;
            }
        }
        catch (OperationCanceledException) { }
        finally { _checking = false; }
    }

    private async Task<bool> StageUpdateAsync(bool silent)
    {
        var info = _pendingUpdate;
        if (info is null) return false;
        if (_staged?.Version == info.Version) { UpdateReady = true; return true; }

        UpdateBusy = true; UpdateProgress = 0;
        try
        {
            UpdateStatus = $"Téléchargement de la version {info.Version}…";
            var progress = new Progress<double>(p => { UpdateProgress = p * 100; UpdateStatus = $"Téléchargement de la version {info.Version}… {p:P0}"; });
            _staged = await Task.Run(() => Updater.DownloadAndStageAsync(info, progress, _updateCts.Token));
            UpdateReady = true;
            UpdateStatus = $"Version {info.Version} prête : elle s'installera à la fermeture, ou tout de suite avec « Redémarrer et installer ».";
            return true;
        }
        catch (OperationCanceledException) { UpdateStatus = "Téléchargement annulé."; return false; }
        catch (Exception ex)
        {
            FrameCastStudio.Core.Log.Error("Téléchargement de la mise à jour", ex);
            _stageFailedVersion = info.Version;
            UpdateStatus = $"Échec du téléchargement de la version {info.Version} : {ex.Message}";
            return false;
        }
        finally { UpdateBusy = false; }
    }

    [RelayCommand]
    private void OpenReleasePage()
    {
        if (_pendingUpdate is null) return;
        try { Process.Start(new ProcessStartInfo(_pendingUpdate.HtmlUrl) { UseShellExecute = true }); } catch { }
    }

    [RelayCommand]
    private void OpenGitHub()
    {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(AppInfo.GitHubUrl) { UseShellExecute = true }); } catch { }
    }

    [RelayCommand]
    private async Task InstallUpdate()
    {
        if (_pendingUpdate is null || UpdateBusy) return;
        if (IsBusy) { UpdateStatus = "Arrête d'abord l'enregistrement / le direct, puis relance l'installation."; return; }
        if (!await StageUpdateAsync(silent: false) || _staged is null) return;

        UpdateBusy = true;
        try
        {
            UpdateStatus = "Fermeture et installation…";
            UpdateState.Unblock();
            UpdateState.MarkPending(_staged.Version);
            await ShutdownAsync();
            Updater.ApplyStaged(_staged, AppContext.BaseDirectory, Environment.ProcessId, relaunch: true, allowElevation: true);
            _applyStarted = true;
            CloseRequested?.Invoke();
            _ = Task.Delay(10_000).ContinueWith(_ => Environment.Exit(0));
        }
        catch (Exception ex)
        {
            UpdateState.ClearPending();
            FrameCastStudio.Core.Log.Error("InstallUpdate", ex);
            UpdateStatus = ex is Win32Exception { NativeErrorCode: 1223 }
                ? "Installation annulée (autorisation administrateur refusée)."
                : "Échec de l'installation : " + ex.Message;
            if (AutoStartPreview) _ = StartAsync("preview");
        }
        finally { UpdateBusy = false; }
    }

    public void ApplyStagedUpdateOnExit()
    {
        if (!InstallUpdateOnExit || _applyStarted || _staged is null || Debugger.IsAttached) return;
        if (UpdateState.IsBlocked(_staged.Version)) return;
        try
        {
            UpdateState.MarkPending(_staged.Version);
            Updater.ApplyStaged(_staged, AppContext.BaseDirectory, Environment.ProcessId, relaunch: false, allowElevation: false);
            _applyStarted = true;
        }
        catch (Exception ex)
        {
            UpdateState.ClearPending();
            FrameCastStudio.Core.Log.Write("Installation à la fermeture ignorée : " + ex.Message);
        }
    }

    private async Task AutoStopAsync()
    {
        _autoStopping = true;
        try
        {
            FrameCastStudio.Core.Log.Write("Arrêt automatique : inactivité de l'écran");
            await StopAsync(keepPreview: true);
            Status = "Arrêté automatiquement (aucune activité à l'écran)";
        }
        finally { _autoStopping = false; }
    }

    public void RefreshTelemetry()
    {
        if (_session is null) return;
        var t = _session.Telemetry.Latest;
        Cpu = $"{t.CpuPct:F1} %"; CpuValue = t.CpuPct;
        Gpu = $"{t.GpuPct:F0} %"; GpuValue = t.GpuPct;
        bool encoding = _mode is "rec" or "live";
        double fps = encoding ? t.OutputFps : t.CaptureFps;
        FrameTime = fps > 0.5 ? $"{1000.0 / fps:F1} ms" : "— ms";
        string prev = IsPreviewVisible ? $"Aperçu {t.PreviewFps:F0} fps" : "Aperçu masqué";
        FpsInfo = encoding ? $"Sortie {t.OutputFps:F0} fps · {prev}" : $"Source {t.CaptureFps:F0} fps · {prev}";
        Ram = $"{t.RamMb:F0} MB";
        Bitrate = $"{t.BitrateKbps:F0} kbps · {t.Dropped} drop";
        if (_mode == "live" && _session.Rtmp != null && !IsPaused) Status = "RTMP : " + _session.Rtmp.State;

        if (AutoStopAfterInactivity && IsBusy && !IsPaused && !_autoStopping
            && Environment.TickCount64 - _session.LastFrameTick > (long)(AutoStopMinutes * 60_000))
            _ = AutoStopAsync();
    }
}
