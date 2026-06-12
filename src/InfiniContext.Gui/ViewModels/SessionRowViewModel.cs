using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using InfiniContext.Core.Events;
using InfiniContext.Core.Mcp;
using InfiniContext.Core.Sessions;
using InfiniContext.Core.State;
using InfiniContext.Gui.Services;
using Serilog;

namespace InfiniContext.Gui.ViewModels;

/// <summary>
/// Row in the Sessions tree (child of a <see cref="TaskNodeViewModel"/>). Wraps a
/// <see cref="SessionContext"/> and exposes (a) row metadata, and (b) the structured per-turn
/// transcript that drives the central column.
///
/// Subscribes to <see cref="SessionContext.EventObserved"/> on construction and groups events
/// into <see cref="TurnNodeViewModel"/>s.
/// </summary>
public sealed partial class SessionRowViewModel : ObservableObject, IDisposable
{
    private static readonly string Separator = " " + char.ConvertFromUtf32(0x00B7) + " ";  // MIDDLE DOT
    private static readonly string Ellipsis = char.ConvertFromUtf32(0x2026);                // HORIZONTAL ELLIPSIS
    private static readonly string EmDash = char.ConvertFromUtf32(0x2014);                  // EM DASH

    public SessionContext Context { get; }
    public Guid Id => Context.Id;
    public string ShortId => Context.Id.ToString("N").Substring(0, 8);
    public string Cwd => Context.Cwd ?? "<no cwd>";
    /// <summary>The effective model this session is running, for the inspector. Fixed for the
    /// session's life (claude's <c>--model</c> can't change in-flight).</summary>
    public string ModelDisplay => Context.Model ?? "(host default)";
    public TranscriptViewModel Transcript { get; }

    /// <summary>Per-turn structured view of the conversation.</summary>
    public ObservableCollection<TurnNodeViewModel> Turns { get; } = new();

    [ObservableProperty]
    private bool _isFocused;

    /// <summary>
    /// False while the session is still bringing itself up; true once communication is safe.
    ///
    /// <para><b>Free sessions</b> flip to true at construction time. Claude (<c>--print
    /// --input-format stream-json</c>) defers <c>system/init</c> until it has read user input
    /// from stdin, so gating on init would deadlock: Send is disabled, the operator can't type,
    /// the spawn hangs silently until claude self-exits ~55s later. There is no pointer turn to
    /// interleave with, so operator input is safe from the moment the session exists.</para>
    ///
    /// <para><b>Task-bound sessions</b> flip to true on <c>system/init</c> (or, if that event
    /// raced ahead of subscription, at ctor time via <see cref="SessionContext.Session"/>'s
    /// non-null <c>SessionId</c>). The earlier result-based gate kept Send disabled until the
    /// pointer turn closed, which for real tasks (read index.md + description.md + progress.md
    /// + initial discovery) regularly ran 30s-2min and left the operator with no agency on a
    /// session the GUI insisted was "warming up." The new contract: Send is enabled the moment
    /// claude is listening; if the operator fires a prompt while a turn is in flight, the UI
    /// surfaces a queue indicator via <see cref="IsAgentTurnInFlight"/> so they know their
    /// message is buffered on stdin and will run after the current turn. Interrupt stays
    /// available throughout (gated on <see cref="IsAlive"/>, not IsReady).</para>
    ///
    /// Bound to button <c>IsEnabled</c> in the row template; the parent VM watches via
    /// <see cref="ObservableObject.PropertyChanged"/> to refresh <c>SendPromptCommand.CanExecute</c>
    /// when the focused session warms up.
    /// </summary>
    [ObservableProperty]
    private bool _isReady;

    /// <summary>True once <c>system/init</c> (or any post-init event via the catch-up branch)
    /// has been observed. Tracks "has claude acknowledged the spawn yet?" for diagnostic logging
    /// and the ctor-pre-seed race protection; readiness gating itself now derives from this
    /// flag directly rather than waiting for a downstream <see cref="ResultEvent"/>.</summary>
    private bool _initSeen;

    [ObservableProperty]
    private string _statusGlyph = ViewModels.StatusGlyph.Idle;

    /// <summary>Current pending approval (slot snapshot), or null when the slot is empty. Drives
    /// the focused-pane approval banner in the central column.</summary>
    [ObservableProperty]
    private PendingApproval? _pendingApproval;

