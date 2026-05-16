namespace InfiniContext.Core.Tasks;

/// <summary>
/// Composite returned by <see cref="TaskService.GetAsync"/>: the SQLite row plus the artifacts
/// currently on disk. The artifacts list is a snapshot at call time; it is not kept in sync.
/// </summary>
public sealed record TaskDetails(TaskRecord Record, IReadOnlyList<ArtifactInfo> Artifacts);
