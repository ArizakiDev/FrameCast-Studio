using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using FrameCastStudio.Linux.ViewModels;

namespace FrameCastStudio.Linux;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var vm = new MainViewModel();
            var win = new MainWindow { DataContext = vm };
            desktop.MainWindow = win;
            Program.Ipc!.Command += vm.HandleCommand;
            desktop.Exit += (_, _) => Task.Run(() => vm.ShutdownAsync()).GetAwaiter().GetResult();
        }
        base.OnFrameworkInitializationCompleted();
    }
}