    public bool IsAwaitingApproval => PendingApproval is not null;
    public string PendingToolName => PendingApproval?.Request.ToolName ?? "";
    public string PendingInputSummary
    {
        get
        {
            var json = PendingApproval?.Request.Input.ToJsonString();
            if (string.IsNullOrEmpty(json)) return "";
            return json.Length <= 400 ? json : json[..400] + Ellipsis;
        }
    }
    /// <summary>Seconds remaining until the slot's server-side timeout. Re-evaluated each tick
    /// of <see cref="_deadlineTimer"/> while a banner is open; the timer is started/stopped by
    /// <see cref="OnPendingApprovalChanged"/> so idle rows pay no per-second cost.</summary>
    public string PendingDeadlineText
    {
        get
        {
            if (PendingApproval is not { } p) return "";
            var seconds = (int)Math.Max(0, (p.DeadlineUtc - DateTimeOffset.UtcNow).TotalSeconds);
            return seconds > 0 ? $"auto-denies in ~{seconds}s" : "deadline reached";
        }
    }

    public IRelayCommand ApproveCommand { get; }
    public IRelayCommand DenyCommand { get; }

    private readonly Action<StateTransition> _onTransition;
    private readonly Action<ClaudeEvent> _onEvent;
    private readonly Action<PendingApproval?> _onApprovalChanged;
    private readonly ILogger? _logger;
    private readonly IUiDispatcher _dispatcher;
    private readonly IUiTimer _deadlineTimer;

    /// <summary>The turn whose <c>ResultEvent</c> we are currently awaiting. Assistant and
    /// user (tool-result) events attach to this turn's tree. Promoted from
    /// <see cref="_queuedTurns"/> when the preceding turn's <c>ResultEvent</c> arrives, or
    /// null between turns when nothing is buffered.</summary>
    private TurnNodeViewModel? _inFlightTurn;

    /// <summary>Operator turns buffered behind an in-flight turn. Claude processes stdin in
    /// FIFO order, so each new operator send while <see cref="_inFlightTurn"/> is non-null
    /// goes here, marked <see cref="TurnNodeViewModel.IsQueued"/>, and is dequeued (and
    /// un-marked) when the next <c>ResultEvent</c> closes the prior in-flight turn.</summary>
    private readonly Queue<TurnNodeViewModel> _queuedTurns = new();

    /// <summary>Glue buffer (task 0012 finding #7 / docs/todo.md #9). Each <see cref="SendOrQueueAsync"/>
    /// call made while a turn is in flight appends here and updates <see cref="_pendingBundleTurn"/>.
    /// On the in-flight turn's <c>ResultEvent</c> the joined contents are flushed as ONE stdin write
    /// and the bundle card is promoted to in-flight. Lives in parallel to <see cref="_queuedTurns"/>:
    /// FIFO queue carries cards already written to stdin (legacy path used by <see cref="BeginUserTurn"/>
    /// for the seed-pointer + tests), bundle queue carries prompts NOT yet written. Process exit
    /// flushes both.</summary>
    private readonly List<string> _pendingPrompts = new();
    private TurnNodeViewModel? _pendingBundleTurn;

    /// <summary>Separator joining buffered prompts into one combined stdin write. Reads as a clear
    /// visual break for both claude (markdown sees three blank lines) and the operator scanning
    /// the transcript.</summary>
    private const string GlueSeparator = "\n\n---\n\n";

    [ObservableProperty]
    private int _queuedPromptCount;

    /// <summary>True while at least one prompt is in the glue buffer. Bound by the row template to
    /// show the small queued-count badge between StatusGlyph and ShortId.</summary>
    public bool HasQueuedPrompts => QueuedPromptCount > 0;

    partial void OnQueuedPromptCountChanged(int value) => OnPropertyChanged(nameof(HasQueuedPrompts));

    private bool _disposed;

