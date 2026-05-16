using Serilog;

namespace InfiniContext.Core.Tasks;

/// <summary>
/// Centralizes <see cref="TaskItemStatus.InProgress"/> / <see cref="TaskItemStatus.Idle"/> flips
/// against <see cref="TaskStore"/> and enforces the rule "exactly one session per task may be
/// <c>InProgress</c> at a time" (Stage 2B D9).
///
/// <para>
/// In Stage 1 + 2A there is only ever one session in flight, so the in-memory holder map is
/// effectively a no-op guard. The map becomes load-bearing in Stage 2B Phase 2 when
/// <c>SessionManager</c> can run N concurrent sessions and two of them might race to
/// <c>/task switch</c> to the same task id.
/// </para>
///
/// <para>
/// The clock is injectable so tests can fake the <c>last_active_at_unix_ms</c> writes that
/// <see cref="TaskStore.UpdateStatusAsync"/> performs (closes 2A todo §B A4).
/// </para>
/// </summary>
public sealed class TaskActivityCoordinator
{
    private readonly TaskStore _store;
    private readonly Func<DateTimeOffset> _clock;
    private readonly ILogger _logger;
    private readonly object _gate = new();
    private readonly Dictionary<int, Guid> _holderByTaskId = new();

    public TaskActivityCoordinator(TaskStore store, Func<DateTimeOffset> clock, ILogger logger)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Attempt to mark <paramref name="taskId"/> as active in <paramref name="sessionId"/>.
    /// Returns <see cref="EnterTaskResult.GrantedResult"/> on success (the store row is flipped
    /// to <see cref="TaskItemStatus.InProgress"/>) or <see cref="EnterTaskResult.RefusedResult"/>
    /// if another live session already holds the task.
    ///
    /// <para>Store-write failures are logged at warning and treated as a soft success — the
    /// claim stands even if the DB write didn't land. The previous single-session host code
    /// (<c>Program.cs:188</c>) had the same shape; preserving it keeps Stage 1 + 2A behavior
    /// observable.</para>
    /// </summary>
    public async Task<EnterTaskResult> EnterAsync(Guid sessionId, int taskId, CancellationToken ct = default)
    {
        lock (_gate)
        {
            if (_holderByTaskId.TryGetValue(taskId, out var holder) && holder != sessionId)
                return new EnterTaskResult.RefusedResult(taskId, holder);
            _holderByTaskId[taskId] = sessionId;
        }

        try
        {
            await _store.UpdateStatusAsync(taskId, TaskItemStatus.InProgress, _clock().ToUnixTimeMilliseconds(), ct)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "tasks: failed to mark {TaskId} InProgress", taskId);
        }

        return EnterTaskResult.Granted;
    }

    /// <summary>
    /// Release the task back to <see cref="TaskItemStatus.Idle"/>. Releasing a task that this
    /// session doesn't hold is a no-op on the in-memory map but the store write still fires —
    /// matching the prior outer-loop behavior of flushing Idle on every exit regardless of
    /// who held the row.
    /// </summary>
    public async Task LeaveAsync(Guid sessionId, int taskId, CancellationToken ct = default)
    {
        lock (_gate)
        {
            if (_holderByTaskId.TryGetValue(taskId, out var holder) && holder == sessionId)
                _holderByTaskId.Remove(taskId);
        }

        try
        {
            await _store.UpdateStatusAsync(taskId, TaskItemStatus.Idle, _clock().ToUnixTimeMilliseconds(), ct)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "tasks: failed to mark {TaskId} Idle", taskId);
        }
    }
}

/// <summary>
/// Outcome of <see cref="TaskActivityCoordinator.EnterAsync"/>. <see cref="GrantedResult"/> is
/// emitted when the calling session now holds the task; <see cref="RefusedResult"/> when a
/// different live session already holds it.
/// </summary>
public abstract record EnterTaskResult
{
    private EnterTaskResult() { }

    public static EnterTaskResult Granted { get; } = new GrantedResult();

    public sealed record GrantedResult : EnterTaskResult;

    /// <param name="TaskId">The task id that was refused.</param>
    /// <param name="HolderSessionId">The session currently holding the task.</param>
    public sealed record RefusedResult(int TaskId, Guid HolderSessionId) : EnterTaskResult;
}
