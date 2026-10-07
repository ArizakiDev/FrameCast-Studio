using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WinUIEx;

namespace FrameCastStudio.App;

public sealed partial class MainWindow : WindowEx
{
    public MainViewModel Vm { get; } = new();

    private readonly DispatcherQueueTimer _statsTimer;
    private readonly DispatcherQueueTimer _meterTimer;
    private bool _initialized, _closing;
    private TrayIcon? _tray;

    public MainWindow()
    {
        InitializeComponent();
        try
        {
            string ico = Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico");
            if (File.Exists(ico)) AppWindow.SetIcon(ico);
        }
        catch {  }
        Vm.AttachPreview(Preview);

        _statsTimer = DispatcherQueue.CreateTimer();
        _statsTimer.Interval = TimeSpan.FromSeconds(1);
        _statsTimer.IsRepeating = true;
        _statsTimer.Tick += (_, _) => Vm.RefreshTelemetry();
        _statsTimer.Start();

        _meterTimer = DispatcherQueue.CreateTimer();
        _meterTimer.Interval = TimeSpan.FromMilliseconds(50);
        _meterTimer.IsRepeating = true;
        _meterTimer.Tick += (_, _) =>
        {
            if (MixerPanel.Visibility == Visibility.Visible) Vm.RefreshMeters();
            Vm.RefreshAppAudioPeaks();
        };
        _meterTimer.Start();

        AppWindow.Changed += AppWindow_Changed;
        AppWindow.Closing += AppWindow_Closing;
        Vm.CloseRequested += () => DispatcherQueue.TryEnqueue(Close);

        Activated += async (_, _) =>
        {
            if (_initialized) return; _initialized = true;
            await Vm.InitializeAsync();
            Vm.RefreshAppAudioSessions();
            if (Vm.StartMinimized)
            {
                if (Vm.MinimizeToTray) HideToTray();
                else (AppWindow.Presenter as OverlappedPresenter)?.Minimize();
            }
        };
    }

    private async void AppWindow_Closing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_closing) return;
        args.Cancel = true;
        _closing = true;
        try { await Vm.ShutdownAsync(); } catch (Exception ex) { FrameCastStudio.Core.Log.Error("Fermeture", ex); }
        try { Vm.ApplyStagedUpdateOnExit(); } catch (Exception ex) { FrameCastStudio.Core.Log.Error("Mise à jour à la fermeture", ex); }
        try { _statsTimer.Stop(); _meterTimer.Stop(); _tray?.Dispose(); } catch { }
        Close();
    }

    private void AppWindow_Changed(AppWindow sender, AppWindowChangedEventArgs args)
    {
        if (args.DidPresenterChange && Vm.MinimizeToTray
            && sender.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized })
            HideToTray();
    }

    private void HideToTray()
    {
        if (_tray is null)
        {
            _tray = new TrayIcon("FrameCast Studio");
            _tray.ShowRequested += () => DispatcherQueue.TryEnqueue(RestoreFromTray);
            _tray.QuitRequested += () => DispatcherQueue.TryEnqueue(Close);
        }
        _tray.Show();
        AppWindow.Hide();
    }

    private void RestoreFromTray()
    {
        AppWindow.Show();
        (AppWindow.Presenter as OverlappedPresenter)?.Restore();
        Activate();
        _tray?.Hide();
    }

    private void NavView_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        var tag = (args.SelectedItemContainer as NavigationViewItem)?.Tag as string ?? "dashboard";
        foreach (var (panel, key) in new (FrameworkElement, string)[]
        {
            (DashboardPanel, "dashboard"), (RecordingPanel, "recording"), (StreamPanel, "stream"),
            (VideoPanel, "video"), (AudioPanel, "audio"), (MixerPanel, "mixer"), (EncoderPanel, "encoder"),
            (AdvancedPanel, "advanced"), (UpdatesPanel, "updates"), (AboutPanel, "about"),
        })
        {
            panel.Visibility = key == tag ? Visibility.Visible : Visibility.Collapsed;
        }
    }
}