    public SessionRowViewModel(
        SessionContext context,
        IUiDispatcher dispatcher,
        IUiTimerFactory timerFactory,
        ILogger? logger = null)
    {
        Context = context ?? throw new ArgumentNullException(nameof(context));
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        if (timerFactory is null) throw new ArgumentNullException(nameof(timerFactory));
        Transcript = new TranscriptViewModel(context.Transcript, dispatcher);
        _logger = logger;
        _onTransition = OnStateTransition;
        _onEvent = OnEvent;
        _onApprovalChanged = OnApprovalChanged;
        ApproveCommand = new RelayCommand(Approve, () => IsAwaitingApproval);
        DenyCommand = new RelayCommand(Deny, () => IsAwaitingApproval);
        _deadlineTimer = timerFactory.Create(TimeSpan.FromSeconds(1));
        _deadlineTimer.Tick += OnDeadlineTick;
        context.Session.State.Transitioned += _onTransition;
        context.EventObserved += _onEvent;
        context.ApprovalSlot.Changed += _onApprovalChanged;
        PendingApproval = context.ApprovalSlot.Current;
        StatusGlyph = ViewModels.StatusGlyph.ForSession(context.Session.State.State, IsAwaitingApproval);

        // Race protection (companion to the catch-up in ApplyEvent): if events have already
        // flowed before we got to subscribe, Session.SessionId is non-null (it's assigned the
        // moment claude's first event carries a session_id - see ClaudeSession.StdoutLoopAsync).
        // A non-null sessionId at ctor time proves system/init has fired, which is the gate
        // for both free and task-bound sessions under the new contract.
        var sessionIdAtCtor = context.Session.SessionId;
        if (sessionIdAtCtor is not null)
        {
            _initSeen = true;
            SetIsReady("ctor-pre-seed");
        }
        else if (context.ActiveTaskId is null)
        {
            // Free-session deadlock break: claude --print --input-format stream-json does not
            // emit system/init until it has read a user message from stdin. Free sessions have
            // no pointer, so the only thing that can kick claude awake is the operator's first
            // typed prompt. Flip IsReady immediately so Send is enabled and the operator can
            // type. _initSeen stays false here; the catch-up in ApplyEvent or the
            // SystemInitEvent arm will flip it when init eventually arrives.
            SetIsReady("ctor-free-deferred-init");
        }
        _logger?.Information(
            "gui-row[{Row}] ctor: activeTaskId={TaskId} sessionId={SessionId} initSeen={InitSeen} isReady={IsReady}",
            ShortId, Context.ActiveTaskId, sessionIdAtCtor, _initSeen, IsReady);
    }

    private void SetIsReady(string reason)
    {
        if (IsReady) return;
        IsReady = true;
        _logger?.Information("gui-row[{Row}] IsReady=true reason={Reason}", ShortId, reason);
    }

    partial void OnIsReadyChanged(bool value)
    {
        // No derived property to refresh: IsAgentTurnInFlight tracks the state machine, not
        // IsReady. Kept as a hook for symmetry with OnPendingApprovalChanged - future derived
        // properties wire in here.
    }

    private void OnApprovalChanged(PendingApproval? pending)
    {
        if (_dispatcher.CheckAccess()) PendingApproval = pending;
        else _dispatcher.Post(() => PendingApproval = pending);
    }

    partial void OnPendingApprovalChanged(PendingApproval? value)
    {
        OnPropertyChanged(nameof(IsAwaitingApproval));
        OnPropertyChanged(nameof(PendingToolName));
        OnPropertyChanged(nameof(PendingInputSummary));
        OnPropertyChanged(nameof(PendingDeadlineText));
        ApproveCommand.NotifyCanExecuteChanged();
        DenyCommand.NotifyCanExecuteChanged();
        StatusGlyph = ViewModels.StatusGlyph.ForSession(Context.Session.State.State, value is not null);
        if (value is not null) _deadlineTimer.Start();
        else _deadlineTimer.Stop();
    }

    private void OnDeadlineTick(object? sender, EventArgs e)
    {
        if (PendingApproval is null)
        {
            _deadlineTimer.Stop();
            return;
        }
        OnPropertyChanged(nameof(PendingDeadlineText));
    }

    private void Approve()
    {
        if (PendingApproval is null) return;
        Context.ApprovalSlot.Resolve(ApprovalBehavior.Allow);
    }

    private void Deny()
    {
        if (PendingApproval is null) return;
        Context.ApprovalSlot.Resolve(ApprovalBehavior.Deny, message: "user denied via GUI banner");
    }

