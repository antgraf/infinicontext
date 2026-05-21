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
    /// is null or synthetic (the (free sessions) bucket).</summary>
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

        try
        {
            StatusText = taskId is { } tid ? $"Spawning under task {tid:D4} in {cwd}..." : $"Spawning claude in {cwd}...";
            var ctx = await _host.CreateSessionAsync(cwd, taskId).ConfigureAwait(true);
            var row = new SessionRowViewModel(ctx);
            var parent = ResolveParentNode(ctx.ActiveTaskId);
            parent.Sessions.Add(row);
            parent.IsExpanded = true;
            FocusedSession = row;
            SelectedNode = row;
            row.IsFocused = true;
            StatusText = $"Spawned {row.ShortId} in {row.Cwd}.";
        }
        catch (Exception ex)
        {
            StatusText = $"Spawn failed: {ex.Message}";
            ShowError?.Invoke($"Failed to spawn session:\n\n{ex}");
        }
    }

    private bool CanSendPrompt() => _host is not null && FocusedSession is not null && !string.IsNullOrWhiteSpace(PromptText);
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
    /// can ping a non-focused session without losing their current focus.</summary>
    private Task SendCannedAsync(SessionRowViewModel? session, string text) =>
        session is null ? Task.CompletedTask : SendToSessionAsync(session, text);

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
        if (oldValue is not null) oldValue.IsFocused = false;
        if (newValue is not null) newValue.IsFocused = true;
        SendPromptCommand.NotifyCanExecuteChanged();
        InterruptCommand.NotifyCanExecuteChanged();
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
