namespace InfiniContext.Core.Tasks;

/// <summary>
/// Outcome of <see cref="TaskService.DeleteAsync"/>. Carries enough state for the CLI handler
/// to produce a useful message (which row was dropped, whether the on-disk folder was touched,
/// how many children were orphaned by the FK clear).
/// </summary>
/// <param name="Record">The record as it existed in the registry immediately before deletion.</param>
/// <param name="FolderPath">Absolute path of the task folder that <em>would</em> be removed if
/// <c>removeFolder=true</c>. Always populated (even when the folder doesn't exist), so callers
/// can mention the path in messages.</param>
/// <param name="FolderExisted"><c>true</c> if the folder was present on disk when delete ran.</param>
/// <param name="FolderRemoved"><c>true</c> if the folder existed AND <c>removeFolder=true</c>.</param>
/// <param name="OrphanedChildren">Count of rows whose <c>parent_task_id</c> was nulled because
/// they pointed at the deleted row.</param>
public sealed record TaskDeleteResult(
    TaskRecord Record,
    string FolderPath,
    bool FolderExisted,
    bool FolderRemoved,
    int OrphanedChildren);