    /// <summary>Add a new operator turn to the visible transcript. If no turn is currently
    /// in flight, the new turn becomes the in-flight turn (its <c>ResultEvent</c> will close
    /// it). Otherwise the turn is enqueued and marked <see cref="TurnNodeViewModel.IsQueued"/>
    /// until the preceding turn closes - the operator sees their message immediately, but the
    /// agent's response only attaches after claude finishes the earlier turn (matching
    /// claude's strict FIFO stdin draining).
    ///
    /// <para>The previous implementation overwrote a single <c>_pendingTurn</c> field, which
    /// orphaned the in-flight turn whenever the operator sent during streaming -- the
    /// orphaned card stayed at "thinking..." forever and the in-flight turn's <c>ResultEvent</c>
    /// was misattributed to the operator's new turn. Task 0011 root cause RC2/RC3.</para></summary>
    public void BeginUserTurn(string promptText)
    {
        var turn = new TurnNodeViewModel(promptText);
        void Apply()
        {
            Turns.Add(turn);
            if (_inFlightTurn is null)
            {
                _inFlightTurn = turn;
            }
            else
            {
                turn.IsQueued = true;
                _queuedTurns.Enqueue(turn);
            }
        }
        if (_dispatcher.CheckAccess()) Apply();
        else _dispatcher.Post(Apply);
    }

    // --- Host slash-command output (task 0017) ----------------------------------------------
    // Host slash-commands (/task, /issue, /approval, ...) write plain text through a callback.
    // The central pane renders Turns, NOT the raw TranscriptModel fragment stream, so routing
    // that output to Context.Transcript left it invisible: the operator saw only the status-strip
    // "<cmd> done." line and concluded the command "did nothing" (task 0017, reported against
    // /task review). These three methods surface the output as its own card in the Turns surface
    // - the typed command in the Operator block, the accumulated output in an expanded System
    // child (monospace + selectable, matching the Details rendering of every other event). The
    // card is independent of the claude turn machinery: no ResultEvent closes it, so
    // EndHostCommandTurn is what stops its in-progress spinner.
    private TurnNodeViewModel? _hostCommandTurn;
    private TurnContentItemViewModel? _hostCommandContent;

    /// <summary>True while a host slash-command card is open on this row (between
    /// <see cref="BeginHostCommandTurn"/> and <see cref="EndHostCommandTurn"/>). The dispatcher
    /// reads it to reject a second concurrent command on the same session - the card uses single
    /// <c>_hostCommandTurn</c>/<c>_hostCommandContent</c> fields, so overlapping commands would
    /// clobber each other's output (task 0017). Toggled on the UI thread inside Begin/End.</summary>
    public bool HostCommandRunning { get; private set; }

    /// <summary>Open a card for a host slash-command invocation. <paramref name="commandLine"/> is
    /// the full text the operator typed (e.g. "/task review"); it shows in the Operator block and
    /// labels the output child. Feed output with <see cref="AppendHostCommandOutput"/>, then close
    /// with <see cref="EndHostCommandTurn"/>.</summary>
    public void BeginHostCommandTurn(string commandLine)
    {
        void Apply()
        {
            var content = new TurnContentItemViewModel(TurnContentKind.System, commandLine, "")
            {
                IsExpanded = true,
            };
            // Headline blank so the (filtered) "thinking..." placeholder doesn't render an Agent
            // block; IsExpanded true so the Details expander shows the output without a click.
            var turn = new TurnNodeViewModel(commandLine)
            {
                Headline = "",
                IsExpanded = true,
            };
            turn.Children.Add(content);
            Turns.Add(turn);
            _hostCommandTurn = turn;
            _hostCommandContent = content;
            HostCommandRunning = true;
        }
        if (_dispatcher.CheckAccess()) Apply();
        else _dispatcher.Post(Apply);
    }

    /// <summary>Append a fragment of host-command output to the open card (see
    /// <see cref="BeginHostCommandTurn"/>). Fragments already carry their own newlines, so they
    /// are concatenated verbatim. No-op when no card is open.</summary>
    public void AppendHostCommandOutput(string text)
    {
        if (string.IsNullOrEmpty(text)) return;
        void Apply()
        {
            if (_hostCommandContent is { } c) c.Detail += text;
        }
        if (_dispatcher.CheckAccess()) Apply();
        else _dispatcher.Post(Apply);
    }

    /// <summary>Close the open host-command card: stop its in-progress spinner and, if the command
    /// produced no output, leave a "(no output)" marker so the card doesn't read as an empty stub.
    /// Safe to call when no card is open.</summary>
    public void EndHostCommandTurn()
    {
        void Apply()
        {
            if (_hostCommandContent is { } c && string.IsNullOrEmpty(c.Detail))
                c.Detail = "(no output)";
            if (_hostCommandTurn is { } t) t.IsInProgress = false;
            _hostCommandTurn = null;
            _hostCommandContent = null;
            HostCommandRunning = false;
        }
        if (_dispatcher.CheckAccess()) Apply();
        else _dispatcher.Post(Apply);
    }

