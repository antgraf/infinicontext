using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using InfiniContext.Gui.Services;
using InfiniContext.Gui.ViewModels;
using InfiniContext.Gui.Views;

namespace InfiniContext.Gui;

public partial class App : Application
{
    private MainWindowViewModel? _mainVm;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // Load the small GUI layout file synchronously before the window is shown so it opens at
            // the persisted size AND on the persisted monitor from the first paint - no post-show
            // resize or cross-monitor jump (task 0021). LoadAsync uses ConfigureAwait(false)
            // throughout, so blocking the UI thread on this tiny file read can't deadlock against
            // Avalonia's sync context. The heavy bring-up (SessionHost process spawn, logger,
            // SessionManager) stays deferred below; the placeholder VM keeps the UI responsive and
            // we swap to the live VM - carrying this same settings instance - once StartAsync returns.
            // INFINICONTEXT__GUI__SETTINGSPATH redirects the layout file to a caller-chosen path
            // (mirrors INFINICONTEXT__TASKS__DBPATH) so the UIA smoke harness can verify window
            // placement against a temp file without disturbing the operator's real layout. LoadAsync
            // stamps SourcePath on the instance, so the later SaveLayoutAsync writes back to the same
            // path automatically - no need to thread it through the VM.
            var settingsPathOverride = Environment.GetEnvironmentVariable("INFINICONTEXT__GUI__SETTINGSPATH");
            if (string.IsNullOrWhiteSpace(settingsPathOverride)) settingsPathOverride = null;
            var guiSettings = GuiSettings.LoadAsync(settingsPathOverride).GetAwaiter().GetResult();

            var window = new MainWindow { DataContext = new MainWindowViewModel(guiSettings) };
            desktop.MainWindow = window;
            desktop.ShutdownRequested += OnShutdownRequested;

            _ = Dispatcher.UIThread.InvokeAsync(async () =>
            {
                try
                {
                    _mainVm = await MainWindowViewModel.CreateAsync(
                        AvaloniaUiDispatcher.Instance,
                        AvaloniaUiTimerFactory.Instance,
                        guiSettings);
                    window.DataContext = _mainVm;
                }
                catch (Exception ex)
                {
                    if (window.DataContext is MainWindowViewModel placeholder)
                    {
                        placeholder.StatusText = $"Startup failed: {ex.Message}";
                    }
                }
            });
        }

        base.OnFrameworkInitializationCompleted();
    }

    private void OnShutdownRequested(object? sender, ShutdownRequestedEventArgs e)
    {
        if (_mainVm is null) return;
        // Hold shutdown briefly so the async dispose (Serilog flush, session teardown) completes.
        e.Cancel = true;
        _ = Dispatcher.UIThread.InvokeAsync(async () =>
        {
            try { await _mainVm.DisposeAsync(); }
            finally
            {
                if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
                {
                    desktop.ShutdownRequested -= OnShutdownRequested;
                    desktop.Shutdown();
                }
            }
        });
    }
}
