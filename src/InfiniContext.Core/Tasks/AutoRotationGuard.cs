namespace InfiniContext.Core.Tasks;

/// <summary>
/// Caps the number of consecutive context-pressure auto-rotations on a single task within a
/// short time window. Prevents a runaway loop where a fresh session immediately re-hits the
/// hard threshold on initial load (e.g. enormous artifact set) and gets rotated again, ad
/// infinitum. When the cap is hit the host falls back to a normal exit and surfaces a
/// message asking the operator to investigate or retry manually.
/// </summary>
/// <remarks>
/// State is per-task-id and persists across host loop iterations (one
/// <see cref="AutoRotationGuard"/> per host process). Construct once in <c>Program.cs</c> and
/// pass into <c>MultiSessionHost</c>.
/// </remarks>
public sealed class AutoRotationGuard
{
    private readonly object _gate = new();
    private readonly Dictionary<int, List<DateTimeOffset>> _history = new();
    private readonly Func<DateTimeOffset> _clock;

    /// <summary>Maximum auto-rotations allowed on a single task within <see cref="Window"/>.</summary>
    public int MaxRotations { get; }

    /// <summary>Rolling window over which <see cref="MaxRotations"/> is enforced.</summary>
    public TimeSpan Window { get; }

    public AutoRotationGuard(int maxRotations, TimeSpan window, Func<DateTimeOffset>? clock = null)
    {
        if (maxRotations < 1) throw new ArgumentOutOfRangeException(nameof(maxRotations), "Must be >= 1.");
        if (window <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(window), "Must be > 0.");
        MaxRotations = maxRotations;
        Window = window;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    /// <summary>
    /// Attempt to register a fresh auto-rotation against <paramref name="taskId"/>. Returns
    /// true if the rotation is allowed (the caller should proceed); false if the cap has
    /// already been hit in the current window (the caller should refuse the rotation, log a
    /// warning, and fall back to a normal exit).
    /// </summary>
    /// <remarks>
    /// On a successful return the new timestamp is recorded; on refusal nothing is recorded
    /// (so a single over-cap call doesn't extend the window further).
    /// </remarks>
    public bool TryRegister(int taskId)
    {
        var now = _clock();
        var cutoff = now - Window;
        lock (_gate)
        {
            if (!_history.TryGetValue(taskId, out var list))
            {
                list = new List<DateTimeOffset>();
                _history[taskId] = list;
            }
            list.RemoveAll(t => t < cutoff);
            if (list.Count >= MaxRotations) return false;
            list.Add(now);
            return true;
        }
    }

    /// <summary>Snapshot of the rotation count for <paramref name="taskId"/> within the current window. Tests only.</summary>
    public int CurrentCount(int taskId)
    {
        var cutoff = _clock() - Window;
        lock (_gate)
        {
            if (!_history.TryGetValue(taskId, out var list)) return 0;
            return list.Count(t => t >= cutoff);
        }
    }
}
