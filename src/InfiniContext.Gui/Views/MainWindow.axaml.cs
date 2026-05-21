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

    public MainWindow()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        Closed += OnClosed;
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
        if (_boundVm is not null) _boundVm.PropertyChanged -= OnVmPropertyChanged;
        WatchSession(null);
    }
}
