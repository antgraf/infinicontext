namespace InfiniContext.Core.Sessions;

/// <summary>
/// Why a <see cref="SessionContext"/> should be auto-rotated when its underlying session exits.
/// Set by <c>SessionManager</c> in response to <c>ContextPressureMonitor</c> crossings; read by
/// the host's pane lifecycle on the <c>Exited</c> state transition. Escalation-only: once
/// <see cref="Hard"/> has been recorded, a subsequent <see cref="Soft"/> does not downgrade it.
/// </summary>
public enum RotationReason
{
    /// <summary>No nudge has fired during this session; an exit is treated as a normal end.</summary>
    None = 0,

    /// <summary>
    /// <c>ContextPressureMonitor.SoftCrossed</c> fired. The agent was asked to finish its
    /// current sub-task and exit cleanly for handoff. If the session exits, the host should
    /// rotate to a fresh session bound to the same task.
    /// </summary>
    Soft = 1,

    /// <summary>
    /// <c>ContextPressureMonitor.HardCrossed</c> fired. The agent was told to flush now and
    /// stop. If the session exits, the host must rotate to a fresh session bound to the same
    /// task тАФ otherwise structured task compaction silently halts.
    /// </summary>
    Hard = 2,
}
