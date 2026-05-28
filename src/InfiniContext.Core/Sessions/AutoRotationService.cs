using InfiniContext.Core.State;
using InfiniContext.Core.Tasks;
using Serilog;

namespace InfiniContext.Core.Sessions;

/// <summary>
/// Outcome of an <see cref="AutoRotationProposal"/>: whether the rotation was approved (caller
/// should spawn a fresh session bound to the same task), refused by the guard (the cap was hit;
/// caller should fall back to a normal exit and surface a message), or failed while building the
/// plan.
/// </summary>
public enum AutoRotationStatus
{
    Approved,
    Refused,
    Failed,
}

/// <summary>
/// Event payload from <see cref="AutoRotationService.AutoRotationProposed"/>. Fires once on the
/// <see cref="SessionState.Exited"/> transition of a task-bound session whose
/// <see cref="SessionContext.RotationReason"/> is non-<see cref="RotationReason.None"/>.
/// </summary>
/// <param name="Session">The exited session that triggered the proposal.</param>
/// <param name="TaskId">The task id the session was bound to.</param>
/// <param name="Reason">The highest-severity nudge that fired during the session.</param>
/// <param name="Status">Approved / Refused / Failed.</param>
/// <param name="Plan">Non-null when <paramref name="Status"/> is <see cref="AutoRotationStatus.Approved"/>
/// and the task was found. Null if Approved but the task lookup returned null, or in any non-Approved status.</param>
/// <param name="RefusedCap">Guard's <see cref="AutoRotationGuard.MaxRotations"/> at the time of refusal.
/// Null for non-refused statuses.</param>
/// <param name="RefusedWindow">Guard's <see cref="AutoRotationGuard.Window"/> at the time of refusal.
/// Null for non-refused statuses.</param>
/// <param name="Failure">Non-null when <paramref name="Status"/> is <see cref="AutoRotationStatus.Failed"/>:
/// the exception from the prepare-switch delegate.</param>
public sealed record AutoRotationProposal(
    SessionContext Session,
    int TaskId,
    RotationReason Reason,
    AutoRotationStatus Status,
    TaskSwitchPlan? Plan,
    int? RefusedCap,
    TimeSpan? RefusedWindow,
    Exception? Failure);

/// <summary>
/// Core-side auto-rotation policy promoted out of <c>MultiSessionHost.TryStartAutoRotation</c>
/// so both adapters (the CLI host, the GUI's <c>MainWindowViewModel</c>) consume the same signal
/// rather than duplicate the policy. The service watches every <see cref="SessionContext"/>
/// registered with the <see cref="SessionManager"/>; on <see cref="SessionState.Exited"/> with a
/// recorded <see cref="RotationReason"/> and a non-null <see cref="SessionContext.ActiveTaskId"/>
/// it asks the <see cref="AutoRotationGuard"/> whether another rotation is permitted, builds the
/// <see cref="TaskSwitchPlan"/> via the supplied prepare-switch delegate, and raises
/// <see cref="AutoRotationProposed"/>.
///
/// <para>
/// The service deliberately does <b>not</b> spawn the new session — the adapter knows how to
/// update its focus model, write status text, and pick which row receives the seed prompt. The
/// boundary mirrors today's CLI shape: the service proposes; the host disposes.
/// </para>
/// </summary>
public sealed class AutoRotationService : IDisposable
{
    private readonly SessionManager _manager;
    private readonly AutoRotationGuard _guard;
    private readonly Func<int, CancellationToken, Task<TaskSwitchPlan?>> _prepareSwitch;
    private readonly ILogger _logger;
    private readonly object _gate = new();
    private readonly Dictionary<Guid, Action<StateTransition>> _handlers = new();
    private int _disposed;

    /// <summary>Fires once per qualifying <see cref="SessionState.Exited"/> transition. Handlers
    /// run on the state machine's locked thread (the stdout-loop or send thread that produced the
    /// transition); marshal to a UI thread inside the handler if needed.</summary>
    public event Action<AutoRotationProposal>? AutoRotationProposed;

