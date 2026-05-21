using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using InfiniContext.Core.Events;
using InfiniContext.Core.Sessions;
using InfiniContext.Core.State;

namespace InfiniContext.Gui.ViewModels;

/// <summary>
/// Row in the Sessions tree (child of a <see cref="TaskNodeViewModel"/>). Wraps a
/// <see cref="SessionContext"/> and exposes (a) the metadata the tree row needs (short id, cwd,
/// state badge, drift, cost), and (b) the structured per-turn transcript that drives the central
/// column's tree.
///
/// <para>
/// Subscribes to <see cref="SessionContext.EventObserved"/> on construction and groups events
/// into <see cref="TurnNodeViewModel"/>s. One turn = one user prompt + the resulting
/// assistant/tool-use/tool-result/result cycle. UI marshalling is via <see cref="Dispatcher"/>.
/// </para>
/// </summary>
public sealed partial class SessionRowViewModel : ObservableObject, IDisposable
{
    public SessionContext Context { get; }
    public Guid Id => Context.Id;
    public string ShortId => Context.Id.ToString("N").Substring(0, 8);
    public string Cwd => Context.Cwd ?? "<no cwd>";
    public TranscriptViewModel Transcript { get; }

    /// <summary>Per-turn structured view of the conversation. Driven by raw events from
    /// <see cref="SessionContext.EventObserved"/>; the flat <see cref="Transcript"/> list stays
    /// as a fallback for debug / raw inspection.</summary>
    public ObservableCollection<TurnNodeViewModel> Turns { get; } = new();

    [ObservableProperty]
    private bool _isFocused;

    [ObservableProperty]
    private string _statusGlyph = ViewModels.StatusGlyph.Idle;

    private readonly Action<StateTransition> _onTransition;
    private readonly Action<ClaudeEvent> _onEvent;
    private TurnNodeViewModel? _pendingTurn;
    private bool _disposed;

    public SessionRowViewModel(SessionContext context)
    {
        Context = context ?? throw new ArgumentNullException(nameof(context));
        Transcript = new TranscriptViewModel(context.Transcript);
        _onTransition = OnStateTransition;
        _onEvent = OnEvent;
        context.Session.State.Transitioned += _onTransition;
        context.EventObserved += _onEvent;
        StatusGlyph = ViewModels.StatusGlyph.ForSession(context.Session.State.State, context.ApprovalSlot.Current is not null);
    }

    /// <summary>
    /// Called by <see cref="MainWindowViewModel.SendPromptAsync"/> when the operator sends a
    /// prompt. Creates a pending <see cref="TurnNodeViewModel"/> with "thinking..." headline.
    /// </summary>
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
        // Marshal all turn-tree mutation onto the UI thread so ObservableCollection<TurnNode>
        // and its children's ObservableCollection<TurnContent> stay single-threaded.
        if (Dispatcher.UIThread.CheckAccess()) ApplyEvent(ev);
        else Dispatcher.UIThread.Post(() => ApplyEvent(ev));
    }

    private void ApplyEvent(ClaudeEvent ev)
    {
        switch (ev)
        {
            case SystemInitEvent init:
                EnsurePendingTurn(initialPrompt: "(session start)")
                    .Children.Add(new TurnContentItemViewModel(
                        TurnContentKind.System,
                        $"system/init model={init.Model ?? "?"} cwd={init.Cwd ?? "?"}",
                        $"claude_code_version={init.ClaudeCodeVersion ?? "?"}\npermissionMode={init.PermissionMode ?? "?"}"));
                // The init "turn" never gets a real assistant response - close it immediately.
                ClosePendingTurnWith("(session ready)");
                break;

            case AssistantEvent asst:
                var turn = EnsurePendingTurn(initialPrompt: "(claude-initiated)");
                AppendAssistantContent(turn, asst);
                break;

            case UserEvent userEv:
                // Tool-result echoes from claude. Attach to the matching tool_use child.
                if (_pendingTurn is { } current) AttachToolResults(current, userEv);
                break;

            case ResultEvent res:
                ClosePendingTurnWith(res.Result, res);
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

            // ControlResponseEvent, RateLimitEvent, PartialMessageEvent: intentionally ignored
            // in the turn tree (informational; visible in the log).
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
                    // Last text block becomes the headline; older text gets demoted to a child
                    // so nothing is lost on multi-step assistant turns.
                    if (!string.IsNullOrEmpty(turn.Headline) && turn.Headline != "thinking..." && turn.Headline != "(session ready)" && turn.Headline != "(claude-initiated)")
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
                    // Insert right after the matching tool_use for visual locality.
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
            if (parts.Count > 0) turn.FooterSummary = string.Join(" ┬╖ ", parts);
        }
        _pendingTurn = null;
    }

    private static string SummarizeInput(string? json)
    {
        if (string.IsNullOrEmpty(json)) return "";
        return Truncate(json, 60);
    }

    private static string Truncate(string s, int max) =>
        s.Length <= max ? s : s[..(max - 1)] + "тАж";

    public string State => Context.Session.State.State.ToString();
    public int Drift => Context.Session.UnknownEventCount;
    public string CostDisplay => Context.LastCostUsd is { } c ? $"${c:0.0000}" : "тАФ";

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { Context.Session.State.Transitioned -= _onTransition; } catch { /* best-effort */ }
        try { Context.EventObserved -= _onEvent; } catch { /* best-effort */ }
        Transcript.Dispose();
    }
}