    /// <summary>Operator-facing send entry point (task 0012 finding #7 / docs/todo.md #9). Decides
    /// whether the typed prompt becomes an immediate stdin write or joins the glue buffer:
    ///
    /// <para><b>Idle path</b> (<see cref="IsAgentTurnInFlight"/> false): render a new card via
    /// <see cref="BeginUserTurn"/>, append the operator marker to the transcript, then write to
    /// claude's stdin via <see cref="IClaudeSession.SendUserMessageAsync"/>.</para>
    ///
    /// <para><b>Glue path</b> (a turn is in flight): append to <see cref="_pendingPrompts"/> and
    /// either create or update a single visible "queued bundle" card whose <c>UserPrompt</c> shows
    /// the joined buffer. Do NOT write to stdin -- the flush is done in
    /// <see cref="CloseInFlightTurnWith"/> when the in-flight turn closes, as ONE combined write.
    /// This is the behaviour the operator asked for: multiple prompts typed while busy collapse
    /// into one claude turn instead of being processed separately.</para>
    ///
    /// <para>Returns <c>true</c> when the prompt was written to stdin in this call; <c>false</c>
    /// when it was buffered (the actual write will happen on the next Idle transition).</para></summary>
    public async Task<bool> SendOrQueueAsync(string text, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(text);

        if (!IsAgentTurnInFlight)
        {
            BeginUserTurn(text);
            Context.Transcript.Append($"\n> {text}\n");
            await Context.Session.SendUserMessageAsync(text, cancellationToken).ConfigureAwait(false);
            return true;
        }

        int countAfter = 0;
        void Apply()
        {
            _pendingPrompts.Add(text);
            countAfter = _pendingPrompts.Count;
            if (_pendingBundleTurn is null)
            {
                _pendingBundleTurn = new TurnNodeViewModel(text) { IsQueued = true };
                Turns.Add(_pendingBundleTurn);
            }
            else
            {
                _pendingBundleTurn.UserPrompt = string.Join(GlueSeparator, _pendingPrompts);
            }
            QueuedPromptCount = countAfter;
        }
        if (_dispatcher.CheckAccess()) Apply();
        else _dispatcher.Post(Apply);

        Context.Transcript.Append($"\n> {text}  [queued: will glue with {countAfter - 1} other(s) on next Idle]\n");
        return false;
    }

    private void OnStateTransition(StateTransition t)
    {
        // Marshal to the UI thread. The state machine fires Transitioned synchronously while
        // holding its lock on whatever thread invoked Apply() - typically the stdout-loop
        // reader. Raising PropertyChanged + assigning the ObservableProperty StatusGlyph from
        // a non-UI thread runs through Avalonia's binding subscribers, which can throw if a
        // bound view is reading on the UI thread. That exception then bubbles back out of
        // _state.Apply and used to kill the stdout loop silently (the "Agent busy never
        // changes after the first assistant event" symptom in task 0008).
        void Apply()
        {
            OnPropertyChanged(nameof(State));
            OnPropertyChanged(nameof(IsAlive));
            OnPropertyChanged(nameof(IsAgentTurnInFlight));
            StatusGlyph = ViewModels.StatusGlyph.ForSession(t.To, Context.ApprovalSlot.Current is not null);
            // Surface terminal state in the per-turn tree so the operator sees a clear marker
            // instead of having to read the (small + grey) state line under the row.
            if (t.To == SessionState.Exited)
            {
                var turn = EnsureInFlightTurn(initialPrompt: "(session ended)");
                turn.Children.Add(new TurnContentItemViewModel(
                    TurnContentKind.System,
                    $"session exited ({t.Reason})",
                    $"transition: {t.From} -> {t.To}\nreason: {t.Reason}"));
                turn.IsInProgress = false;
                // Process exit invalidates any queued turns: claude won't drain them, so flush
                // and mark each as closed (no spinner, no IsQueued badge) so the operator can
                // see what was never sent rather than the queue silently vanishing.
                _inFlightTurn = null;
                while (_queuedTurns.TryDequeue(out var queued))
                {
                    queued.IsQueued = false;
                    queued.IsInProgress = false;
                    queued.Children.Add(new TurnContentItemViewModel(
                        TurnContentKind.System,
                        "(not sent - session exited before turn started)",
                        ""));
                }
                // Glue buffer (task 0012 finding #7 / docs/todo.md #9). Claude can't drain the
                // bundle now; surface the unsent payload as a system child so the operator sees
                // what was buffered rather than having the bundle card vanish silently.
                if (_pendingBundleTurn is { } bundle)
                {
                    bundle.IsQueued = false;
                    bundle.IsInProgress = false;
                    var joined = string.Join(GlueSeparator, _pendingPrompts);
                    bundle.Children.Add(new TurnContentItemViewModel(
                        TurnContentKind.System,
                        $"(not sent - session exited with {_pendingPrompts.Count} prompt(s) buffered)",
                        joined));
                    _pendingPrompts.Clear();
                    _pendingBundleTurn = null;
                    QueuedPromptCount = 0;
                }
            }
        }
        if (_dispatcher.CheckAccess()) Apply();
        else _dispatcher.Post(Apply);
    }

