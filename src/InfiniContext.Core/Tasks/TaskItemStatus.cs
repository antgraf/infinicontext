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
