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
    /// <param name="allowedRelativePaths">
    /// The repo-root-relative paths the review actually covered (<see cref="ReviewScope.Files"/>).
    /// When non-null, any finding whose target file is not in this set is demoted to
    /// <see cref="Disposition.NeedsUser"/> instead of being written (C4 — a fix that creates or
    /// edits a file outside the reviewed set is a common prompt-injection payload). Null skips
    /// the scope constraint; the unconditional <c>.git/</c>/CI/build-file denylist still applies.
    /// </param>
    Task<AutoApplyResult> ApplyAsync(
        string repoRoot,
        IReadOnlyList<ConsolidatedFinding> findings,
        CancellationToken ct,
        IReadOnlyCollection<string>? allowedRelativePaths = null);
}

/// <summary>
/// Runtime knobs for <see cref="AutoApplyExecutor"/>, mirrored from <c>ReviewSettings</c>. The
/// executor enforces these as defense-in-depth: the coordinator prompt also instructs the agent
/// to respect the same thresholds, but the host re-checks on the way in so an over-eager
/// coordinator can't sneak an out-of-spec fix past the gate.
/// </summary>
public sealed record AutoApplyOptions
{
    /// <summary>
    /// Minimum value for both <see cref="ConsolidatedFinding.Confidence"/> and
    /// <see cref="ConsolidatedFinding.DispositionConfidence"/>. Findings below this floor are
    /// demoted to <see cref="Disposition.NeedsUser"/> with an explanatory note. Default 0.9.
    /// </summary>
    public double ConfidenceFloor { get; init; } = 0.9;

    /// <summary>
    /// Maximum lines a single finding's <c>SuggestedFix.OldText</c> may span. Hand the user a
    /// review entry instead of auto-applying a sweeping rewrite. Default 50.
    /// </summary>
    public int MaxLinesPerFinding { get; init; } = 50;

    /// <summary>
    /// Maximum files a single finding may touch. v1 fix kinds (Edit, ReplaceFile) only touch
    /// one file each; this knob is forward-looking for a future multi-file fix kind. Default 3.
    /// </summary>
    public int MaxFilesPerFinding { get; init; } = 3;

    /// <summary>
    /// Test-only escape hatch: when true, both the baseline gate and the post-apply build+test
    /// gate are skipped. Used by tests that exercise the apply path without spawning
    /// <c>dotnet</c>. Production should leave this <c>false</c>.
    /// </summary>
    public bool SkipBuildTestGate { get; init; } = false;
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
