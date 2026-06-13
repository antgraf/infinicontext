namespace InfiniContext.Core.Tasks;

/// <summary>
/// Lifecycle status of a tracked task. MVP supports two states; richer states
/// (e.g. <c>InSync</c>, <c>Stale</c>) are reserved for post-MVP per the design doc.
///
/// <para>
/// Named <c>TaskItemStatus</c> rather than <c>TaskStatus</c> to avoid colliding with
/// <see cref="System.Threading.Tasks.TaskStatus"/> in consumers that import both
/// namespaces.
/// </para>
/// </summary>
public enum TaskItemStatus
{
    InProgress,
    Idle,
}

/// <summary>Outcome of <see cref="TaskStore.DeleteIfNotInProgressAsync"/> (M16).</summary>
public enum DeleteOutcomeKind
{
    /// <summary>No row with that id existed.</summary>
    NotFound,

    /// <summary>The row was <see cref="TaskItemStatus.InProgress"/>; deletion was refused.</summary>
    RefusedInProgress,

    /// <summary>The row was deleted.</summary>
    Deleted,
}

/// <param name="Kind">What happened.</param>
/// <param name="OrphanedChildren">Children whose <c>parent_task_id</c> was nulled (only meaningful for <see cref="DeleteOutcomeKind.Deleted"/>).</param>
public sealed record DeleteOutcome(DeleteOutcomeKind Kind, int OrphanedChildren);
