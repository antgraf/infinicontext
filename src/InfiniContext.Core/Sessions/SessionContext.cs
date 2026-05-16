using InfiniContext.Core.Mcp;
using InfiniContext.Core.Tasks;

namespace InfiniContext.Core.Sessions;

/// <summary>
/// Host-side bundle for one managed session. Stage 2B Phase 2 introduces this as the per-session
/// state owned by <see cref="SessionManager"/>: the running <see cref="IClaudeSession"/>, the
/// context-pressure monitor + threshold injector that watch it, the approval sink it's wired to,
/// the transcript model that will hold rendered fragments for off-screen preservation (Phase 4),
/// and the optional task id this session has claimed via
/// <see cref="TaskActivityCoordinator.EnterAsync"/>.
///
/// <para>
/// <see cref="DisposeAsync"/> only disposes the underlying <see cref="IClaudeSession"/> — the
/// session is responsible for tearing down its sidecar in the correct order (stdout drain before
/// pipe closes; locks reclaimed before the channel is torn down). The other bundle members are
/// either inert (<see cref="TranscriptModel"/>) or get released when the session's reader stops
/// firing events (<see cref="ContextPressureMonitor"/>, <see cref="ThresholdInjector"/>). The
/// approval sink is owned by the host and outlives the session.
/// </para>
/// </summary>
public sealed class SessionContext : IAsyncDisposable
{
    /// <summary>Host-side identifier minted at construction. Stable for the session's lifetime;
    /// not to be confused with the <see cref="IClaudeSession.SessionId"/> issued by claude.</summary>
    public Guid Id { get; }

    public IClaudeSession Session { get; }
    public ContextPressureMonitor PressureMonitor { get; }
    public ThresholdInjector Injector { get; }

    /// <summary>Per-session approval sink with a mutable inner so the host's <c>/approval</c>
    /// mode toggle (D12 — host-wide effective mode) can hot-swap each session independently against
    /// its own <see cref="ApprovalSlot"/>.</summary>
    public SwitchableApprovalSink ApprovalSink { get; }

    /// <summary>Per-session bridge between the approval sink and the host UI. Stage 2B Phase 5:
    /// the host's input loop checks the focused session's slot on each line read to decide whether
    /// to treat the input as an approval response or pass it through as a prompt.</summary>
    public PendingApprovalSlot ApprovalSlot { get; }

    public TranscriptModel Transcript { get; }

    /// <summary>Task id this session has claimed through <see cref="TaskActivityCoordinator"/>,
    /// or null if the session is not task-bound.</summary>
    public int? ActiveTaskId { get; }

    /// <summary>Working directory the session was spawned in. Surfaced here (rather than
    /// extracted from <see cref="IClaudeSession"/> internals) so multi-pane UI can render
    /// session rows without reaching into private manager state.</summary>
    public string? Cwd { get; init; }

    /// <summary>Cost-in-USD reported by the most recent <c>ResultEvent</c>, or null if no turn
    /// has completed yet. Populated by <see cref="SessionManager"/> from the
    /// <see cref="SessionTranscriptReader"/>'s event stream so the host UI doesn't have to
    /// re-subscribe to <see cref="IClaudeSession.Events"/>.</summary>
    public decimal? LastCostUsd { get; internal set; }

    /// <summary>Turn count reported by the most recent <c>ResultEvent</c>.</summary>
    public int? LastNumTurns { get; internal set; }

    public SessionContext(
        Guid id,
        IClaudeSession session,
        ContextPressureMonitor pressureMonitor,
        ThresholdInjector injector,
        SwitchableApprovalSink approvalSink,
        PendingApprovalSlot approvalSlot,
        TranscriptModel transcript,
        int? activeTaskId)
    {
        if (id == Guid.Empty) throw new ArgumentException("Id must not be empty.", nameof(id));
        Id = id;
        Session = session ?? throw new ArgumentNullException(nameof(session));
        PressureMonitor = pressureMonitor ?? throw new ArgumentNullException(nameof(pressureMonitor));
        Injector = injector ?? throw new ArgumentNullException(nameof(injector));
        ApprovalSink = approvalSink ?? throw new ArgumentNullException(nameof(approvalSink));
        ApprovalSlot = approvalSlot ?? throw new ArgumentNullException(nameof(approvalSlot));
        Transcript = transcript ?? throw new ArgumentNullException(nameof(transcript));
        ActiveTaskId = activeTaskId;
    }

    public ValueTask DisposeAsync() => Session.DisposeAsync();
}