    private void OnEvent(ClaudeEvent ev)
    {
        if (_dispatcher.CheckAccess()) ApplyEvent(ev);
        else _dispatcher.Post(() => ApplyEvent(ev));
    }

    private void ApplyEvent(ClaudeEvent ev)
    {
        _logger?.Debug(
            "gui-row[{Row}] event observed: type={Type} subtype={Subtype} initSeen={InitSeen} isReady={IsReady} activeTaskId={TaskId} lastNumTurns={LastNumTurns}",
            ShortId, ev.Type, ev.Subtype, _initSeen, IsReady, Context.ActiveTaskId, Context.LastNumTurns);

        // Race protection: the per-session reader is started inside SessionManager.CreateAsync
        // BEFORE this row VM has a chance to subscribe to Context.EventObserved, so the very
        // first SystemInitEvent can be dispatched-and-dropped if the reader's background loop
        // beats the GUI's row-VM construction. Without this guard _initSeen would stay false
        // forever and IsReady would never flip on subsequent events - the original "buttons
        // stay disabled forever" symptom. Claude emits system/init before any other event and
        // the events channel preserves FIFO, so observing ANY non-init event proves init has
        // already happened: treat that as proof and catch up.
        if (!_initSeen && ev is not SystemInitEvent)
        {
            _initSeen = true;
            _logger?.Information("gui-row[{Row}] catch-up: _initSeen flipped on first non-init event type={Type}", ShortId, ev.Type);
            // Under the new contract both free and task-bound sessions become ready as soon as
            // claude is listening. If the catch-up arm fires, claude is already past init - so
            // it's safe to flip IsReady regardless of ActiveTaskId.
            SetIsReady("catch-up");
        }

        switch (ev)
        {
            case SystemInitEvent init:
                var initTurn = EnsureInFlightTurn(initialPrompt: "(session start)");
                initTurn.Children.Add(new TurnContentItemViewModel(
                    TurnContentKind.System,
                    $"system/init model={init.Model ?? "?"} cwd={init.Cwd ?? "?"}",
                    $"claude_code_version={init.ClaudeCodeVersion ?? "?"}\npermissionMode={init.PermissionMode ?? "?"}"));
                initTurn.Children.Add(new TurnContentItemViewModel(
                    TurnContentKind.System, "(session ready)", ""));
                // Only close out a synthetic "(session start)" placeholder card. A turn opened
                // by BeginUserTurn for a real seed prompt (task pointer) must stay in flight so
                // claude's response attaches as its children and the ResultEvent eventually
                // closes it. Branching on HasOperatorMessage replaces the prior
                // SyntheticHeadlines check, which incorrectly matched the ctor-default
                // "thinking..." headline of any new turn and prematurely closed real seed-prompt
                // turns - the "stuck thinking..." card under task-bound sessions (task 0011).
                if (!initTurn.HasOperatorMessage)
                {
                    initTurn.Headline = "";
                    initTurn.IsInProgress = false;
                    _inFlightTurn = null;
                }
                _initSeen = true;
                _logger?.Information("gui-row[{Row}] init: observed system/init activeTaskId={TaskId}", ShortId, Context.ActiveTaskId);
                SetIsReady("init");
                break;

            case AssistantEvent asst:
                var turn = EnsureInFlightTurn(initialPrompt: "(claude-initiated)");
                AppendAssistantContent(turn, asst);
                break;

            case UserEvent userEv:
                if (_inFlightTurn is { } current) AttachToolResults(current, userEv);
                break;

            case ResultEvent res:
                CloseInFlightTurnWith(res.Result, res);
                _logger?.Information(
                    "gui-row[{Row}] result observed: numTurns={NumTurns} costUsd={Cost} initSeen={InitSeen} isReady={IsReady} queued={Queued}",
                    ShortId, res.NumTurns, res.TotalCostUsd, _initSeen, IsReady, _queuedTurns.Count);
                // Defensive: if init was somehow missed (e.g. a parser anomaly that swallowed
                // it) and only the catch-up branch didn't fire either, a ResultEvent still
                // proves claude is responsive - flip IsReady as a last-resort gate.
                if (!IsReady) SetIsReady("result-fallback");
                break;

            case SystemCompactEvent:
            case SystemTaskStartedEvent:
            case SystemTaskProgressEvent:
            case SystemTaskNotificationEvent:
            case SystemStatusEvent:
            case SystemHookResponseEvent:
            case SystemPluginInstallEvent:
                _inFlightTurn?.Children.Add(new TurnContentItemViewModel(
                    TurnContentKind.System,
                    $"{ev.Type}/{ev.Subtype ?? "?"}",
                    ev.Raw.ToJsonString()));
                break;

            case UnknownEvent unk:
                _inFlightTurn?.Children.Add(new TurnContentItemViewModel(
                    TurnContentKind.UnknownEvent,
                    $"unknown_event: {unk.Discriminator}",
                    unk.Raw.ToJsonString()));
                break;
        }
    }

