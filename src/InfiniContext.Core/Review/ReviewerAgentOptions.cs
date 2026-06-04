using InfiniContext.Core.Configuration;

namespace InfiniContext.Core.Review;

/// <summary>
/// Configuration for one <see cref="IReviewerAgentService"/> invocation. Single-shot by
/// design -- long-lived/reusable channels are explicitly out of v1 scope (each invocation gets a
/// fresh process to isolate reviewers from each other).
/// </summary>
public sealed record ReviewerAgentOptions
{
    /// <summary>
    /// Anthropic model id to spawn. Defaults to a Sonnet-class id; users override via
    /// <c>settings.review.reviewerModel</c>. The light-model default (Haiku-class) is
    /// deliberately not the default here -- the user's design requires reasoning, not
    /// classification.
    /// </summary>
    public string Model { get; init; } = KnownModels.Reviewer;

    /// <summary>Override the resolved <c>claude</c> executable. Mainly for tests pointing at FakeClaude.</summary>
    public string? ClaudeExecutable { get; init; }

    /// <summary>
    /// Working directory the child runs in. <c>null</c> resolves to
    /// <c>%USERPROFILE%/.infinicontext/review-cwd/</c> (auto-created on first use). The recipe
    /// requires a managed empty directory to avoid <c>CLAUDE.md</c> auto-discovery and
    /// <c>.mcp.json</c> auto-load.
    /// </summary>
    public string? ManagedCwd { get; init; }

    /// <summary>
    /// Hard cap on output tokens (<c>CLAUDE_CODE_MAX_OUTPUT_TOKENS</c>). Reviewers emit JSON
    /// finding lists -- much larger than the light-model 128-token cap. 32K is generous; the
    /// coordinator may need more.
    /// </summary>
    public int MaxOutputTokens { get; init; } = 32_768;

    /// <summary>
    /// Whether to allow extended thinking. Reviewers do real reasoning so the default is
    /// <c>true</c>; flip to <c>false</c> only for quick spot-checks or cost-tuning.
    /// </summary>
    public bool ThinkingEnabled { get; init; } = true;

    /// <summary>
    /// Wall-clock budget for one <see cref="IReviewerAgentService.RunOneShotAsync"/> call.
    /// 600s matches the design's <c>review.invocationTimeoutSeconds</c> default -- reviewers
    /// reading a few-hundred-line packet should finish well inside this.
    /// </summary>
    public int InvocationTimeoutSeconds { get; init; } = 600;

    /// <summary>
    /// Extra env vars merged into the spawned child's environment <em>after</em> the recipe vars.
    /// Test-only: production callers should leave this null. Lets tests inject e.g.
    /// <c>FAKE_CLAUDE_SCRIPT</c> without mutating the test process's global env.
    /// </summary>
    public IReadOnlyDictionary<string, string>? ExtraEnv { get; init; }
}
