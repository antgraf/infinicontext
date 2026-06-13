using InfiniContext.Core.Configuration;
using InfiniContext.Core.Supervision;
using Serilog;

namespace InfiniContext.Core.Review;

/// <summary>Bundle of the review-subsystem objects <c>/task review</c> needs.</summary>
public sealed record ReviewStack(ReviewService ReviewService, ScopeResolver ScopeResolver);

/// <summary>
/// M20 (audit <c>docs/architecture-review-2026-06-11.md</c>): the single Core-owned builder for the
/// <c>/task review</c> stack. The CLI (<c>Program.cs</c>) and the GUI (<c>SessionHost</c>) used to
/// keep byte-identical copies of this wiring — the GUI's was even self-described as "ported verbatim
/// from the CLI's Program.cs" — which violates the project's no-duplication rule and had already
/// started to drift. Both hosts now call <see cref="Build"/>.
///
/// <para>
/// Throws on construction failure; each host wraps the call so the tasks subsystem degrades to
/// "review unavailable" rather than crashing.
/// </para>
/// </summary>
public static class ReviewStackFactory
{
    public static ReviewStack Build(InfiniContextSettings settings, IProcessSupervisor? supervisor, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(logger);

        var reviewSettings = settings.Review;

        // Reviewer agent uses review.reviewerModel (null → the user's main model id); coordinator
        // uses review.coordinatorModel (null → the reviewer model). When both null all six agents
        // share the main model.
        var reviewerModelId = string.IsNullOrWhiteSpace(reviewSettings.ReviewerModel)
            ? settings.Models.Main.Id
            : reviewSettings.ReviewerModel!;
        var coordinatorModelId = string.IsNullOrWhiteSpace(reviewSettings.CoordinatorModel)
            ? reviewerModelId
            : reviewSettings.CoordinatorModel!;

        var reviewerAgent = new CliReviewerAgentService(
            new ReviewerAgentOptions { Model = reviewerModelId, InvocationTimeoutSeconds = reviewSettings.InvocationTimeoutSeconds },
            supervisor: supervisor, logger: logger);

        // Reuse the same instance when the model ids match: avoids spinning two identical CLI
        // children's worth of resources for the common case (no override configured).
        var coordinatorAgent = string.Equals(coordinatorModelId, reviewerModelId, StringComparison.Ordinal)
            ? reviewerAgent
            : new CliReviewerAgentService(
                new ReviewerAgentOptions { Model = coordinatorModelId, InvocationTimeoutSeconds = reviewSettings.InvocationTimeoutSeconds },
                supervisor: supervisor, logger: logger);

        var autoApplyExecutor = new AutoApplyExecutor(
            new ProcessBuildTestRunner(supervisor: supervisor),
            new AutoApplyOptions
            {
                ConfidenceFloor = reviewSettings.ConfidenceFloor,
                MaxLinesPerFinding = reviewSettings.AutoApplyMaxLinesPerFinding,
                MaxFilesPerFinding = reviewSettings.AutoApplyMaxFilesPerFinding,
                SkipBuildTestGate = reviewSettings.SkipBuildTestGate,
            },
            logger: logger);

        var reviewService = new ReviewService(reviewerAgent, coordinatorAgent, autoApplyExecutor, logger: logger);
        var scopeResolver = new ScopeResolver(new ProcessGitRunner(supervisor: supervisor), byteCap: reviewSettings.PerFileByteCap);

        return new ReviewStack(reviewService, scopeResolver);
    }
}
