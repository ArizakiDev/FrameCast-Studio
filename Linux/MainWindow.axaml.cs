using Avalonia.Controls;
using Avalonia.Platform.Storage;
using FrameCastStudio.Linux.ViewModels;

namespace FrameCastStudio.Linux;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        Opened += async (_, _) =>
        {
            if (DataContext is not MainViewModel vm) return;
            vm.PickFolderAsync = PickFolderAsync;
            vm.ShowRequested += () =>
            {
                if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
                Activate();
            };
            await vm.InitializeAsync();
        };
    }

    private async Task<string?> PickFolderAsync()
    {
        var res = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { AllowMultiple = false, Title = "Choisir un dossier" });
        return res.Count > 0 ? res[0].TryGetLocalPath() : null;
    }
}
