using System.Collections.ObjectModel;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using InfiniContext.Core.Sessions;
using InfiniContext.Core.Tasks;
using InfiniContext.Gui.Services;

namespace InfiniContext.Gui.ViewModels;

/// <summary>
/// Root view-model. Owns the <see cref="SessionHost"/>, the left-column tree of tasks &amp;
/// sessions, and the focused-session pointer. The TreeView's selection drives two derived
/// pointers: <see cref="FocusedSession"/> (when a session row is picked) and
/// <see cref="SelectedTaskNode"/> (the task most recently focused - used by tree-row commands).
/// </summary>
public sealed partial class MainWindowViewModel : ObservableObject, IAsyncDisposable
{
    private readonly SessionHost? _host;
    private readonly GuiSettings _guiSettings;
    private readonly TaskNodeViewModel _unattached = new(null);

    public ObservableCollection<TaskNodeViewModel> TaskNodes { get; } = new();

    /// <summary>Left-column width as a Grid-bindable <see cref="GridLength"/>. Two-way bound to
    /// <c>ColumnDefinition.Width</c> so dragging the splitter updates this directly. The
    /// double form is persisted via <see cref="GuiSettings"/> on <c>SaveLayoutAsync</c>.</summary>
    [ObservableProperty]
    private GridLength _leftColumnWidth = new(GuiSettings.Defaults.LeftColumnWidth);

    public double LeftColumnMinWidth => GuiSettings.Defaults.LeftColumnMin;
    public double LeftColumnMaxWidth => GuiSettings.Defaults.LeftColumnMax;

    /// <summary>Whichever tree item the operator clicked - <see cref="TaskNodeViewModel"/> or
    /// <see cref="SessionRowViewModel"/>. Two-way bound from <c>TreeView.SelectedItem</c>.</summary>
    [ObservableProperty]
    private object? _selectedNode;

    [ObservableProperty]
    private SessionRowViewModel? _focusedSession;

    /// <summary>The task context "+ New session" will spawn into. Derived from <see cref="SelectedNode"/>:
    /// either the selected task header, or (if a session row is selected) that session's parent task.
    /// Null when nothing relevant is selected or only the (free sessions) bucket is in play - in
    /// which case "+ New session" falls back to the folder picker.</summary>
    [ObservableProperty]
    private TaskNodeViewModel? _selectedTaskNode;

    [ObservableProperty]
    private string _statusText = "Ready.";

    [ObservableProperty]
    private string _logFilePath = "(host not initialised)";

    /// <summary>When true, the transcript ScrollViewer auto-scrolls to the bottom on each new
    /// turn. View code-behind owns the actual scroll call; the VM only carries the flag so the
    /// checkbox state survives focus changes.</summary>
    [ObservableProperty]
    private bool _autoScroll = true;

    public Func<Task<string?>>? FolderPicker { get; set; }
    public Action<string>? ShowError { get; set; }

    [ObservableProperty]
    private string _promptText = "";

    public IAsyncRelayCommand NewTaskCommand { get; }
    public IAsyncRelayCommand<TaskNodeViewModel?> SpawnSessionForTaskCommand { get; }
    public IAsyncRelayCommand SendPromptCommand { get; }
    public IAsyncRelayCommand InterruptCommand { get; }
    public IAsyncRelayCommand<SessionRowViewModel?> SendContinueCommand { get; }
    public IAsyncRelayCommand<SessionRowViewModel?> SendWhatsNextCommand { get; }

    /// <summary>Canned prompt fired by the play button on a session row's action bar.</summary>
    public const string ContinuePromptText = "Continue with the task.";
    /// <summary>Canned prompt fired by the question button on a session row's action bar.</summary>
    public const string WhatsNextPromptText = "What's next for the task?";

    public MainWindowViewModel() : this(new GuiSettings())
    {
    }

