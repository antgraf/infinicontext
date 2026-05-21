using Avalonia.Controls;
using Avalonia.Platform.Storage;
using InfiniContext.Gui.ViewModels;

namespace InfiniContext.Gui.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm) return;

        // Wire OS folder picker (Avalonia StorageProvider is window-bound, hence the late wire).
        vm.FolderPicker = async () =>
        {
            var picker = StorageProvider;
            if (picker is null) return null;
            var folders = await picker.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                AllowMultiple = false,
                Title = "Pick a working directory",
            });
            return folders.Count == 0 ? null : folders[0].TryGetLocalPath();
        };

        vm.ShowError = msg =>
        {
            // Phase 3B: surface to status text. Modal dialog wiring is a polish item; for now the
            // status strip line + log file capture every error.
            vm.StatusText = msg.Split('\n')[0];
        };
    }
}
