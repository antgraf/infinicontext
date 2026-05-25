using System.Collections.Specialized;
using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using InfiniContext.Gui.ViewModels;

namespace InfiniContext.Gui.Views;

public partial class MainWindow : Window
{
    private MainWindowViewModel? _boundVm;
    private SessionRowViewModel? _watchedSession;
    // Pre-maximize windowed dimensions, captured from Window.Width/Height whenever WindowState
    // is Normal. While Maximized, Window.Width/Height are screen dimensions and the two-way
    // binding would push them into the VM, overwriting the user's last windowed size. OnClosed
    // restores VM.WindowWidth/Height from these snapshots so SaveLayoutAsync persists the value
    // to restore to on next un-maximize (task 0012 finding #6 follow-up).
    private double _lastNormalWidth;
    private double _lastNormalHeight;

    public MainWindow()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        Closed += OnClosed;
        // WindowState two-way mapping (Avalonia's enum <-> VM bool). PropertyChanged fires on
        // every state transition; we update the VM, and the VM's persisted value is flushed by
        // SaveLayoutAsync on close. The reverse direction (VM bool seeded from gui-settings.json
        // on launch) is applied in OnDataContextChanged once the deferred VM lands.
        PropertyChanged += OnWindowPropertyChanged;
    }

    private void OnWindowPropertyChanged(object? sender, Avalonia.AvaloniaPropertyChangedEventArgs e)
    {
        if (_boundVm is null) return;
        if (e.Property == WindowStateProperty)
        {
            var maximized = WindowState == WindowState.Maximized;
            if (_boundVm.WindowMaximized != maximized)
                _boundVm.WindowMaximized = maximized;
            return;
        }
        if (e.Property == WidthProperty || e.Property == HeightProperty)
        {
            // Only treat Normal-state dimensions as canonical. While Maximized, Width/Height are
            // screen dims and don't reflect what the operator wants restored to on next launch.
            if (WindowState == WindowState.Normal)
            {
                if (e.Property == WidthProperty) _lastNormalWidth = Width;
                else _lastNormalHeight = Height;
            }
        }
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        // Detach the previous VM (DataContext can swap when the deferred CreateAsync VM lands
        // over the placeholder one from App.OnFrameworkInitializationCompleted).
        if (_boundVm is not null)
        {
            _boundVm.PropertyChanged -= OnVmPropertyChanged;
        }
        WatchSession(null);

        _boundVm = DataContext as MainWindowViewModel;
        if (_boundVm is null) return;

        _boundVm.PropertyChanged += OnVmPropertyChanged;
        WatchSession(_boundVm.FocusedSession);

        // Seed the "last normal" dimensions from the persisted VM values so a close-while-
        // maximized without any intervening windowed resize still persists the right size.
        // (After this, any Width/Height PropertyChanged with WindowState == Normal refreshes
        // them; OnClosed reads these into the VM before SaveLayoutAsync runs.)
        if (_lastNormalWidth == 0) _lastNormalWidth = _boundVm.WindowWidth;
        if (_lastNormalHeight == 0) _lastNormalHeight = _boundVm.WindowHeight;

        // Apply persisted maximized state. Width/Height are already two-way bound and applied
        // by the binding system before Show; only the WindowState enum needs a one-shot apply
        // because the VM stores a bool (Normal vs Maximized only - we don't restore Minimized).
        if (_boundVm.WindowMaximized && WindowState != WindowState.Maximized)
            WindowState = WindowState.Maximized;

        // Wire OS folder picker (Avalonia StorageProvider is window-bound, hence the late wire).
        _boundVm.FolderPicker = async () =>
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

        _boundVm.ShowError = msg =>
        {
            // Phase 3B: surface to status text. Modal dialog wiring is a polish item; for now the
            // status strip line + log file capture every error.
            _boundVm.StatusText = msg.Split('\n')[0];
        };

        // "+ New task" dialog launcher. The VM stays free of Avalonia Window references; the
        // View hosts the dialog and returns the operator's choice.
        _boundVm.NewTaskPrompt = async () =>
        {
            var dialog = new NewTaskDialog();
            return await dialog.ShowAsync(this);
        };
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainWindowViewModel.FocusedSession))
        {
            WatchSession(_boundVm?.FocusedSession);
            // Newly-focused session: jump to the bottom so the operator sees the most recent
            // exchange first. (Auto-scroll respected; if the user disabled it, we leave them
            // where they were in the previous transcript.)
            if (_boundVm?.AutoScroll == true) ScrollToEndDeferred();
        }
        else if (e.PropertyName == nameof(MainWindowViewModel.AutoScroll) && _boundVm?.AutoScroll == true)
        {
            // Re-enabling auto-scroll snaps to the bottom once so the operator sees what they
            // missed - subsequent new turns continue to scroll via OnTurnsChanged.
            ScrollToEndDeferred();
        }
    }

    private void WatchSession(SessionRowViewModel? row)
    {
        if (_watchedSession is not null)
        {
            _watchedSession.Turns.CollectionChanged -= OnTurnsChanged;
        }
        _watchedSession = row;
        if (_watchedSession is not null)
        {
            _watchedSession.Turns.CollectionChanged += OnTurnsChanged;
        }
    }

    private void OnTurnsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (_boundVm?.AutoScroll == true) ScrollToEndDeferred();
    }

    private void ScrollToEndDeferred()
    {
        // Queue at Loaded priority: layout for the just-added item hasn't run yet, so calling
        // ScrollToEnd synchronously would scroll to the previous extent.
        Dispatcher.UIThread.Post(() => TranscriptScroll.ScrollToEnd(), DispatcherPriority.Loaded);
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        // If the user closes while maximized, the two-way Width/Height binding has been pushing
        // screen dimensions into the VM throughout the maximized session. Restore the last
        // windowed size before SaveLayoutAsync runs (via DisposeAsync from OnShutdownRequested)
        // so we persist the value to restore to on next un-maximize, not the screen dimensions
        // (task 0012 finding #6 follow-up).
        if (_boundVm is not null && WindowState == WindowState.Maximized)
        {
            if (_lastNormalWidth > 0) _boundVm.WindowWidth = _lastNormalWidth;
            if (_lastNormalHeight > 0) _boundVm.WindowHeight = _lastNormalHeight;
        }

        if (_boundVm is not null) _boundVm.PropertyChanged -= OnVmPropertyChanged;
        WatchSession(null);
        PropertyChanged -= OnWindowPropertyChanged;
    }
}