    private MainWindowViewModel(GuiSettings guiSettings)
    {
        _guiSettings = guiSettings;
        _leftColumnWidth = new GridLength(guiSettings.LeftColumnWidth);
        NewTaskCommand = new AsyncRelayCommand(NewTaskAsync);
        SpawnSessionForTaskCommand = new AsyncRelayCommand<TaskNodeViewModel?>(SpawnSessionForTaskAsync);
        SendPromptCommand = new AsyncRelayCommand(SendPromptAsync, CanSendPrompt);
        InterruptCommand = new AsyncRelayCommand(InterruptAsync, CanInterrupt);
        SendContinueCommand = new AsyncRelayCommand<SessionRowViewModel?>(s => SendCannedAsync(s, ContinuePromptText));
        SendWhatsNextCommand = new AsyncRelayCommand<SessionRowViewModel?>(s => SendCannedAsync(s, WhatsNextPromptText));
        // _unattached is added lazily on first free-session spawn and removed when emptied -
        // keeps the tree clean for users who only work via task-bound sessions.
    }

    private MainWindowViewModel(SessionHost host, GuiSettings guiSettings) : this(guiSettings)
    {
        _host = host;
        LogFilePath = host.LogFilePath;
        host.SessionClosed += OnSessionClosed;
    }

    public static async Task<MainWindowViewModel> CreateAsync()
    {
        var host = await SessionHost.StartAsync().ConfigureAwait(false);
        var guiSettings = await GuiSettings.LoadAsync(logger: host.Logger).ConfigureAwait(false);
        var vm = new MainWindowViewModel(host, guiSettings);
        await vm.LoadTasksAsync().ConfigureAwait(false);
        return vm;
    }

    /// <summary>Persist the current layout (column widths, etc.) to <c>~/.infinicontext/gui-settings.json</c>.
    /// Best-effort; failures are logged but don't propagate - losing a width tweak is not worth
    /// crashing the GUI on close.</summary>
    public async Task SaveLayoutAsync()
    {
        _guiSettings.LeftColumnWidth = LeftColumnWidth.IsAbsolute ? LeftColumnWidth.Value : GuiSettings.Defaults.LeftColumnWidth;
        try
        {
            await _guiSettings.SaveAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _host?.Logger.Warning(ex, "gui-settings: SaveAsync failed");
        }
    }

    private async Task LoadTasksAsync()
    {
        if (_host is null) return;
        var records = await _host.ListTasksAsync().ConfigureAwait(true);

        void Apply()
        {
            TaskNodes.Clear();
            foreach (var rec in records.OrderByDescending(r => r.Id))
                TaskNodes.Add(new TaskNodeViewModel(rec));
        }

        if (Dispatcher.UIThread.CheckAccess()) Apply();
        else Dispatcher.UIThread.Post(Apply);
    }

    private TaskNodeViewModel ResolveParentNode(int? taskId)
    {
        if (taskId is null)
        {
            // Lazy-add the free-sessions bucket on first use so users with task-bound flows
            // don't see a permanent empty row.
            if (!TaskNodes.Contains(_unattached)) TaskNodes.Add(_unattached);
            return _unattached;
        }
        foreach (var node in TaskNodes)
            if (node.TaskId == taskId) return node;
        if (!TaskNodes.Contains(_unattached)) TaskNodes.Add(_unattached);
        return _unattached;
    }

    partial void OnSelectedNodeChanged(object? oldValue, object? newValue)
    {
        // Update both derived pointers based on what kind of tree item is now selected.
        switch (newValue)
        {
            case SessionRowViewModel session:
                FocusedSession = session;
                SelectedTaskNode = TaskNodes.FirstOrDefault(t => t.Sessions.Contains(session));
                break;
            case TaskNodeViewModel task:
                SelectedTaskNode = task;
                // Don't clear FocusedSession when a task header is clicked - the operator may
                // be readying a new session under that task without losing context of the one
                // they're reading.
                break;
            default:
                // Selection cleared entirely - keep prior focused session, clear task ptr.
                SelectedTaskNode = null;
                break;
        }
    }

    /// <summary>"+ New task" top-bar button. Stub for now - the full flow needs a modal that
    /// captures title + home_cwd, then writes via <c>TaskStore.AllocateIdAsync</c> +
    /// <c>TaskFolder.ScaffoldAsync</c>. Tracked as a follow-up; this stub just surfaces the gap
    /// so the button is wired before the dialog lands.</summary>
    private Task NewTaskAsync()
    {
        StatusText = "+ New task: dialog not wired yet (stub).";
        return Task.CompletedTask;
    }

