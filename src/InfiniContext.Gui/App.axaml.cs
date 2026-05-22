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
            // Defer the heavy bring-up (settings, logger, SessionManager) so the window paints
            // immediately. The placeholder VM keeps the UI responsive; we swap to the live VM
            // once SessionHost.StartAsync returns.
            var window = new MainWindow { DataContext = new MainWindowViewModel() };
            desktop.MainWindow = window;
            desktop.ShutdownRequested += OnShutdownRequested;

            _ = Dispatcher.UIThread.InvokeAsync(async () =>
            {
                try
                {
                    _mainVm = await MainWindowViewModel.CreateAsync(
                        AvaloniaUiDispatcher.Instance,
                        AvaloniaUiTimerFactory.Instance);
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
