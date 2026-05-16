namespace InfiniContext.Core.Tasks;

/// <summary>
/// Composes the first user message that the host injects when a task-bound session starts
/// (Phase 6 of Stage 2A). Pure logic — keeps the wire format in one tested place rather than
/// scattered across the host loop.
///
/// Mechanism is "5B" per Phase 0 research: the pointer mentions the skill by name so the
/// auto-discovered <c>infinicontext-task</c> skill fires deterministically. Drift, if any, is
/// noted as a single up-front sentence so the agent reconciles before doing other work.
/// </summary>
public static class TaskSessionPointer
{
    /// <summary>
    /// Build the pointer string. <paramref name="drift"/> is the list of <c>.md</c> files
    /// present on disk under the task folder but not referenced from <c>index.md</c>; pass an
    /// empty list when there is no drift.
    /// </summary>
    public static string ComposeFirstMessage(int taskId, string slug, string title, IReadOnlyList<string> drift)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(slug);
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        ArgumentNullException.ThrowIfNull(drift);
        if (taskId <= 0) throw new ArgumentOutOfRangeException(nameof(taskId), "taskId must be positive.");

        var folder = $".task/{taskId:D4}-{slug}/";
        var pointer = $"Working on task {taskId:D4} (\"{title}\"). Task folder: {folder}. Use the infinicontext-task skill.";

        if (drift.Count == 0) return pointer;

        var driftList = string.Join(", ", drift);
        return $"Note: {driftList} exist on disk but are missing from index.md — please reconcile.\n\n{pointer}";
    }
}