    /// <summary>Spawn a new session under <paramref name="task"/>. Bound to the per-task "+"
    /// action button in the left tree. Falls back to the folder picker if <paramref name="task"/>
    /// is null or synthetic (the (free sessions) bucket).
    ///
    /// <para>For task-bound spawns, calls <see cref="TaskService.PrepareSwitchAsync"/> to build
    /// the pointer message (<see cref="TaskSessionPointer"/>) and passes it through as
    /// <c>SpawnRequest.FirstUserMessage</c>. Mirrors the CLI's <c>/task switch</c> /
    /// <c>/task spawn</c> path so the agent learns its task binding before the operator types.
    /// If the task subsystem is unavailable or <c>PrepareSwitchAsync</c> fails, the session still
    /// spawns - just without the pointer - and the operator is warned via <see cref="StatusText"/>.
    /// </para></summary>
    private async Task SpawnSessionForTaskAsync(TaskNodeViewModel? task)
    {
        if (_host is null)
        {
            ShowError?.Invoke("Host is not initialised - settings load failed at startup.");
            return;
        }

        string? cwd;
        int? taskId = null;
        if (task is { IsSynthetic: false } realTask)
        {
            cwd = realTask.HomeCwd;
            taskId = realTask.TaskId;
        }
        else
        {
            cwd = FolderPicker is null ? null : await FolderPicker().ConfigureAwait(true);
        }

        if (string.IsNullOrWhiteSpace(cwd))
        {
            StatusText = "New session cancelled.";
            return;
        }

        // Build the task-pointer first message when spawning under a real task. Failure here
        // (subsystem missing, task row gone, home folder wiped) is non-fatal: log + warn, then
        // fall through to spawning without a pointer. The operator will see the warning and can
        // type context manually.
        string? firstUserMessage = null;
        IReadOnlyList<string>? drift = null;
        if (taskId is { } tid)
        {
            if (_host.TaskService is { } svc)
            {
                try
                {
                    var plan = await svc.PrepareSwitchAsync(tid).ConfigureAwait(true);
                    if (plan is null)
                    {
                        StatusText = $"Task {tid:D4} not found - spawning without task pointer.";
                    }
                    else
                    {
                        firstUserMessage = plan.FirstUserMessage;
                        drift = plan.Drift;
                    }
                }
                catch (Exception ex)
                {
                    StatusText = $"Task pointer load failed: {ex.Message} - spawning without pointer.";
                }
            }
            else
            {
                StatusText = "Task subsystem unavailable - spawning without pointer.";
            }
        }

        try
        {
            var spawning = taskId is { } stid ? $"Spawning under task {stid:D4} in {cwd}..." : $"Spawning claude in {cwd}...";
            // Only overwrite a "spawning without pointer" warning when we *do* have a pointer;
            // otherwise the warning stays visible long enough for the operator to notice it.
            if (firstUserMessage is not null || taskId is null) StatusText = spawning;
            var ctx = await _host.CreateSessionAsync(cwd, taskId, firstUserMessage).ConfigureAwait(true);
            var row = new SessionRowViewModel(ctx, _host.Logger);
            var parent = ResolveParentNode(ctx.ActiveTaskId);
            parent.Sessions.Add(row);
            parent.IsExpanded = true;
            FocusedSession = row;
            SelectedNode = row;
            row.IsFocused = true;
            var driftSuffix = drift is { Count: > 0 } d ? $" drift: {string.Join(", ", d)} (agent will reconcile)." : "";
            var warmingSuffix = firstUserMessage is not null ? " Warming up..." : "";
            StatusText = $"Spawned {row.ShortId} in {row.Cwd}.{warmingSuffix}{driftSuffix}";
        }
        catch (Exception ex)
        {
            StatusText = $"Spawn failed: {ex.Message}";
            ShowError?.Invoke($"Failed to spawn session:\n\n{ex}");
        }
    }

    private bool CanSendPrompt() =>
        _host is not null
        && FocusedSession is { IsReady: true }
        && !string.IsNullOrWhiteSpace(PromptText);
    private bool CanInterrupt() => FocusedSession is not null;

    private async Task SendPromptAsync()
    {
        var target = FocusedSession;
        var text = PromptText;
        if (target is null || string.IsNullOrWhiteSpace(text)) return;
        PromptText = "";
        await SendToSessionAsync(target, text).ConfigureAwait(true);
    }

