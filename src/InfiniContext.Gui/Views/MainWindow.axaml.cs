using System.Collections.Specialized;
using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using InfiniContext.Gui.ViewModels;

namespace InfiniContext.Gui.Views;

public partial class MainWindow : Window
{
    private MainWindowViewModel? _boundVm;
    private SessionRowViewModel? _watchedSession;
    // One realized transcript view per session row, parented under TranscriptHost for the row's
    // whole lifetime and toggled via IsVisible on focus change (task 0016). Detaching a live
    // Markdown.Avalonia subtree is what corrupted/crashed the pane in the 0014/0015 designs -
    // see the transcript-host comment in MainWindow.axaml for the full failure chain.
    private readonly Dictionary<SessionRowViewModel, ThemeVariantScope> _transcriptViews = new();
    // Pre-maximize windowed dimensions, captured from Window.Width/Height whenever WindowState
    // is Normal. While Maximized, Window.Width/Height are screen dimensions and the two-way
    // binding would push them into the VM, overwriting the user's last windowed size. OnClosed
    // restores VM.WindowWidth/Height from these snapshots so SaveLayoutAsync persists the value
    // to restore to on next un-maximize (task 0012 finding #6 follow-up).
    private double _lastNormalWidth;
    private double _lastNormalHeight;
    // Window's most recent Normal-state top-left (physical pixels) - the source of truth for the
    // restored position. Maintained by OnWindowPositionChanged (operator moves), the pre-show
    // restore, and the OnOpened off-screen correction. While Maximized, Window.Position is the
    // maximized origin, so OnClosed restores VM.WindowX/Y from this before SaveLayoutAsync persists
    // the value to reopen at - on the monitor the operator last used (task 0021).
    private PixelPoint? _lastNormalPosition;
    // The placeholder VM (seeded from gui-settings before Show) and the live VM (swapped in after
    // the async host bring-up) both carry the same persisted coordinates. We apply the initial
    // Position exactly once, on the first attach, so the second attach can't yank the window back
    // if the operator nudged it during the load (task 0021).
    private bool _windowPlacementRestored;

