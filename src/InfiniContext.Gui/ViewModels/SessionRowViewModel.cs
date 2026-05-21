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
    /// For task-bound sessions (<c>SessionContext.ActiveTaskId is not null</c>), the
    /// <c>SpawnRequest.FirstUserMessage</c> pointer is sent fire-and-forget after
    /// <c>system/init</c> (see <c>ClaudeSession.StdoutLoopAsync</c>) - we wait for the first
    /// <c>ResultEvent</c> after init so that pointer turn has fully closed before the operator
    /// can interleave their own prompts. For free sessions (no pointer), this flips on the
    /// <c>system/init</c> event itself. Bound to button <c>IsEnabled</c> in the row template;
    /// the parent VM watches via <see cref="ObservableObject.PropertyChanged"/> to refresh
    /// <c>SendPromptCommand.CanExecute</c> when the focused session warms up.
    /// </summary>
    [ObservableProperty]
    private bool _isReady;

    /// <summary>True between <c>system/init</c> and the first <c>ResultEvent</c> on a task-bound
    /// session - i.e. while the pointer-acknowledgement turn is in flight. Internal flag the
    /// event router uses to gate the IsReady flip; kept distinct from <see cref="IsReady"/> so
    /// the latter has a single state-machine meaning (true == operator can send).</summary>
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
    /// <summary>Seconds remaining at the moment the banner was opened. Static text (not a live
    /// countdown) so we don't burn a per-second timer on every row; the slot's timeout still
    /// fires server-side. Live countdown is a polish follow-up.</summary>
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
        context.Session.State.Transitioned += _onTransition;
        context.EventObserved += _onEvent;
        context.ApprovalSlot.Changed += _onApprovalChanged;
        PendingApproval = context.ApprovalSlot.Current;
        StatusGlyph = ViewModels.StatusGlyph.ForSession(context.Session.State.State, IsAwaitingApproval);

        // Race protection (companion to the catch-up in ApplyEvent): if events have already
        // flowed before we got to subscribe, Session.SessionId is non-null (it's assigned the
        // moment claude's first event carries a session_id - see ClaudeSession.StdoutLoopAsync).
        // For free sessions claude is idle after system/init, so the catch-up in ApplyEvent
        // would never fire without further events; mark init-seen here so IsReady flips
        // immediately. Task-bound sessions also benefit: a missed init still lets the next
        // ResultEvent flip IsReady because _initSeen is already true.
        //
        // Task-bound full-race case: if the reader drained init AND the pointer's ResultEvent
        // before we subscribed, no future event will fire - the agent is idle waiting for the
        // operator. SessionManager.CreateAsync writes LastNumTurns the moment a ResultEvent is
        // observed (see SessionManager.cs:247), so a non-null value proves the pointer turn has
        // already closed - safe to flip IsReady.
        var sessionIdAtCtor = context.Session.SessionId;
        var lastNumTurnsAtCtor = context.LastNumTurns;
        if (sessionIdAtCtor is not null)
        {
            _initSeen = true;
            if (context.ActiveTaskId is null || lastNumTurnsAtCtor is not null)
                SetIsReady("ctor-pre-seed");
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
        StatusGlyph = ViewModels.StatusGlyph.ForSession(t.To, Context.ApprovalSlot.Current is not null);
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
            if (Context.ActiveTaskId is null) SetIsReady("catch-up-free");
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
                // Free sessions have no pointer to wait for - flip ready immediately. Task-bound
                // sessions wait until the pointer-acknowledgement ResultEvent below.
                if (Context.ActiveTaskId is null) SetIsReady("init-free");
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
                // First ResultEvent after init signals that the FirstUserMessage pointer turn
                // (if any) has fully closed. Safe to enable operator input now.
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

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { Context.Session.State.Transitioned -= _onTransition; } catch { /* best-effort */ }
        try { Context.EventObserved -= _onEvent; } catch { /* best-effort */ }
        try { Context.ApprovalSlot.Changed -= _onApprovalChanged; } catch { /* best-effort */ }
        Transcript.Dispose();
    }
}
