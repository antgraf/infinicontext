using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using InfiniContext.Core.Events;
using InfiniContext.Core.Mcp;
using InfiniContext.Core.Sessions;
using InfiniContext.Core.State;
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
    public TranscriptViewModel Transcript { get; }

    /// <summary>Per-turn structured view of the conversation.</summary>
    public ObservableCollection<TurnNodeViewModel> Turns { get; } = new();

    [ObservableProperty]
    private bool _isFocused;

    /// <summary>
    /// False while the session is still bringing itself up; true once communication is safe.
    /// Flips on <c>system/init</c> for both free and task-bound sessions: by that point claude
    /// has read the FirstUserMessage pointer from stdin (per its <c>--print --input-format
    /// stream-json</c> behaviour - it doesn't emit init until it has consumed stdin input),
    /// so the pointer is guaranteed ahead of any operator-typed message in claude's queue.
    /// An older policy waited for the first <c>ResultEvent</c> on task-bound sessions, on the
    /// theory that the pointer turn should close before the operator could interleave - but
    /// that turn can run for minutes (read index.md, read large progress.md, do discovery), and
    /// the operator had no agency to send guidance or canned prompts the entire time. Interrupt
    /// stays available throughout (gated on <see cref="IsAlive"/>, not IsReady) as the escape
    /// hatch for a runaway first turn. Free sessions also flip at ctor time when no event has
    /// fired yet: claude defers init until stdin input, so gating on init would deadlock - the
    /// operator couldn't type because Send is disabled. Bound to button <c>IsEnabled</c> in
    /// the row template; the parent VM watches via <see cref="ObservableObject.PropertyChanged"/>
    /// to refresh <c>SendPromptCommand.CanExecute</c> when the focused session warms up.
    /// </summary>
    [ObservableProperty]
    private bool _isReady;

    /// <summary>True once <c>system/init</c> (or any post-init event via the catch-up branch)
    /// has been observed. Kept as a safety-net flag for the <c>result-after-init</c> path so the
    /// gate is idempotent across all the SetIsReady reasons.</summary>
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
    private readonly DispatcherTimer _deadlineTimer;
    private TurnNodeViewModel? _pendingTurn;
    private bool _disposed;

    public SessionRowViewModel(SessionContext context, ILogger? logger = null)
    {
        Context = context ?? throw new ArgumentNullException(nameof(context));
        Transcript = new TranscriptViewModel(context.Transcript);
        _logger = logger;
        _onTransition = OnStateTransition;
        _onEvent = OnEvent;
        _onApprovalChanged = OnApprovalChanged;
        ApproveCommand = new RelayCommand(Approve, () => IsAwaitingApproval);
        DenyCommand = new RelayCommand(Deny, () => IsAwaitingApproval);
        _deadlineTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1) };
        _deadlineTimer.Tick += OnDeadlineTick;
        context.Session.State.Transitioned += _onTransition;
        context.EventObserved += _onEvent;
        context.ApprovalSlot.Changed += _onApprovalChanged;
        PendingApproval = context.ApprovalSlot.Current;
        StatusGlyph = ViewModels.StatusGlyph.ForSession(context.Session.State.State, IsAwaitingApproval);

        // Race protection (companion to the catch-up in ApplyEvent): if events have already
        // flowed before we got to subscribe, Session.SessionId is non-null (it's assigned the
        // moment claude's first event carries a session_id - see ClaudeSession.StdoutLoopAsync).
        // A non-null sessionId at ctor time therefore proves system/init has fired, which - per
        // claude --print's stdin-first contract - proves the FirstUserMessage pointer has been
        // consumed. Safe to flip IsReady for both free and task-bound sessions.
        var sessionIdAtCtor = context.Session.SessionId;
        var lastNumTurnsAtCtor = context.LastNumTurns;
        if (sessionIdAtCtor is not null)
        {
            _initSeen = true;
            SetIsReady("ctor-pre-seed");
        }
        else if (context.ActiveTaskId is null)
        {
            // Free-session deadlock break: claude --print --input-format stream-json does not
            // emit system/init until it has read a user message from stdin (see the bring-up
            // deadlock fix in ClaudeSession.StartAsync). For task-bound sessions the host writes
            // the FirstUserMessage pointer to satisfy that requirement; free sessions have no
            // pointer, so the only thing that can kick claude awake is the operator's first
            // typed prompt. If we gate IsReady on init, Send stays disabled, the operator
            // can't type, and the spawn hangs silently until claude self-exits ~55s later.
            // Flip IsReady immediately - there is no pointer turn to interleave with, so
            // operator input is safe from the moment the session exists. _initSeen stays false
            // here: the catch-up in ApplyEvent will flip it (or the SystemInitEvent arm will)
            // when init eventually arrives; this branch is only about button enablement.
            SetIsReady("ctor-free-deferred-init");
        }
        _logger?.Information(
            "gui-row[{Row}] ctor: activeTaskId={TaskId} sessionId={SessionId} lastNumTurns={LastNumTurns} initSeen={InitSeen} isReady={IsReady}",
            ShortId, Context.ActiveTaskId, sessionIdAtCtor, lastNumTurnsAtCtor, _initSeen, IsReady);
    }

    private void SetIsReady(string reason)
    {
        if (IsReady) return;
        IsReady = true;
        _logger?.Information("gui-row[{Row}] IsReady=true reason={Reason}", ShortId, reason);
    }

    private void OnApprovalChanged(PendingApproval? pending)
    {
        if (Dispatcher.UIThread.CheckAccess()) PendingApproval = pending;
        else Dispatcher.UIThread.Post(() => PendingApproval = pending);
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

    public void BeginUserTurn(string promptText)
    {
        var turn = new TurnNodeViewModel(promptText);
        if (Dispatcher.UIThread.CheckAccess())
        {
            Turns.Add(turn);
            _pendingTurn = turn;
        }
        else
        {
            Dispatcher.UIThread.Post(() =>
            {
                Turns.Add(turn);
                _pendingTurn = turn;
            });
        }
    }

    private void OnStateTransition(StateTransition t)
    {
        OnPropertyChanged(nameof(State));
        OnPropertyChanged(nameof(IsAlive));
        StatusGlyph = ViewModels.StatusGlyph.ForSession(t.To, Context.ApprovalSlot.Current is not null);
        // Surface terminal state in the per-turn tree so the operator sees a clear marker instead
        // of having to read the (small + grey) state line under the row. Without this, the GUI
        // gives no in-transcript signal that the child died - operators keep typing into a dead
        // pipe and only the (invisible) flat transcript records the send failure.
        if (t.To == SessionState.Exited)
        {
            void Apply()
            {
                var turn = EnsurePendingTurn(initialPrompt: "(session ended)");
                turn.Children.Add(new TurnContentItemViewModel(
                    TurnContentKind.System,
                    $"session exited ({t.Reason})",
                    $"transition: {t.From} -> {t.To}\nreason: {t.Reason}"));
                turn.IsInProgress = false;
                _pendingTurn = null;
            }
            if (Dispatcher.UIThread.CheckAccess()) Apply();
            else Dispatcher.UIThread.Post(Apply);
        }
    }

    private void OnEvent(ClaudeEvent ev)
    {
        if (Dispatcher.UIThread.CheckAccess()) ApplyEvent(ev);
        else Dispatcher.UIThread.Post(() => ApplyEvent(ev));
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
        // forever and IsReady would never flip even on subsequent ResultEvents - the exact
        // symptom of "buttons stay disabled forever". Claude emits system/init before any
        // other event and the events channel preserves FIFO, so observing ANY non-init event
        // proves init has already happened: treat that as proof and catch up.
        if (!_initSeen && ev is not SystemInitEvent)
        {
            _initSeen = true;
            _logger?.Information("gui-row[{Row}] catch-up: _initSeen flipped on first non-init event type={Type}", ShortId, ev.Type);
            SetIsReady("catch-up");
        }

        switch (ev)
        {
            case SystemInitEvent init:
                var initTurn = EnsurePendingTurn(initialPrompt: "(session start)");
                initTurn.Children.Add(new TurnContentItemViewModel(
                    TurnContentKind.System,
                    $"system/init model={init.Model ?? "?"} cwd={init.Cwd ?? "?"}",
                    $"claude_code_version={init.ClaudeCodeVersion ?? "?"}\npermissionMode={init.PermissionMode ?? "?"}"));
                // "(session ready)" is technical chatter, not an assistant reply - park it in
                // Details and leave the Agent body empty so the card reads as a status note.
                initTurn.Children.Add(new TurnContentItemViewModel(
                    TurnContentKind.System, "(session ready)", ""));
                initTurn.Headline = "";
                initTurn.IsInProgress = false;
                // Details starts collapsed (default). 4e auto-expanded it so the operator could
                // see the init payload at a glance, but in practice it just made the first card
                // look noisy; the chevron remains a one-click reveal.
                _pendingTurn = null;
                _initSeen = true;
                _logger?.Information("gui-row[{Row}] init: observed system/init activeTaskId={TaskId}", ShortId, Context.ActiveTaskId);
                // system/init is emitted only after claude has read stdin (FirstUserMessage), so
                // the pointer is guaranteed ahead of any operator-typed message in claude's queue.
                // Safe to flip for both free and task-bound sessions.
                SetIsReady("init");
                break;

            case AssistantEvent asst:
                var turn = EnsurePendingTurn(initialPrompt: "(claude-initiated)");
                AppendAssistantContent(turn, asst);
                break;

            case UserEvent userEv:
                if (_pendingTurn is { } current) AttachToolResults(current, userEv);
                break;

            case ResultEvent res:
                ClosePendingTurnWith(res.Result, res);
                _logger?.Information(
                    "gui-row[{Row}] result observed: numTurns={NumTurns} costUsd={Cost} initSeen={InitSeen} isReady={IsReady}",
                    ShortId, res.NumTurns, res.TotalCostUsd, _initSeen, IsReady);
                // Belt-and-braces safety net: in normal operation init/catch-up/ctor-pre-seed
                // have already flipped IsReady by now, but if anything reaches a ResultEvent
                // without it (e.g. parser/reader anomaly that swallowed init), this idempotent
                // flip unsticks the gate so the operator never sees a dead-permanently session.
                if (_initSeen && !IsReady) SetIsReady("result-after-init");
                break;

            case SystemCompactEvent:
            case SystemTaskStartedEvent:
            case SystemTaskProgressEvent:
            case SystemTaskNotificationEvent:
            case SystemStatusEvent:
            case SystemHookResponseEvent:
            case SystemPluginInstallEvent:
                _pendingTurn?.Children.Add(new TurnContentItemViewModel(
                    TurnContentKind.System,
                    $"{ev.Type}/{ev.Subtype ?? "?"}",
                    ev.Raw.ToJsonString()));
                break;

            case UnknownEvent unk:
                _pendingTurn?.Children.Add(new TurnContentItemViewModel(
                    TurnContentKind.UnknownEvent,
                    $"unknown_event: {unk.Discriminator}",
                    unk.Raw.ToJsonString()));
                break;
        }
    }

    private TurnNodeViewModel EnsurePendingTurn(string initialPrompt)
    {
        if (_pendingTurn is { } existing) return existing;
        var turn = new TurnNodeViewModel(initialPrompt);
        Turns.Add(turn);
        _pendingTurn = turn;
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

    private void ClosePendingTurnWith(string? finalText, ResultEvent? result = null)
    {
        if (_pendingTurn is not { } turn) return;
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
        _pendingTurn = null;
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
