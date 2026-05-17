namespace InfiniContext.Core.Review;

/// <summary>
/// Applies <see cref="Disposition.AutoApply"/> findings to source files and gates them through
/// build + test before declaring them "applied". Implemented in step 7
/// (<c>AutoApplyExecutor</c>); the orchestrator (<see cref="ReviewService"/>) depends only on
/// this interface so step 6 can ship without step 7.
/// </summary>
public interface IAutoApplyExecutor
{
    /// <summary>
    /// Apply each <paramref name="findings"/> finding's <see cref="ConsolidatedFinding.SuggestedFix"/>
    /// to disk under <paramref name="repoRoot"/>, then run a build+test gate. Findings that
    /// applied cleanly and survived the gate appear in <see cref="AutoApplyResult.Applied"/>
    /// (disposition unchanged); findings that couldn't apply or that the gate failed reverted
    /// are in <see cref="AutoApplyResult.Demoted"/> with <see cref="Disposition.NeedsUser"/>.
    /// </summary>
    Task<AutoApplyResult> ApplyAsync(
        string repoRoot,
        IReadOnlyList<ConsolidatedFinding> findings,
        CancellationToken ct);
}

/// <summary>Outcome of an <see cref="IAutoApplyExecutor.ApplyAsync"/> call.</summary>
/// <param name="Applied">Findings that applied cleanly and survived the build+test gate. Disposition remains <see cref="Disposition.AutoApply"/>.</param>
/// <param name="Demoted">Findings that could not apply, or applied but the gate reverted them. Disposition is flipped to <see cref="Disposition.NeedsUser"/>; <see cref="ConsolidatedFinding.ConsolidatorNote"/> carries the reason.</param>
/// <param name="BuildTestGatePassed">False when the post-batch build or test gate failed and the batch was reverted.</param>
/// <param name="GateReversionReason">Non-null only when <paramref name="BuildTestGatePassed"/> is false. Human-readable summary of which gate failed.</param>
public sealed record AutoApplyResult(
    IReadOnlyList<ConsolidatedFinding> Applied,
    IReadOnlyList<ConsolidatedFinding> Demoted,
    bool BuildTestGatePassed,
    string? GateReversionReason);