    public AutoRotationService(
        SessionManager manager,
        AutoRotationGuard guard,
        Func<int, CancellationToken, Task<TaskSwitchPlan?>> prepareSwitch,
        ILogger logger)
    {
        _manager = manager ?? throw new ArgumentNullException(nameof(manager));
        _guard = guard ?? throw new ArgumentNullException(nameof(guard));
        _prepareSwitch = prepareSwitch ?? throw new ArgumentNullException(nameof(prepareSwitch));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        // Attach to sessions that already exist (Active is empty at Program.cs construction time
        // but a test might construct the service against a manager with sessions registered).
        foreach (var ctx in _manager.Active) Attach(ctx);
        _manager.SessionCreated += OnSessionCreated;
    }

    private void OnSessionCreated(SessionContext ctx) => Attach(ctx);

    private void Attach(SessionContext ctx)
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        Action<StateTransition> handler = t => OnStateTransition(ctx, t);
        lock (_gate)
        {
            if (_handlers.ContainsKey(ctx.Id)) return;
            _handlers[ctx.Id] = handler;
        }
        ctx.Session.State.Transitioned += handler;
    }

    private void OnStateTransition(SessionContext ctx, StateTransition t)
    {
        if (t.To != SessionState.Exited) return;

        // Unsubscribe immediately so a flapping state machine cannot double-fire the proposal.
        Action<StateTransition>? handler;
        lock (_gate)
        {
            if (!_handlers.Remove(ctx.Id, out handler)) return;
        }
        try { ctx.Session.State.Transitioned -= handler; } catch { /* best-effort */ }

        if (ctx.RotationReason == RotationReason.None) return;
        if (ctx.ActiveTaskId is not int taskId) return;

        _ = ProposeAsync(ctx, taskId, ctx.RotationReason);
    }

    private async Task ProposeAsync(SessionContext ctx, int taskId, RotationReason reason)
    {
        if (!_guard.TryRegister(taskId))
        {
            _logger.Warning("auto-rotation: cap hit for task {TaskId} ({Max} within {Minutes} min)",
                taskId, _guard.MaxRotations, _guard.Window.TotalMinutes);
            Raise(new AutoRotationProposal(
                Session: ctx,
                TaskId: taskId,
                Reason: reason,
                Status: AutoRotationStatus.Refused,
                Plan: null,
                RefusedCap: _guard.MaxRotations,
                RefusedWindow: _guard.Window,
                Failure: null));
            return;
        }

        try
        {
            var plan = await _prepareSwitch(taskId, CancellationToken.None).ConfigureAwait(false);
            _logger.Information("auto-rotation: task {TaskId} reason={Reason} plan={HasPlan}",
                taskId, reason, plan is not null);
            Raise(new AutoRotationProposal(
                Session: ctx,
                TaskId: taskId,
                Reason: reason,
                Status: AutoRotationStatus.Approved,
                Plan: plan,
                RefusedCap: null,
                RefusedWindow: null,
                Failure: null));
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "auto-rotation: failed to build plan for task {TaskId}", taskId);
            Raise(new AutoRotationProposal(
                Session: ctx,
                TaskId: taskId,
                Reason: reason,
                Status: AutoRotationStatus.Failed,
                Plan: null,
                RefusedCap: null,
                RefusedWindow: null,
                Failure: ex));
        }
    }

    private void Raise(AutoRotationProposal proposal)
    {
        try { AutoRotationProposed?.Invoke(proposal); }
        catch (Exception ex) { _logger.Warning(ex, "auto-rotation: subscriber threw"); }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _manager.SessionCreated -= OnSessionCreated;
        KeyValuePair<Guid, Action<StateTransition>>[] snapshot;
        lock (_gate)
        {
            snapshot = _handlers.ToArray();
            _handlers.Clear();
        }
        foreach (var kv in snapshot)
        {
            var ctx = _manager.TryGet(kv.Key);
            if (ctx is null) continue;
            try { ctx.Session.State.Transitioned -= kv.Value; } catch { /* best-effort */ }
        }
    }
}