    public MainWindow()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        Closed += OnClosed;
        // Post-show off-screen sanity check for the restored position. Screens isn't reliably
        // populated before the window is realized, so the pre-show restore sets Position blind and
        // Opened validates it once the display list exists (task 0021).
        Opened += OnOpened;
        // WindowState two-way mapping (Avalonia's enum <-> VM bool). PropertyChanged fires on
        // every state transition; we update the VM, and the VM's persisted value is flushed by
        // SaveLayoutAsync on close. The reverse direction (VM bool seeded from gui-settings.json
        // on launch) is applied in OnDataContextChanged once the deferred VM lands.
        PropertyChanged += OnWindowPropertyChanged;
        // Window.Position is a plain CLR property (not an AvaloniaProperty), so it can't be
        // two-way bound like Width/Height - we track it through its dedicated event and persist
        // via the VM. The initial value is restored pre-show in OnDataContextChanged (task 0021).
        PositionChanged += OnWindowPositionChanged;
        // Any click in the session tree hands keyboard focus back to the prompt box (when a
        // session is focused). The FocusedSession hook in OnVmPropertyChanged covers selection
        // CHANGES, but re-clicking the already-selected row raises no PropertyChanged - focus
        // would stay on the TreeItem and typing would be silently swallowed (task 0017).
        // handledEventsToo because TreeViewItem handles pointer events for selection.
        this.FindControl<TreeView>("SessionTree")?.AddHandler(
            PointerReleasedEvent,
            (_, _) => { if (_boundVm?.FocusedSession is not null) FocusPromptBoxDeferred(); },
            RoutingStrategies.Bubble,
            handledEventsToo: true);
        // After a mouse-click send, hand focus straight back to the box so the operator can
        // type the next message without re-clicking (Ctrl+Enter sends never lose it).
        this.FindControl<Button>("SendButton")?.AddHandler(
            Button.ClickEvent,
            (_, _) => FocusPromptBoxDeferred(),
            RoutingStrategies.Bubble,
            handledEventsToo: true);
    }

    private void OnWindowPropertyChanged(object? sender, Avalonia.AvaloniaPropertyChangedEventArgs e)
    {
        // Keep every cached transcript view's pinned variant in lockstep with the window's, so
        // an OS light/dark flip still restyles the markdown. Pinning (rather than inheriting)
        // is what shields the views from the detach-time variant cascade - see _transcriptViews.
        if (e.Property == ActualThemeVariantProperty)
        {
            foreach (var scope in _transcriptViews.Values)
                scope.RequestedThemeVariant = ActualThemeVariant;
            return;
        }
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

    private void OnWindowPositionChanged(object? sender, PixelPointEventArgs e)
    {
        if (_boundVm is null) return;
        // Only Normal-state position is canonical (mirrors the Width/Height handling above). While
        // Maximized, the top-left is the maximized origin, not the windowed position we restore to.
        if (WindowState != WindowState.Normal) return;
        _lastNormalPosition = e.Point;
        _boundVm.WindowX = e.Point.X;
        _boundVm.WindowY = e.Point.Y;
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

        // Restore the window onto the monitor it was last closed on, before applying maximized
        // state so a maximize lands on the right display. On the first attach this runs against the
        // placeholder VM before Show (App loads gui-settings synchronously and seeds it), so the
        // window opens directly on the persisted monitor - no post-show jump. Position is applied
        // here (not via a binding) because Window.Position is a plain CLR property (task 0021).
        TryRestoreWindowPosition();

        // Mirror the window's current Normal-state top-left into the (re)attached VM. On the live-VM
        // swap this overwrites the file's original coordinates with whatever the window is actually
        // at now (a since-applied off-screen correction, or an operator nudge during load), so
        // SaveLayoutAsync on close persists reality rather than the stale loaded value.
        if (_lastNormalPosition is { } current)
        {
            _boundVm.WindowX = current.X;
            _boundVm.WindowY = current.Y;
        }

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
            dialog.SetModelChoices(_boundVm.BuildModelChoices());
            return await dialog.ShowAsync(this);
        };

        // Confirmation modal for the /task review cost guard. The VM stays free of Avalonia Window
        // references; the View hosts the dialog and returns the operator's yes/no (task 0017).
        _boundVm.ConfirmReview = async message =>
        {
            var dialog = new ConfirmDialog(message);
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
            // Land keyboard focus in the prompt box. Focusing a session happens by clicking in
            // the TreeView ("+" spawn or row select), which leaves keyboard focus on the
            // TreeItem - typing then goes nowhere, PromptText stays empty, and Send (gated on
            // non-empty text) reads as "always disabled for every session" (task 0017). Deferred
            // to Input priority so it runs after the click's own focus handling.
            if (_boundVm?.FocusedSession is not null) FocusPromptBoxDeferred();
        }
        else if (e.PropertyName == nameof(MainWindowViewModel.AutoScroll) && _boundVm?.AutoScroll == true)
        {
            // Re-enabling auto-scroll snaps to the bottom once so the operator sees what they
            // missed - subsequent new turns continue to scroll via OnTurnsChanged.
            ScrollToEndDeferred();
        }
    }

    /// <summary>Apply the persisted top-left once, on the first VM attach (pre-show). We don't
    /// validate against <see cref="Screens"/> here because the platform display list isn't reliably
    /// populated before the window is realized; <see cref="OnOpened"/> does the off-screen sanity
    /// check once it is. Physical pixels, so there's no DIP conversion (task 0021).</summary>
    private void TryRestoreWindowPosition()
    {
        if (_windowPlacementRestored) return;
        if (_boundVm?.WindowX is not int x || _boundVm.WindowY is not int y) return;
        _windowPlacementRestored = true;
        var saved = new PixelPoint(x, y);
        WindowStartupLocation = WindowStartupLocation.Manual;
        Position = saved;
        _lastNormalPosition = saved;
    }

    /// <summary>Post-show correction: now that the window is realized and <see cref="Screens"/> is
    /// populated, pull it back onto a connected display if the restored top-left landed on a monitor
    /// that's since been disconnected (or the layout changed). Without this the window could sit
    /// off-screen and unreachable. Centers within the target screen's working area (task 0021).</summary>
    private void OnOpened(object? sender, EventArgs e)
    {
        if (_boundVm is null) return;
        // ScreenFromPoint returns null when the current top-left is on no connected display.
        if (Screens is null || Screens.ScreenFromPoint(Position) is not null) return;

        var target = Screens.Primary ?? Screens.All.FirstOrDefault();
        if (target is null) return;

        // WorkingArea and Position are both physical pixels; scale the DIP Width/Height to match.
        var area = target.WorkingArea;
        var pixelWidth = (int)(Width * target.Scaling);
        var pixelHeight = (int)(Height * target.Scaling);
        var corrected = new PixelPoint(
            area.X + Math.Max(0, (area.Width - pixelWidth) / 2),
            area.Y + Math.Max(0, (area.Height - pixelHeight) / 2));

        Position = corrected;
        _lastNormalPosition = corrected;
        _boundVm.WindowX = corrected.X;
        _boundVm.WindowY = corrected.Y;
    }

    private void FocusPromptBoxDeferred() =>
        Dispatcher.UIThread.Post(
            () => this.FindControl<TextBox>("PromptBox")?.Focus(),
            DispatcherPriority.Input);

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
        ShowTranscript(row);
    }

    /// <summary>
    /// Show <paramref name="row"/>'s transcript view, creating it on first focus (task 0016).
    /// Each session gets ONE <see cref="ItemsControl"/> bound to its
    /// <see cref="SessionRowViewModel.Turns"/>, parented under <c>TranscriptHost</c> for the
    /// row's whole lifetime; focus changes only flip <c>IsVisible</c>. This replaces the task
    /// 0014/0015 designs that swapped a ContentControl's Content per switch: any swap detaches
    /// the previously focused session's realized Markdown.Avalonia visuals, and that detach
    /// changes their inherited <c>ActualThemeVariant</c>, making ColorTextBlock's
    /// DynamicResource-backed MultiBindings re-resolve Fluent <c>Color</c> resources into
    /// <c>IBrush</c> properties - an InvalidCastException that fired mid-swap (corrupting the
    /// pane, mislabelling the second spawn as failed, and eventually killing the app). The
    /// <see cref="ThemeVariantScope"/> wrapper pins each view's variant so even the one
    /// remaining detach (pruning a closed session's view) cannot re-trigger the cascade.
    /// </summary>
    private void ShowTranscript(SessionRowViewModel? row)
    {
        if (TranscriptHost is null) return;
        PruneClosedTranscripts();
        foreach (var view in _transcriptViews.Values)
            view.IsVisible = false;
        if (row is null) return;

        if (!_transcriptViews.TryGetValue(row, out var scope))
        {
            scope = new ThemeVariantScope
            {
                RequestedThemeVariant = ActualThemeVariant,
                Child = new ItemsControl
                {
                    ItemsSource = row.Turns,
                    ItemTemplate = (IDataTemplate)Resources["TurnNodeTemplate"]!,
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                },
            };
            _transcriptViews[row] = scope;
            TranscriptHost.Children.Add(scope);
        }
        scope.IsVisible = true;
    }

    /// <summary>Drop cached transcript views whose session row is no longer anywhere in the VM
    /// tree (closed sessions, or rows belonging to a swapped-out DataContext). Safe to detach
    /// here: the view's pinned <see cref="ThemeVariantScope"/> keeps descendant variants stable
    /// through the removal, and the session is gone so nothing re-renders it.</summary>
    private void PruneClosedTranscripts()
    {
        if (_transcriptViews.Count == 0) return;
        var live = _boundVm is null
            ? new HashSet<SessionRowViewModel>()
            : _boundVm.TaskNodes.SelectMany(n => n.Sessions).ToHashSet();
        foreach (var (row, view) in _transcriptViews.Where(kv => !live.Contains(kv.Key)).ToList())
        {
            TranscriptHost.Children.Remove(view);
            _transcriptViews.Remove(row);
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
            // Same maximized-close restore for position: persist the windowed top-left (on the
            // right monitor), not the maximized origin the live Position now reports.
            if (_lastNormalPosition is { } pos)
            {
                _boundVm.WindowX = pos.X;
                _boundVm.WindowY = pos.Y;
            }
        }

        if (_boundVm is not null) _boundVm.PropertyChanged -= OnVmPropertyChanged;
        WatchSession(null);
        PropertyChanged -= OnWindowPropertyChanged;
        PositionChanged -= OnWindowPositionChanged;
        Opened -= OnOpened;
    }
}
