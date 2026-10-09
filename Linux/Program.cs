using Avalonia;
using FrameCastStudio.Linux.Services;

namespace FrameCastStudio.Linux;

internal static class Program
{
    private static readonly Dictionary<string, string> Commands = new()
    {
        ["--toggle-record"] = "toggle-record",
        ["--toggle-live"] = "toggle-live",
        ["--save-replay"] = "save-replay",
        ["--screenshot"] = "screenshot",
        ["--stop"] = "stop",
        ["--show"] = "show",
    };

    public static IpcServer? Ipc { get; private set; }

    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Any(a => a is "--help" or "-h"))
        {
            Console.WriteLine("FrameCast Studio " + Updater.CurrentVersionText + " (Linux)\n" +
                "Commandes envoyées à l'instance déjà ouverte :\n  " + string.Join("\n  ", Commands.Keys) +
                "\nVariables : FRAMECAST_FFMPEG=/chemin/ffmpeg");
            return 0;
        }
        if (args.Any(a => a is "--version" or "-v")) { Console.WriteLine(Updater.CurrentVersionText); return 0; }

        // Commande CLI → on la transmet à l'instance en cours puis on quitte.
        foreach (var a in args)
        {
            if (!Commands.TryGetValue(a, out var cmd)) continue;
            if (IpcServer.TrySend(cmd)) return 0;
            if (cmd != "show")
            {
                Console.Error.WriteLine("FrameCast Studio n'est pas lancé.");
                return 1;
            }
        }

        // Instance unique : une 2e ouverture ramène simplement la fenêtre existante.
        if (IpcServer.TrySend("show")) return 0;

        Ipc = new IpcServer();
        Ipc.Start();
        try
        {
            Log.Write($"FrameCast Studio {Updater.CurrentVersionText} démarre");
            return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        catch (Exception ex)
        {
            Log.Error("Plantage", ex);
            Console.Error.WriteLine(ex);
            return 2;
        }
        finally { Ipc.Dispose(); }
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>().UsePlatformDetect().WithInterFont().LogToTrace();
}
