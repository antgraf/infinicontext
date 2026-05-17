namespace InfiniContext.Core.Review;

/// <summary>
/// Everything <see cref="ReviewService"/> needs to run a review. Built by the CLI handler
/// (step 9) from <c>TaskService.GetAsync</c>, <see cref="ScopeResolver"/>, and on-disk reads of
/// <c>CLAUDE.md</c> / <c>docs/.../decisions.md</c>. Keeping the orchestrator independent of
/// TaskService + ScopeResolver makes step 6 unit-testable without scaffolding the whole task
/// subsystem.
/// </summary>
/// <param name="TaskId">Task being reviewed.</param>
/// <param name="TaskTitle">Task title (for prompt context + report header).</param>
/// <param name="RepoRoot">Task's <c>home_cwd</c> тАФ the repo root the executor will edit.</param>
/// <param name="Scope">Pre-resolved review scope: file list + excerpts + exclusions.</param>
/// <param name="DescriptionMd">Contents of <c>description.md</c> from the task folder (empty if missing).</param>
/// <param name="ProgressMd">Contents of <c>progress.md</c> (empty if missing).</param>
/// <param name="IndexMd">Contents of <c>index.md</c> (empty if missing).</param>
/// <param name="ClaudeMd">Contents of repo-root <c>CLAUDE.md</c> (empty if missing).</param>
/// <param name="DecisionsMd">Contents of a project decisions doc (empty if missing).</param>
public sealed record ReviewRequest(
    int TaskId,
    string TaskTitle,
    string RepoRoot,
    ReviewScope Scope,
    string DescriptionMd,
    string ProgressMd,
    string IndexMd,
    string ClaudeMd,
    string DecisionsMd);

/// <summary>Runtime knobs for a single review run. Values mirror the design's settings defaults.</summary>
public sealed record ReviewOptions
{
    /// <summary>Default ctor with the design's defaults.</summary>
    public ReviewOptions() { }

    /// <summary>
    /// When true (default), <see cref="Disposition.AutoApply"/> findings are handed to the
    /// <see cref="IAutoApplyExecutor"/>. When false, every auto_apply gets demoted to
    /// <see cref="Disposition.NeedsUser"/> with a note (the report becomes "advisory only").
    /// </summary>
    public bool AutoApplyEnabled { get; init; } = true;
}
