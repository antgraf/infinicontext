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
        => ComposeFirstMessage(taskId, slug, title, drift, Array.Empty<string>());

    /// <summary>
    /// Build the pointer string with bidirectional drift (M17). <paramref name="unindexedFiles"/>
    /// are present on disk but missing from <c>index.md</c>; <paramref name="missingFiles"/> are
    /// listed in <c>index.md</c> but absent from disk. Each non-empty direction prepends its own
    /// reconcile note so a fresh session fixes a stale table of contents before doing other work.
    /// </summary>
    public static string ComposeFirstMessage(
        int taskId,
        string slug,
        string title,
        IReadOnlyList<string> unindexedFiles,
        IReadOnlyList<string> missingFiles)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(slug);
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        ArgumentNullException.ThrowIfNull(unindexedFiles);
        ArgumentNullException.ThrowIfNull(missingFiles);
        if (taskId <= 0) throw new ArgumentOutOfRangeException(nameof(taskId), "taskId must be positive.");

        var folder = $".task/{taskId:D4}-{slug}/";
        var pointer = $"Working on task {taskId:D4} (\"{title}\"). Task folder: {folder}. Use the infinicontext-task skill.";

        var notes = new List<string>();
        if (unindexedFiles.Count > 0)
            notes.Add($"Note: {string.Join(", ", unindexedFiles)} exist on disk but are missing from index.md — please reconcile.");
        if (missingFiles.Count > 0)
            notes.Add($"Note: {string.Join(", ", missingFiles)} are listed in index.md but do not exist on disk — please reconcile.");

        if (notes.Count == 0) return pointer;
        return $"{string.Join("\n", notes)}\n\n{pointer}";
    }
}
