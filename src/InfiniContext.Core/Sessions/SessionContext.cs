using InfiniContext.Core.Events;
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

    /// <summary>Effective model id the session was spawned with (<c>request.Model ??
    /// settings.Session.Model</c>), or null when neither was set and claude picked its own
    /// default. Surfaced so per-session UI can show the running model and offer a respawn-to-
    /// change affordance. Fixed for the process's life — claude's <c>--model</c> can't be changed
    /// in-flight, so changing it means killing and re-spawning the session.</summary>
    public string? Model { get; init; }

    /// <summary>Cost-in-USD reported by the most recent <c>ResultEvent</c>, or null if no turn
    /// has completed yet. Populated by <see cref="SessionManager"/> from the
    /// <see cref="SessionTranscriptReader"/>'s event stream so the host UI doesn't have to
    /// re-subscribe to <see cref="IClaudeSession.Events"/>.</summary>
    public decimal? LastCostUsd { get; internal set; }

    /// <summary>Turn count reported by the most recent <c>ResultEvent</c>.</summary>
    public int? LastNumTurns { get; internal set; }

    /// <summary>Capacity of the event replay ring (M19). Bounds the per-session retention so a
    /// session whose events are never replayed (e.g. a CLI session, which never calls
    /// <see cref="SubscribeWithReplay"/>) holds at most this many <see cref="ClaudeEvent"/>
    /// references. Generous enough to cover a full seed-prompt turn streaming before any UI
    /// consumer attaches.</summary>
    public const int EventReplayCapacity = 1024;

    private readonly object _eventGate = new();
    private readonly Queue<ClaudeEvent> _eventReplayBuffer = new(EventReplayCapacity);
    private Action<ClaudeEvent>? _eventObserved;

    /// <summary>
    /// Atomically replay the buffered event window to <paramref name="handler"/> and then subscribe
    /// it for all future events — under a lock that also guards <see cref="RaiseEventObserved"/>.
    ///
    /// <para>
    /// M19: the per-session <see cref="SessionTranscriptReader"/> starts pumping inside
    /// <see cref="SessionManager.CreateAsync"/> <i>before</i> any UI consumer can attach (the GUI
    /// subscribes in a later UI-thread continuation). A plain multicast <c>+=</c> subscription
    /// permanently loses every event observed in that window — for a task-bound spawn whose seed
    /// prompt starts streaming immediately, that can be the whole first turn. Replaying the buffer
    /// under the gate gives the handler a gap-free, in-order sequence (<c>[buffered] ++ [future]</c>)
    /// with no duplicates, mirroring <see cref="TranscriptModel.Snapshot"/> + <c>FragmentAppended</c>.
    /// </para>
    ///
    /// <para>
    /// Handlers run on the calling thread during replay (while the gate is held — they must not
    /// block) and on the reader's loop thread for future events. Marshal to the UI thread inside the
    /// handler if needed. Dispose the returned token to unsubscribe.
    /// </para>
    /// </summary>
    public IDisposable SubscribeWithReplay(Action<ClaudeEvent> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        lock (_eventGate)
        {
            foreach (var ev in _eventReplayBuffer) handler(ev);
            _eventObserved += handler;
        }
        return new EventSubscription(this, handler);
    }

    /// <summary>
    /// Append <paramref name="ev"/> to the bounded replay buffer and deliver it to current
    /// subscribers, atomically with respect to <see cref="SubscribeWithReplay"/>. Called by the
    /// <see cref="SessionManager"/>'s reader hook (and by tests) for every event pulled off the
    /// session's channel. The buffer append and the snapshot of subscribers happen under the gate
    /// so a subscribe racing this call sees the event either via replay or via live delivery —
    /// never both, never neither.
    /// </summary>
    internal void RaiseEventObserved(ClaudeEvent ev)
    {
        Action<ClaudeEvent>? handlers;
        lock (_eventGate)
        {
            if (_eventReplayBuffer.Count == EventReplayCapacity) _eventReplayBuffer.Dequeue();
            _eventReplayBuffer.Enqueue(ev);
            handlers = _eventObserved;
        }
        handlers?.Invoke(ev);
    }

    private void Unsubscribe(Action<ClaudeEvent> handler)
    {
        lock (_eventGate) _eventObserved -= handler;
    }

    private sealed class EventSubscription : IDisposable
    {
        private SessionContext? _ctx;
        private readonly Action<ClaudeEvent> _handler;

        public EventSubscription(SessionContext ctx, Action<ClaudeEvent> handler)
        {
            _ctx = ctx;
            _handler = handler;
        }

        public void Dispose() => Interlocked.Exchange(ref _ctx, null)?.Unsubscribe(_handler);
    }

    private int _rotationReason; // 0=None, 1=Soft, 2=Hard; see RotationReason.

    /// <summary>
    /// Highest context-pressure nudge that has fired during this session, or <see cref="Sessions.RotationReason.None"/>
    /// if neither has. The host reads this on the session's <c>Exited</c> transition: a non-<see cref="Sessions.RotationReason.None"/>
    /// value together with a non-null <see cref="ActiveTaskId"/> is the signal to auto-rotate to a fresh session bound to
    /// the same task. Wired by <see cref="SessionManager.CreateAsync"/> against
    /// <see cref="ContextPressureMonitor.SoftCrossed"/> / <see cref="ContextPressureMonitor.HardCrossed"/>.
    /// </summary>
    public RotationReason RotationReason => (RotationReason)Volatile.Read(ref _rotationReason);

    /// <summary>
    /// Record a context-pressure nudge. Escalation-only: a subsequent <see cref="Sessions.RotationReason.Soft"/>
    /// after <see cref="Sessions.RotationReason.Hard"/> has been recorded is dropped, matching the monitor's "no flapping"
    /// invariant (a hard crossing implicitly flags soft as observed; soft can never re-fire after hard).
    /// </summary>
    internal void MarkRotationReason(RotationReason reason)
    {
        var target = (int)reason;
        int observed;
        do
        {
            observed = Volatile.Read(ref _rotationReason);
            if (observed >= target) return;
        } while (Interlocked.CompareExchange(ref _rotationReason, target, observed) != observed);
    }

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
