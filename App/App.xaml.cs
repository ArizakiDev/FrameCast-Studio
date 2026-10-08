using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;

namespace FrameCastStudio.App;

public partial class App : Application
{
    private Window? _window;

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBoxW(IntPtr hWnd, string text, string caption, uint type);

    public App()
    {
        // Rien ne doit planter en silence : tout est écrit dans %LOCALAPPDATA%\FrameCastStudio\log.txt
        UnhandledException += (_, e) =>
        {
            e.Handled = true;
            FailStartup(e.Exception);
        };
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            FrameCastStudio.Core.Log.Write("ERREUR FATALE [AppDomain] " + e.ExceptionObject);
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            FrameCastStudio.Core.Log.Error("TaskScheduler", e.Exception);
            e.SetObserved();
        };

        try { InitializeComponent(); }
        catch (Exception ex) { FailStartup(ex); }
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        try
        {
            _window = new MainWindow();
            _window.Activate();
        }
        catch (Exception ex) { FailStartup(ex); }
    }

    // Contexte utile pour comprendre un échec de chargement XAML (fichiers compilés manquants, culture, runtime…).
    private static void LogEnvironment(Exception ex)
    {
        try
        {
            string dir = AppContext.BaseDirectory;
            var w = FrameCastStudio.Core.Log.Write;
            w($"  HRESULT=0x{ex.HResult:X8}  dossier={dir}");
            w($"  culture UI={System.Globalization.CultureInfo.CurrentUICulture.Name}  runtime={System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription}  proc={System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture}");
            foreach (var f in new[] { "MainWindow.xbf", "App.xbf", "Microsoft.UI.Xaml.Controls.dll", "Microsoft.ui.xaml.dll", "Assets\\logo.png" })
                w($"  {f} : " + (File.Exists(Path.Combine(dir, f)) ? "présent" : "MANQUANT"));
            foreach (var f in Directory.GetFiles(dir, "*.pri"))
                w($"  {Path.GetFileName(f)} : {new FileInfo(f).Length} octets");
            for (var inner = ex.InnerException; inner != null; inner = inner.InnerException)
                w($"  interne : {inner.GetType().Name} 0x{inner.HResult:X8} {inner.Message}");
        }
        catch { }
    }

    private static void FailStartup(Exception ex)
    {
        FrameCastStudio.Core.Log.Error("Démarrage", ex);
        LogEnvironment(ex);
        // En test de lancement automatique (publish.bat) on ne bloque pas sur une boîte de dialogue.
        if (Environment.GetEnvironmentVariable("FRAMECAST_SMOKETEST") != "1")
        try
        {
            MessageBoxW(IntPtr.Zero,
                "FrameCast Studio a rencontré une erreur fatale.\n\n" + ex.GetType().Name + " : " + ex.Message +
                "\n\nDétails : " + FrameCastStudio.Core.Log.PathFile,
                "FrameCast Studio", 0x10);
        }
        catch { }
        Environment.Exit(1);
    }
}
