using System.Collections.ObjectModel;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using InfiniContext.Core.Configuration;
using InfiniContext.Core.Sessions;
using InfiniContext.Core.SlashCommands;
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
    private readonly IUiDispatcher _dispatcher;
    private readonly IUiTimerFactory _timerFactory;
    private readonly TaskNodeViewModel _unattached = new(null);

    /// <summary>The host-handled slash-commands, built from the single registration site shared
    /// with the CLI (task 0015). Typed slash input is dispatched through this in
    /// <see cref="SendToSessionAsync"/> before falling through to claude's stdin.</summary>
    private readonly SlashCommandRegistry _slashCommands = SlashCommandRegistryFactory.Build();

    public ObservableCollection<TaskNodeViewModel> TaskNodes { get; } = new();

    /// <summary>Left-column width as a Grid-bindable <see cref="GridLength"/>. Two-way bound to
    /// <c>ColumnDefinition.Width</c> so dragging the splitter updates this directly. The
    /// double form is persisted via <see cref="GuiSettings"/> on <c>SaveLayoutAsync</c>.</summary>
    [ObservableProperty]
    private GridLength _leftColumnWidth = new(GuiSettings.Defaults.LeftColumnWidth);

    public double LeftColumnMinWidth => GuiSettings.Defaults.LeftColumnMin;
    public double LeftColumnMaxWidth => GuiSettings.Defaults.LeftColumnMax;

    /// <summary>Window outer width in DIPs. Two-way bound to <c>Window.Width</c> so a resize
    /// updates this immediately; on close <see cref="SaveLayoutAsync"/> flushes the value to
    /// <c>gui-settings.json</c>. Initialised from the loaded <see cref="GuiSettings"/> on the
    /// host-attached ctor, defaulting to <c>GuiSettings.Defaults.WindowWidth</c> otherwise.</summary>
    [ObservableProperty]
    private double _windowWidth = GuiSettings.Defaults.WindowWidth;

    /// <summary>Window outer height in DIPs. See <see cref="WindowWidth"/>.</summary>
    [ObservableProperty]
    private double _windowHeight = GuiSettings.Defaults.WindowHeight;

    /// <summary>True if the window is currently maximized. Two-way bound through a code-behind
    /// hook (Avalonia's <c>Window.WindowState</c> is an enum; the VM stores a bool for JSON
    /// portability across Avalonia versions - the code-behind maps to/from <c>WindowState</c>).</summary>
    [ObservableProperty]
    private bool _windowMaximized;

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

    /// <summary>Callback the View provides so the VM can pop the "+ New task" modal without
    /// referencing Avalonia Window types directly. Returns null when the operator cancels.</summary>
    public Func<Task<Views.NewTaskRequest?>>? NewTaskPrompt { get; set; }

    [ObservableProperty]
    private string _promptText = "";

    /// <summary>Host-wide effective approval mode (D12). Two-way bound to the status-strip
    /// ComboBox. Setter routes through <see cref="SessionHost.SwitchApprovalMode"/> to hot-swap
    /// every active session's <see cref="Core.Mcp.SwitchableApprovalSink"/> in lockstep. When the
    /// host falls back (e.g. Auto -&gt; Ask on light-model construct failure), the
    /// <see cref="SessionHost.ApprovalModeChanged"/> event syncs the VM property back to the
    /// effective mode. The <see cref="_suppressApprovalModeSwitch"/> guard prevents the
    /// event-driven sync from re-entering the switch path.</summary>
    [ObservableProperty]
    private ApprovalMode _currentApprovalMode = ApprovalMode.Ask;

    private bool _suppressApprovalModeSwitch;

    /// <summary>Static list backing the status-strip ComboBox. Order matches the
    /// <see cref="ApprovalMode"/> enum (Ask, Deny, Allow, Auto), which is also the order the
    /// CLI's <c>/approval</c> help lists them in.</summary>
    public IReadOnlyList<ApprovalMode> ApprovalModes { get; } = new[]
    {
        ApprovalMode.Ask, ApprovalMode.Deny, ApprovalMode.Allow, ApprovalMode.Auto,
    };

    /// <summary>True while the focused session has a turn in flight (Sending/Streaming/
    /// Cancelling/Compacting). The XAML uses this to surface a "Agent busy - your next
    /// message will queue behind the in-flight turn" indicator in the status strip. Send
    /// stays enabled while this is true; the indicator's purpose is to make stdin queuing
    /// visible rather than letting the operator's prompt disappear silently into the buffer.</summary>
    public bool IsAgentBusy => FocusedSession is { IsAgentTurnInFlight: true };

    /// <summary>Tooltip shown on the Send button. Surfaces queueing behaviour when the agent
    /// is in the middle of a turn; otherwise advertises the keyboard shortcut.</summary>
    public string SendButtonTooltip => IsAgentBusy
        ? "Agent is busy - Send will queue your message behind the in-flight turn. Press Esc to interrupt the current turn."
        : "Send (Ctrl+Enter)";

    /// <summary>Text inside the status-strip "Agent busy" badge. Surfaces the focused session's
    /// glue-buffer count when non-empty so the operator sees both "agent is mid-turn" AND "your
    /// typed prompts are buffered and will be glued on the next Idle." Task 0012 finding #7.</summary>
    public string AgentBusyHint
    {
        get
        {
            if (FocusedSession is { HasQueuedPrompts: true } row)
                return $"Agent busy - {row.QueuedPromptCount} message(s) queued, will glue on Idle";
            return "Agent busy - next message will queue";
        }
    }

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

    /// <summary>Design-time / placeholder ctor used by <c>App.OnFrameworkInitializationCompleted</c>
    /// while <see cref="CreateAsync"/> is still running. Defaults to the Avalonia-backed dispatcher
    /// + timer factory since this path always runs inside a real Avalonia app. Tests should use
    /// the explicit-dispatcher ctor below.</summary>
    public MainWindowViewModel() : this(AvaloniaUiDispatcher.Instance, AvaloniaUiTimerFactory.Instance, new GuiSettings())
    {
    }

    /// <summary>Test-friendly ctor: takes the dispatcher + timer factory explicitly. No
    /// <see cref="SessionHost"/> attached, so spawn-related commands fail gracefully via the
    /// existing null-guards; tests exercising <see cref="FocusedSession"/> can construct a row
    /// VM directly and set it.</summary>
    public MainWindowViewModel(IUiDispatcher dispatcher, IUiTimerFactory timerFactory)
        : this(dispatcher, timerFactory, new GuiSettings())
    {
    }

    private MainWindowViewModel(IUiDispatcher dispatcher, IUiTimerFactory timerFactory, GuiSettings guiSettings)
    {
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _timerFactory = timerFactory ?? throw new ArgumentNullException(nameof(timerFactory));
        _guiSettings = guiSettings;
        _leftColumnWidth = new GridLength(guiSettings.LeftColumnWidth);
        _windowWidth = guiSettings.WindowWidth;
        _windowHeight = guiSettings.WindowHeight;
        _windowMaximized = guiSettings.WindowMaximized;
        NewTaskCommand = new AsyncRelayCommand(NewTaskAsync);
        SpawnSessionForTaskCommand = new AsyncRelayCommand<TaskNodeViewModel?>(SpawnSessionForTaskAsync);
        SendPromptCommand = new AsyncRelayCommand(SendPromptAsync, CanSendPrompt);
        InterruptCommand = new AsyncRelayCommand(InterruptAsync, CanInterrupt);
        SendContinueCommand = new AsyncRelayCommand<SessionRowViewModel?>(s => SendCannedAsync(s, ContinuePromptText));
        SendWhatsNextCommand = new AsyncRelayCommand<SessionRowViewModel?>(s => SendCannedAsync(s, WhatsNextPromptText));
        // _unattached is added lazily on first free-session spawn and removed when emptied -
        // keeps the tree clean for users who only work via task-bound sessions.
    }

    private MainWindowViewModel(SessionHost host, IUiDispatcher dispatcher, IUiTimerFactory timerFactory, GuiSettings guiSettings)
        : this(dispatcher, timerFactory, guiSettings)
    {
        _host = host;
        LogFilePath = host.LogFilePath;
        // Seed the VM property from the host's effective mode without triggering the
        // OnCurrentApprovalModeChanged partial's switch call (the host's state already matches).
        _suppressApprovalModeSwitch = true;
        CurrentApprovalMode = host.CurrentApprovalMode;
        _suppressApprovalModeSwitch = false;
        host.SessionClosed += OnSessionClosed;
        host.ApprovalModeChanged += OnHostApprovalModeChanged;
        host.AutoRotationProposed += OnAutoRotationProposed;
    }

    private void OnHostApprovalModeChanged(ApprovalMode effective)
    {
        void Apply()
        {
            if (CurrentApprovalMode == effective) return;
            _suppressApprovalModeSwitch = true;
            CurrentApprovalMode = effective;
            _suppressApprovalModeSwitch = false;
        }
        if (_dispatcher.CheckAccess()) Apply();
        else _dispatcher.Post(Apply);
    }

    partial void OnCurrentApprovalModeChanged(ApprovalMode value)
    {
        if (_suppressApprovalModeSwitch || _host is null) return;
        try
        {
            var effective = _host.SwitchApprovalMode(value);
            if (effective != value)
            {
                StatusText = $"Approval mode requested {value.ToString().ToLowerInvariant()} but using {effective.ToString().ToLowerInvariant()} (see log for details).";
                _suppressApprovalModeSwitch = true;
                CurrentApprovalMode = effective;
                _suppressApprovalModeSwitch = false;
            }
            else
            {
                StatusText = $"Approval mode: {effective.ToString().ToLowerInvariant()}.";
            }
        }
        catch (Exception ex)
        {
            StatusText = $"Approval mode switch failed: {ex.Message}";
            _host.Logger.Error(ex, "gui-main: SwitchApprovalMode({Mode}) failed", value);
        }
    }

    public static async Task<MainWindowViewModel> CreateAsync(IUiDispatcher dispatcher, IUiTimerFactory timerFactory)
    {
        var host = await SessionHost.StartAsync().ConfigureAwait(false);
        var guiSettings = await GuiSettings.LoadAsync(logger: host.Logger).ConfigureAwait(false);
        var vm = new MainWindowViewModel(host, dispatcher, timerFactory, guiSettings);
        await vm.LoadTasksAsync().ConfigureAwait(false);
        return vm;
    }

    /// <summary>Persist the current layout (column widths, etc.) to <c>~/.infinicontext/gui-settings.json</c>.
    /// Best-effort; failures are logged but don't propagate - losing a width tweak is not worth
    /// crashing the GUI on close.</summary>
    public async Task SaveLayoutAsync()
    {
        _guiSettings.LeftColumnWidth = LeftColumnWidth.IsAbsolute ? LeftColumnWidth.Value : GuiSettings.Defaults.LeftColumnWidth;
        // Window size is only meaningful in Normal state. If the user closed while maximized,
        // MainWindow.OnClosed has already restored WindowWidth/Height from its _lastNormalWidth
        // /_lastNormalHeight snapshots before this method runs, so what we read here is the
        // pre-maximize windowed size - not the screen dimensions the two-way binding pushed in
        // while Maximized. Restoring to Normal on next launch un-maximizes to the right size.
        _guiSettings.WindowWidth = WindowWidth;
        _guiSettings.WindowHeight = WindowHeight;
        _guiSettings.WindowMaximized = WindowMaximized;
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

        if (_dispatcher.CheckAccess()) Apply();
        else _dispatcher.Post(Apply);
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

    /// <summary>"+ New task" top-bar button. Opens the <see cref="Views.NewTaskDialog"/>
    /// (provided by the View via <see cref="NewTaskPrompt"/>) to capture a home folder + free-form
    /// description, then calls <see cref="Core.Tasks.TaskService.CreateAsync"/> which generates the
    /// title via the light model, scaffolds the folder, and writes the row. Refreshes the
    /// <see cref="TaskNodes"/> on success so the new task appears at the top of the tree.</summary>
    private async Task NewTaskAsync()
    {
        if (_host is null)
        {
            ShowError?.Invoke("Host is not initialised - settings load failed at startup.");
            return;
        }
        if (_host.TaskService is not { } svc)
        {
            ShowError?.Invoke("Task subsystem unavailable (DB ahead of code / locked file). See log for details.");
            return;
        }
        if (NewTaskPrompt is null)
        {
            ShowError?.Invoke("New-task dialog is not wired to the view (internal error).");
            return;
        }

        Views.NewTaskRequest? req;
        try
        {
            req = await NewTaskPrompt().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            StatusText = $"New task dialog failed: {ex.Message}";
            return;
        }
        if (req is null)
        {
            StatusText = "New task cancelled.";
            return;
        }

        StatusText = $"Creating task in {req.HomeCwd}...";
        try
        {
            var record = await svc.CreateAsync(req.HomeCwd, req.Description).ConfigureAwait(true);
            void Apply()
            {
                // Prepend so the new task appears at the top of the tree, matching LoadTasksAsync's
                // "newest first" ordering.
                TaskNodes.Insert(0, new TaskNodeViewModel(record));
            }
            if (_dispatcher.CheckAccess()) Apply();
            else _dispatcher.Post(Apply);
            StatusText = $"Created task {record.Id:D4}: {record.Title}";
        }
        catch (Exception ex)
        {
            StatusText = $"Create failed: {ex.Message}";
            ShowError?.Invoke($"Failed to create task:\n\n{ex}");
        }
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

        SessionContext ctx;
        try
        {
            var spawning = taskId is { } stid ? $"Spawning under task {stid:D4} in {cwd}..." : $"Spawning claude in {cwd}...";
            // Only overwrite a "spawning without pointer" warning when we *do* have a pointer;
            // otherwise the warning stays visible long enough for the operator to notice it.
            if (firstUserMessage is not null || taskId is null) StatusText = spawning;
            ctx = await _host.CreateSessionAsync(cwd, taskId, firstUserMessage).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _host.Logger.Error(ex, "gui-main: spawn failed cwd={Cwd} task={TaskId}", cwd, taskId);
            StatusText = $"Spawn failed: {ex.Message}";
            ShowError?.Invoke($"Failed to spawn session: {ex.Message}\n\n{ex}");
            return;
        }

        // Attach is UI work over an already-running session. Keep its failures out of the spawn
        // catch: in the task 0016 repro, a rendering exception thrown while flipping focus was
        // reported as "Failed to spawn session:" even though the claude child was alive and
        // streaming - the operator had no way to tell the session existed.
        try
        {
            var row = AttachRowForContext(ctx, firstUserMessage);
            var driftSuffix = drift is { Count: > 0 } d ? $" drift: {string.Join(", ", d)} (agent will reconcile)." : "";
            var warmingSuffix = firstUserMessage is not null ? " Warming up..." : "";
            StatusText = $"Spawned {row.ShortId} in {row.Cwd}.{warmingSuffix}{driftSuffix}";
        }
        catch (Exception ex)
        {
            _host.Logger.Error(ex, "gui-main: session {SessionId} spawned but UI attach failed", ctx.Id);
            StatusText = $"Session spawned ({ctx.Id.ToString("N")[..8]}) but the UI failed to attach it: {ex.Message}";
            ShowError?.Invoke($"Session spawned but the UI failed to attach it: {ex.Message}\n\n{ex}");
        }
    }

    /// <summary>Build the row VM for a freshly-spawned context, hang it under its task node,
    /// focus it, and render the optional seed prompt. Shared between manual spawns
    /// (<see cref="SpawnSessionForTaskAsync"/>) and the Core auto-rotation path
    /// (<see cref="OnAutoRotationProposed"/>) so the two routes stay observably identical.</summary>
    private SessionRowViewModel AttachRowForContext(SessionContext ctx, string? firstUserMessage)
    {
        var row = new SessionRowViewModel(ctx, _dispatcher, _timerFactory, _host!.Logger);
        var parent = ResolveParentNode(ctx.ActiveTaskId);
        parent.Sessions.Add(row);
        parent.IsExpanded = true;
        // Route through SelectedNode first so the TreeView highlight and the derived pointers
        // (FocusedSession + SelectedTaskNode, via OnSelectedNodeChanged) move together; the
        // explicit FocusedSession assignment is a no-op then, but keeps headless tests (no
        // TreeView binding) on the same path.
        SelectedNode = row;
        FocusedSession = row;
        // Render the seed (task pointer) prompt into the transcript and the per-turn tree so
        // the operator can SEE what was just sent to claude. Without this, the message area
        // shows only the (session ready) marker from system/init - leaving the operator
        // wondering what the agent is "busy" processing. The send itself happens inside
        // ClaudeSession.StartAsync as a host-driven stdin write; that path never produces a
        // UserEvent in the stream, so this is the only place we can surface it.
        if (firstUserMessage is not null)
        {
            row.BeginUserTurn(firstUserMessage);
            ctx.Transcript.Append($"\n> {firstUserMessage}  [seed prompt]\n");
        }
        return row;
    }

    private bool CanSendPrompt() =>
        FocusedSession is { IsReady: true, IsAlive: true }
        && !string.IsNullOrWhiteSpace(PromptText);
    private bool CanInterrupt() => FocusedSession is { IsAlive: true };

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
        // Host-handled slash-commands are intercepted here and routed through the shared registry
        // (task 0015 slice E) instead of being sent to claude's stdin. Unknown slash-commands are
        // forwarded to claude (it owns /clear, /compact, etc.), matching the CLI's D6 policy.
        if (text.StartsWith('/') && await TryHandleSlashCommandAsync(target, text).ConfigureAwait(true))
            return;

        // The row VM decides Idle vs glue-buffer (task 0012 finding #7 / docs/todo.md #9):
        // - Idle: writes to claude's stdin immediately. Returns true.
        // - Busy: appends to the row's pending-prompt bundle. Returns false. The actual stdin
        //   write will happen as ONE combined message on the next ResultEvent, via the flush hook
        //   in SessionRowViewModel.CloseInFlightTurnWith.
        // The host VM only does status-text reporting + error surfacing.
        try
        {
            var sentNow = await target.SendOrQueueAsync(text).ConfigureAwait(true);
            StatusText = sentNow
                ? $"Sent to {target.ShortId}."
                : $"Queued to {target.ShortId} ({target.QueuedPromptCount} pending, will glue on next Idle).";
        }
        catch (Exception ex)
        {
            StatusText = $"Send failed: {ex.Message}";
            target.Context.Transcript.Append($"\n[send failed: {ex.Message}]\n");
        }
    }

    /// <summary>
    /// Resolve a typed <c>/</c>-command through the shared <see cref="SlashCommandRegistry"/> and
    /// run it against <paramref name="target"/>. Returns true when the command was recognised and
    /// handled (so the caller must NOT also send the text to claude); false for an unknown command,
    /// which the caller forwards to claude verbatim (D6 - claude owns /clear, /compact, ...).
    /// </summary>
    private async Task<bool> TryHandleSlashCommandAsync(SessionRowViewModel target, string text)
    {
        var space = text.IndexOf(' ');
        var head = space < 0 ? text : text[..space];
        var argLine = space < 0 ? "" : text[(space + 1)..];

        var cmd = _slashCommands.TryResolve(head);
        if (cmd is null) return false; // unknown -> forward to claude

        if (_host is null)
        {
            StatusText = $"{head} is unavailable (host not initialised).";
            return true;
        }

        void Append(string s)
        {
            if (_dispatcher.CheckAccess()) target.Context.Transcript.Append(s);
            else _dispatcher.Post(() => target.Context.Transcript.Append(s));
        }

        var renderer = new GuiSlashCommandRenderer(Append);
        var writer = new TranscriptTextWriter(Append);
        var ctx = BuildSlashContext(target, renderer, writer);
        StatusText = $"Running {head}...";
        try
        {
            await cmd.ExecuteAsync(ctx, argLine, CancellationToken.None).ConfigureAwait(true);
            StatusText = $"{head} done.";
        }
        catch (Exception ex)
        {
            Append($"[{head.TrimStart('/')}] error: {ex.Message}\n");
            StatusText = $"{head} failed: {ex.Message}";
            _host.Logger.Warning(ex, "gui-slash: {Cmd} threw", head);
        }
        return true;
    }

    private SlashCommandContext BuildSlashContext(SessionRowViewModel target, ISlashCommandRenderer renderer, TextWriter writer)
    {
        void SetStatus(string s)
        {
            if (_dispatcher.CheckAccess()) StatusText = s;
            else _dispatcher.Post(() => StatusText = s);
        }

        var actions = new GuiReplActions(
            requestExit: () => SetStatus("/quit: close the window to exit the GUI."),
            requestResume: sid => SetStatus($"/resume is not supported in the GUI (requested {sid})."),
            requestSwitch: plan => SetStatus($"/task switch: spawn a session for task {plan.Task.Id:D4} \"{plan.Task.Title}\" from the tree."),
            requestFocus: id => SetStatus($"/focus: select session {id:D} in the tree to focus it."));

        return new SlashCommandContext(
            Session: target.Context.Session,
            Renderer: renderer,
            Settings: _host!.Settings,
            EffectiveSettingsJson: null,
            GetApprovalMode: () => _host.CurrentApprovalMode,
            SwitchApprovalMode: m => _host.SwitchApprovalMode(m),
            TaskHandler: _host.CreateTaskCommandHandler(writer),
            PrepareTaskSwitch: _host.TaskService is null
                ? null
                : (id, ct) => _host.TaskService.PrepareSwitchAsync(id, ct),
            GetLastCostUsd: () => target.Context.LastCostUsd,
            GetLastNumTurns: () => target.Context.LastNumTurns,
            Actions: actions,
            Registry: _slashCommands)
        {
            SessionManager = _host.SessionManager,
            FocusedHostSessionId = target.Context.Id,
            ActiveTaskId = target.Context.ActiveTaskId,
            IssueHandler = _host.CreateIssueCommandHandler(writer),
            TryEnterTask = null,
        };
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

        if (_dispatcher.CheckAccess()) Apply();
        else _dispatcher.Post(Apply);
    }

    /// <summary>Handler for <see cref="SessionHost.AutoRotationProposed"/>. Runs on the source
    /// session's state-machine thread; marshals to the UI thread before touching the tree.
    /// Unlike the CLI adapter, the GUI does NOT filter on focus -- every row is visible in the
    /// left tree, so a non-focused session crossing the threshold should still spawn its
    /// successor (the operator may be reading another pane and gets a status-text update they
    /// can act on). On Refused/Failed/plan-missing, the VM only updates StatusText -- there is
    /// no GUI analogue to the CLI's RequestSwitch (the dead row stays in the tree and the
    /// operator can decide whether to /task switch manually).</summary>
    internal void OnAutoRotationProposed(AutoRotationProposal proposal)
    {
        if (_dispatcher.CheckAccess()) _ = HandleAutoRotationAsync(proposal);
        else _dispatcher.Post(() => _ = HandleAutoRotationAsync(proposal));
    }

    private async Task HandleAutoRotationAsync(AutoRotationProposal proposal)
    {
        switch (proposal.Status)
        {
            case AutoRotationStatus.Refused:
                StatusText = $"Auto-rotation refused: cap of {proposal.RefusedCap} within {proposal.RefusedWindow!.Value.TotalMinutes:F0} min hit on task {proposal.TaskId:D4}. Spawn manually from the tree.";
                _host?.Logger.Warning("gui-auto-rotate: cap hit for task {TaskId}", proposal.TaskId);
                return;

            case AutoRotationStatus.Failed:
                StatusText = $"Auto-rotation failed for task {proposal.TaskId:D4}: {proposal.Failure?.Message}";
                return;

            case AutoRotationStatus.Approved:
                if (proposal.Plan is null)
                {
                    StatusText = $"Auto-rotation: task {proposal.TaskId:D4} not found - spawn manually from the tree.";
                    return;
                }
                await SpawnRotatedSessionAsync(proposal).ConfigureAwait(true);
                return;
        }
    }

    private async Task SpawnRotatedSessionAsync(AutoRotationProposal proposal)
    {
        if (_host is null) return;
        var plan = proposal.Plan!;
        var cwd = proposal.Session.Cwd;
        if (string.IsNullOrWhiteSpace(cwd))
        {
            StatusText = $"Auto-rotation failed: source session for task {proposal.TaskId:D4} has no cwd.";
            return;
        }
        try
        {
            var ctx = await _host.CreateSessionAsync(cwd, plan.Task.Id, plan.FirstUserMessage).ConfigureAwait(true);
            var row = AttachRowForContext(ctx, plan.FirstUserMessage);
            var label = proposal.Reason switch
            {
                RotationReason.Hard => "hard-nudge",
                RotationReason.Soft => "soft-nudge",
                _ => "nudge",
            };
            var driftSuffix = plan.Drift is { Count: > 0 } d ? $" drift: {string.Join(", ", d)} (agent will reconcile)." : "";
            StatusText = $"{label} flush detected - auto-rotated to task {plan.Task.Id:D4} \"{plan.Task.Title}\" ({row.ShortId}).{driftSuffix} Warming up...";
            _host.Logger.Information("gui-auto-rotate: task {TaskId} reason={Reason} new-session={SessionId}", plan.Task.Id, proposal.Reason, ctx.Id);
        }
        catch (Exception ex)
        {
            StatusText = $"Auto-rotation spawn failed for task {proposal.TaskId:D4}: {ex.Message}";
            _host.Logger.Error(ex, "gui-auto-rotate: spawn failed for task {TaskId}", proposal.TaskId);
        }
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
        OnPropertyChanged(nameof(IsAgentBusy));
        OnPropertyChanged(nameof(SendButtonTooltip));
        OnPropertyChanged(nameof(AgentBusyHint));
    }

    /// <summary>Refresh <see cref="SendPromptCommand"/>'s CanExecute when the focused session
    /// flips <c>IsReady</c>. Without this, the Send button stays disabled even after the session
    /// finishes warming up until the operator types another character to retrigger the prompt-
    /// text path. <c>IsReady</c> updates on the reader thread but the property-changed event is
    /// already marshalled to the UI thread by <see cref="ObservableObject"/>.</summary>
    private void OnFocusedSessionPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SessionRowViewModel.IsReady) ||
            e.PropertyName == nameof(SessionRowViewModel.IsAlive) ||
            e.PropertyName == nameof(SessionRowViewModel.State))
        {
            SendPromptCommand.NotifyCanExecuteChanged();
            InterruptCommand.NotifyCanExecuteChanged();
            if (sender is SessionRowViewModel row)
                _host?.Logger.Information(
                    "gui-main: focused-session changed row={Row} prop={Prop} isReady={IsReady} isAlive={IsAlive} canSend={CanSend}",
                    row.ShortId, e.PropertyName, row.IsReady, row.IsAlive, CanSendPrompt());
        }
        if (e.PropertyName == nameof(SessionRowViewModel.IsAgentTurnInFlight))
        {
            OnPropertyChanged(nameof(IsAgentBusy));
            OnPropertyChanged(nameof(SendButtonTooltip));
            OnPropertyChanged(nameof(AgentBusyHint));
        }
        if (e.PropertyName == nameof(SessionRowViewModel.QueuedPromptCount) ||
            e.PropertyName == nameof(SessionRowViewModel.HasQueuedPrompts))
        {
            OnPropertyChanged(nameof(AgentBusyHint));
        }
    }

    public async ValueTask DisposeAsync()
    {
        // Save layout before the host disposes (the logger lives on the host).
        await SaveLayoutAsync().ConfigureAwait(false);
        foreach (var node in TaskNodes)
            foreach (var s in node.Sessions) s.Dispose();
        TaskNodes.Clear();
        if (_host is not null)
        {
            _host.SessionClosed -= OnSessionClosed;
            _host.ApprovalModeChanged -= OnHostApprovalModeChanged;
            _host.AutoRotationProposed -= OnAutoRotationProposed;
            await _host.DisposeAsync().ConfigureAwait(false);
        }
    }
}
