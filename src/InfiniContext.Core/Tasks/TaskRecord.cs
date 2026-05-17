namespace InfiniContext.Core.Tasks;

/// <summary>
/// A row in the <c>tasks</c> table. Mirrors the schema declared in
/// <c>Migration_001_InitialSchema</c>. Timestamps are Unix milliseconds (UTC).
/// </summary>
/// <param name="Id">Globally unique identifier within the host's SQLite registry. Allocated by <see cref="TaskStore.AllocateIdAsync(System.Threading.CancellationToken)"/>.</param>
/// <param name="HomeCwd">Single home repo for this task. Cross-repo work reaches other paths from here (see decision: cross-repo single home_cwd).</param>
/// <param name="Title">Human-readable name; light-model-generated at creation time.</param>
/// <param name="CreatedAtUnixMs">Unix epoch milliseconds (UTC) at row insertion.</param>
/// <param name="LastActiveAtUnixMs">Unix epoch milliseconds (UTC). Bumped on session start/exit and other meaningful state changes.</param>
/// <param name="Status">Lifecycle state.</param>
/// <param name="ParentTaskId">If this task was forked from another, the parent's id; otherwise null.</param>
/// <param name="BaseCommitSha">Git HEAD SHA captured at <see cref="TaskService.CreateAsync"/> time, used by <c>/task review</c> as the base of the diff range that defines "the work attributable to this task". Null when the home_cwd wasn't a git repo at create time, or for tasks created before <see cref="Migrations.Migration_002_AddBaseCommitSha"/>.</param>
public sealed record TaskRecord(
    int Id,
    string HomeCwd,
    string Title,
    long CreatedAtUnixMs,
    long LastActiveAtUnixMs,
    TaskItemStatus Status,
    int? ParentTaskId,
    string? BaseCommitSha = null);