    /// <summary>Fire-and-forget canned prompt to <paramref name="session"/>. Bound to the per-row
    /// play / question action buttons; does NOT use <see cref="FocusedSession"/> so the operator
    /// can ping a non-focused session without losing their current focus. The row buttons are
    /// also disabled at the XAML layer via <c>IsEnabled="{Binding IsReady}"</c>; the guard here
    /// is defence-in-depth for programmatic invocation (future keybindings, scripting).</summary>
    private Task SendCannedAsync(SessionRowViewModel? session, string text)
    {
        if (session is null) return Task.CompletedTask;
        if (!session.IsReady)
        {
            StatusText = $"{session.ShortId} is still warming up - try again in a moment.";
            return Task.CompletedTask;
        }
        return SendToSessionAsync(session, text);
    }

    private async Task SendToSessionAsync(SessionRowViewModel target, string text)
    {
        target.BeginUserTurn(text);
        target.Context.Transcript.Append($"\n> {text}\n");
        try
        {
            await target.Context.Session.SendUserMessageAsync(text).ConfigureAwait(true);
            StatusText = $"Sent to {target.ShortId}.";
        }
        catch (Exception ex)
        {
            StatusText = $"Send failed: {ex.Message}";
            target.Context.Transcript.Append($"\n[send failed: {ex.Message}]\n");
        }
    }

    private async Task InterruptAsync()
    {
        var target = FocusedSession;
        if (target is null) return;
        try
        {
            await target.Context.Session.InterruptAsync().ConfigureAwait(true);
            StatusText = $"Interrupt sent to {target.ShortId}.";
        }
        catch (Exception ex)
        {
            StatusText = $"Interrupt failed: {ex.Message}";
        }
    }

    partial void OnPromptTextChanged(string value) => SendPromptCommand.NotifyCanExecuteChanged();

    private void OnSessionClosed(Guid id)
    {
        void Apply()
        {
            foreach (var node in TaskNodes)
            {
                for (var i = node.Sessions.Count - 1; i >= 0; i--)
                {
                    if (node.Sessions[i].Id == id)
                    {
                        node.Sessions[i].Dispose();
                        node.Sessions.RemoveAt(i);
                    }
                }
            }
            if (FocusedSession?.Id == id)
                FocusedSession = TaskNodes.SelectMany(n => n.Sessions).FirstOrDefault();
            // Remove the free-sessions bucket when it empties.
            if (_unattached.Sessions.Count == 0 && TaskNodes.Contains(_unattached))
                TaskNodes.Remove(_unattached);
        }

        if (Dispatcher.UIThread.CheckAccess()) Apply();
        else Dispatcher.UIThread.Post(Apply);
    }

    partial void OnFocusedSessionChanged(SessionRowViewModel? oldValue, SessionRowViewModel? newValue)
    {
        if (oldValue is not null)
        {
            oldValue.IsFocused = false;
            oldValue.PropertyChanged -= OnFocusedSessionPropertyChanged;
        }
        if (newValue is not null)
        {
            newValue.IsFocused = true;
            newValue.PropertyChanged += OnFocusedSessionPropertyChanged;
        }
        SendPromptCommand.NotifyCanExecuteChanged();
        InterruptCommand.NotifyCanExecuteChanged();
    }

    /// <summary>Refresh <see cref="SendPromptCommand"/>'s CanExecute when the focused session
    /// flips <c>IsReady</c>. Without this, the Send button stays disabled even after the session
    /// finishes warming up until the operator types another character to retrigger the prompt-
    /// text path. <c>IsReady</c> updates on the reader thread but the property-changed event is
    /// already marshalled to the UI thread by <see cref="ObservableObject"/>.</summary>
    private void OnFocusedSessionPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SessionRowViewModel.IsReady))
        {
            SendPromptCommand.NotifyCanExecuteChanged();
            if (sender is SessionRowViewModel row)
                _host?.Logger.Information(
                    "gui-main: focused-session IsReady changed row={Row} isReady={IsReady} canSend={CanSend}",
                    row.ShortId, row.IsReady, CanSendPrompt());
        }
    }

    public async ValueTask DisposeAsync()
    {
        // Save layout before the host disposes (the logger lives on the host).
        await SaveLayoutAsync().ConfigureAwait(false);
        foreach (var node in TaskNodes)
            foreach (var s in node.Sessions) s.Dispose();
        TaskNodes.Clear();
        if (_host is not null) await _host.DisposeAsync().ConfigureAwait(false);
    }
}