    /// <summary>Return the current in-flight turn, creating a synthetic-prompt one if there
    /// is none. The synthetic prompt is used by callers that need a target for orphan events
    /// (system/init before any send, claude-initiated assistant emissions, terminal-state
    /// markers). Real operator turns always come in via <see cref="BeginUserTurn"/> and so
    /// never trigger the synthetic path.</summary>
    private TurnNodeViewModel EnsureInFlightTurn(string initialPrompt)
    {
        if (_inFlightTurn is { } existing) return existing;
        var turn = new TurnNodeViewModel(initialPrompt);
        Turns.Add(turn);
        _inFlightTurn = turn;
        return turn;
    }

    private static void AppendAssistantContent(TurnNodeViewModel turn, AssistantEvent asst)
    {
        foreach (var block in asst.Content)
        {
            switch (block)
            {
                case TextBlock text:
                    if (!string.IsNullOrEmpty(turn.Headline) && !TurnNodeViewModel.SyntheticHeadlines.Contains(turn.Headline))
                    {
                        turn.Children.Add(new TurnContentItemViewModel(
                            TurnContentKind.System,
                            "(earlier assistant text)",
                            turn.Headline));
                    }
                    turn.Headline = text.Text;
                    break;

                case ThinkingBlock thinking:
                    turn.Children.Add(new TurnContentItemViewModel(
                        TurnContentKind.Thinking,
                        Truncate(thinking.Thinking, 80),
                        thinking.Thinking));
                    break;

                case ToolUseBlock tu:
                    turn.Children.Add(new TurnContentItemViewModel(
                        TurnContentKind.ToolUse,
                        $"{tu.Name}({SummarizeInput(tu.Input?.ToJsonString())})",
                        tu.Input?.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }) ?? "(no input)",
                        toolUseId: tu.Id));
                    break;

                case UnknownBlock ub:
                    turn.Children.Add(new TurnContentItemViewModel(
                        TurnContentKind.UnknownBlock,
                        $"unknown_block: {ub.Type}",
                        ub.Raw.ToJsonString()));
                    break;
            }
        }
    }

    private static void AttachToolResults(TurnNodeViewModel turn, UserEvent userEv)
    {
        foreach (var block in userEv.Content)
        {
            if (block is ToolResultBlock tr)
            {
                var match = turn.Children.FirstOrDefault(c => c.Kind == TurnContentKind.ToolUse && c.ToolUseId == tr.ToolUseId);
                var payload = tr.Content?.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }) ?? "";
                var resultItem = new TurnContentItemViewModel(
                    TurnContentKind.ToolResult,
                    tr.IsError ? "tool result (error)" : "tool result",
                    payload,
                    toolUseId: tr.ToolUseId);
                if (match is not null)
                {
                    var idx = turn.Children.IndexOf(match);
                    turn.Children.Insert(idx + 1, resultItem);
                }
                else
                {
                    turn.Children.Add(resultItem);
                }
            }
        }
    }

    /// <summary>Close the current in-flight turn (final headline, footer summary) and, if any
    /// operator turn was queued behind it, promote that one to in-flight. This is the single
    /// place where turn-boundary attribution happens for <c>ResultEvent</c>: previously the
    /// caller of <c>BeginUserTurn</c> could clobber the in-flight pointer mid-turn, causing
    /// the orphan-and-misattribute bug (task 0011).</summary>
    private void CloseInFlightTurnWith(string? finalText, ResultEvent? result = null)
    {
        if (_inFlightTurn is not { } turn) return;
        if (!string.IsNullOrEmpty(finalText) && turn.Headline == "thinking...") turn.Headline = finalText!;
        turn.IsInProgress = false;
        if (result is not null)
        {
            var parts = new List<string>();
            if (result.NumTurns is int n) parts.Add($"{n} turn(s)");
            if (result.TotalCostUsd is decimal c) parts.Add($"${c:0.0000}");
            if (result.DurationMs is long d) parts.Add($"{d}ms");
            if (parts.Count > 0) turn.FooterSummary = string.Join(Separator, parts);
        }
        if (_queuedTurns.TryDequeue(out var next))
        {
            next.IsQueued = false;
            _inFlightTurn = next;
        }
        else
        {
            _inFlightTurn = null;
        }

        // Glue flush (task 0012 finding #7 / docs/todo.md #9). If the FIFO queue didn't claim the
        // in-flight slot and the bundle buffer is non-empty, promote the bundle and write the
        // joined contents to claude as ONE combined turn. Task.Run avoids deadlocking the UI
        // thread - SendUserMessageAsync awaits stdin writes through the session's _writeLock and
        // we're being called from the dispatched OnEvent path.
        if (_inFlightTurn is null && _pendingBundleTurn is { } bundle)
        {
            var combined = string.Join(GlueSeparator, _pendingPrompts);
            _pendingPrompts.Clear();
            bundle.IsQueued = false;
            _inFlightTurn = bundle;
            _pendingBundleTurn = null;
            QueuedPromptCount = 0;
            _ = Task.Run(async () =>
            {
                try { await Context.Session.SendUserMessageAsync(combined).ConfigureAwait(false); }
                catch (Exception ex) { _logger?.Warning(ex, "gui-row[{Row}] glue-flush SendUserMessageAsync failed", ShortId); }
            });
        }
    }

    private static string SummarizeInput(string? json)
    {
        if (string.IsNullOrEmpty(json)) return "";
        return Truncate(json, 60);
    }

    private static string Truncate(string s, int max) =>
        s.Length <= max ? s : s[..(max - 1)] + Ellipsis;

    public string State => Context.Session.State.State.ToString();
    public int Drift => Context.Session.UnknownEventCount;
    public string CostDisplay => Context.LastCostUsd is { } c ? $"${c:0.0000}" : EmDash;
    /// <summary>True while the underlying claude process is still capable of receiving messages.
    /// Flips false once the state machine reaches <c>Exited</c> - sending to a dead child silently
    /// fails because the write goes to a closed pipe and the catch path only echoes to the flat
    /// transcript (which the GUI doesn't display). Bound into <c>CanSendPrompt</c> on the host VM
    /// via <see cref="OnStateTransition"/>'s PropertyChanged propagation.</summary>
    public bool IsAlive => Context.Session.State.State != SessionState.Exited;

    /// <summary>True whenever the session's state machine is in a turn-in-flight state
    /// (<c>Sending</c>, <c>Streaming</c>, <c>Cancelling</c>, or <c>Compacting</c>). Drives the
    /// "Agent busy - your message will queue behind the in-flight turn" indicator on the host
    /// VM. The Send button stays enabled while this is true; a prompt fired during a turn-in-
    /// flight buffers on claude's stdin and runs once the current turn closes, which is what
    /// the queue-warning indicator surfaces visually.</summary>
    public bool IsAgentTurnInFlight =>
        IsAlive && Context.Session.State.State is SessionState.Sending
            or SessionState.Streaming
            or SessionState.Cancelling
            or SessionState.Compacting;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { Context.Session.State.Transitioned -= _onTransition; } catch { /* best-effort */ }
        try { Context.EventObserved -= _onEvent; } catch { /* best-effort */ }
        try { Context.ApprovalSlot.Changed -= _onApprovalChanged; } catch { /* best-effort */ }
        try { _deadlineTimer.Stop(); _deadlineTimer.Tick -= OnDeadlineTick; } catch { /* best-effort */ }
        Transcript.Dispose();
    }
}
