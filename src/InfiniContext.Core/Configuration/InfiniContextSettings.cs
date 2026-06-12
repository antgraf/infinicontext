using InfiniContext.Core.LightModel;

namespace InfiniContext.Core.Configuration;

/// <summary>
/// Root settings record for InfiniContext. Bound from <c>settings.json</c> (defaults, committed) +
/// <c>settings.user.json</c> (user overrides, gitignored) + the <c>INFINICONTEXT__</c>-prefixed env
/// var overlay, in that precedence order. <see cref="Defaults"/> exposes a compile-time instance
/// for tests and library types that need to run without a host-loaded settings file.
/// </summary>
public sealed record InfiniContextSettings
{
    public static InfiniContextSettings Defaults { get; } = new();

    public LoggingOptions Logging { get; init; } = new();
    public SessionOptions Session { get; init; } = new();
    public ApprovalOptions Approval { get; init; } = new();
    public McpOptions Mcp { get; init; } = new();
    public VersioningOptions Versioning { get; init; } = new();
    public ModelsOptions Models { get; init; } = new();
    public CosmeticsOptions Cosmetics { get; init; } = new();
    public TasksOptions Tasks { get; init; } = new();
    public ReviewSettings Review { get; init; } = new();
}

public sealed record LoggingOptions
{
    public string Level { get; init; } = "Debug";
    public string Directory { get; init; } = "./logs";
    public int FlushIntervalMs { get; init; } = 250;
    public int MaxFieldBytes { get; init; } = 4096;
    public int RxTruncationBytes { get; init; } = 2048;
}

public sealed record SessionOptions
{
    public int EventChannelCapacity { get; init; } = 1024;
    public int StderrRingCapacity { get; init; } = 200;
    public int StdoutDrainTimeoutSeconds { get; init; } = 2;
    public int ExitDrainWaitSeconds { get; init; } = 2;
    public int InterruptTimeoutSeconds { get; init; } = 5;
    public string? Model { get; init; }
    public bool IncludePartialMessages { get; init; }
    public string[] ExtraArgs { get; init; } = Array.Empty<string>();
}

public sealed record ApprovalOptions
{
    public int TimeoutSeconds { get; init; } = 60;

    /// <summary>
    /// Default decision policy for tool-permission requests that fall through Claude Code's own
    /// allow/deny lists and reach our sidecar:
    /// <list type="bullet">
    ///   <item><c>Ask</c> — prompt the user (current Stage 1 behavior).</item>
    ///   <item><c>Deny</c> — auto-deny everything; useful for sandbox/CI runs.</item>
    ///   <item><c>Allow</c> — auto-allow everything. <b>Dangerous.</b> Refused as a persisted
    ///   default (must be set via CLI or REPL for one session at a time).</item>
    ///   <item><c>Auto</c> — consult the light model (<see cref="ILightModelService"/>) for a
    ///   safety verdict; configurable fallback in <see cref="Auto"/>.</item>
    /// </list>
    /// </summary>
    public ApprovalMode Mode { get; init; } = ApprovalMode.Auto;

    public AutoApprovalOptions Auto { get; init; } = new();
}

public enum ApprovalMode
{
    Ask,
    Deny,
    Allow,
    Auto,
}

public sealed record AutoApprovalOptions
{
    /// <summary>
    /// Behavior when the light model returns an allow verdict but with low confidence.
    /// <c>Deny</c> (default) — fail closed. <c>FallbackToAsk</c> — surface the prompt to the user.
    /// </summary>
    public LowConfidenceFallback OnLowConfidence { get; init; } = LowConfidenceFallback.Deny;

    /// <summary>
    /// Wall-clock deadline for the light-model API call, in seconds. On timeout, behaves as if
    /// the model returned a low-confidence response (i.e. <see cref="OnLowConfidence"/> kicks in).
    /// Kept shorter than the user-prompt timeout so auto-mode stays responsive.
    /// </summary>
    public int TimeoutSeconds { get; init; } = 15;

    /// <summary>
    /// Effort tier for the safety-review verdict. <c>Low</c> (default) — non-thinking, ~2.3 s p50,
    /// $0.0007/call; matches the §6 recipe. <c>High</c> — thinking enabled, ~9 s p50, ~$0.0074/call;
    /// catches more ambiguous cases. Same axis on both back ends: SDK passes the corresponding
    /// <c>thinking</c> parameter; CLI flips <c>CLAUDE_CODE_DISABLE_THINKING</c>.
    /// </summary>
    public ApprovalAutoEffort Effort { get; init; } = ApprovalAutoEffort.Low;

    /// <summary>
    /// Lifecycle for the CLI light-model back end specifically when used by auto-mode. Default:
    /// <see cref="CliLightModelMode.SingleShot"/>. Safety review wants real isolation between
    /// unrelated tool calls, which only single-shot's fresh-process-per-call provides — long-lived
    /// uses <c>/clear</c> for cosmetic-only isolation (see I8) and is wrong for the gate. This
    /// is intentionally a separate knob from <see cref="CliLightModelOptions.Mode"/>, which
    /// controls the default for <em>general</em> consumers (title generation, classification,
    /// etc., where leakage is fine and amortized startup is wanted).
    /// Ignored when the SDK back end is in use (<c>ANTHROPIC_API_KEY</c> set).
    /// </summary>
    public CliLightModelMode Mode { get; init; } = CliLightModelMode.SingleShot;
}

public enum LowConfidenceFallback
{
    Deny,
    FallbackToAsk,
}

public enum ApprovalAutoEffort
{
    Low,
    High,
}

public sealed record ModelsOptions
{
    public MainModelOptions Main { get; init; } = new();
    public LightModelOptions Light { get; init; } = new();

    /// <summary>
    /// Curated set of models the operator can pick from in the GUI (per-session dropdown and the
    /// task-creation default). Defaults to <see cref="KnownModels.Catalog"/>; overridable in
    /// <c>settings.json</c> / <c>settings.user.json</c>. The GUI binds to this list, not to
    /// <see cref="KnownModels"/> directly, so an operator can curate the offered set without a code
    /// change. Each entry's <see cref="ModelCatalogEntry.ContextWindowTokens"/> should have a
    /// matching <see cref="TasksOptions.ModelContextWindows"/> key so context-pressure thresholds
    /// resolve correctly; a missing key falls back to default thresholds (not a hard error).
    /// </summary>
    public IReadOnlyList<ModelCatalogEntry> Available { get; init; } = KnownModels.Catalog;
}

public sealed record MainModelOptions
{
    /// <summary>
    /// Identifier of the host's "main" model. This is the model the spawned <c>claude</c> child
    /// runs when neither the <c>--model</c> CLI flag nor <c>session.model</c> is set: both the CLI
    /// host (<c>Program.cs</c>) and the GUI (<c>SessionHost.cs</c>) fall back to this id. Bump
    /// <see cref="KnownModels.Main"/> to upgrade the whole app in one edit.
    /// </summary>
    public string Id { get; init; } = KnownModels.Main;
}

public sealed record LightModelOptions
{
    /// <summary>Anthropic model id used by <see cref="ILightModelService"/>. Default: Haiku 4.5.</summary>
    public string Id { get; init; } = KnownModels.Light;

    /// <summary>Cap on output tokens per call. Light tasks rarely need more than a few hundred.</summary>
    public int MaxTokens { get; init; } = 1024;

    /// <summary>Wall-clock deadline for one light-model HTTP call.</summary>
    public int TimeoutSeconds { get; init; } = 30;

    /// <summary>
    /// Knobs for the CLI back end (<c>SingleShotCliLightModelService</c> /
    /// <c>LongLivedCliLightModelService</c>). The SDK back end ignores these. See
    /// <see cref="CliLightModelOptions"/>.
    /// </summary>
    public CliLightModelOptions Cli { get; init; } = new();
}

/// <summary>
/// Configuration for the CLI-backed light-model services (the OAuth-only fallback path).
/// Encodes the recipe codified at <c>docs/research/claude-cli-light-model.md</c> §6.
/// <c>Thinking</c> is intentionally NOT here — it is selected per-instance by the consumer
/// (auto-mode reads it from <see cref="AutoApprovalOptions.Effort"/>; other consumers may pin
/// it independently). Settings carry policy defaults; the per-instance choice is a ctor arg.
/// </summary>
public sealed record CliLightModelOptions
{
    /// <summary>
    /// Default lifecycle for general (non-auto-mode) CLI light-model consumers. <c>LongLived</c>
    /// (default) amortizes process startup across many calls and uses <c>/clear</c> between turns
    /// for cosmetic isolation — the right shape for title generation, classification, summarization
    /// and other workloads where leakage is harmless. <c>SingleShot</c> spawns a fresh process per
    /// call for real isolation. Auto-mode safety review reads its own
    /// <see cref="AutoApprovalOptions.Mode"/>; this setting does not affect it.
    /// </summary>
    public CliLightModelMode Mode { get; init; } = CliLightModelMode.LongLived;

    /// <summary>
    /// Hard cap on output tokens passed to the child via <c>CLAUDE_CODE_MAX_OUTPUT_TOKENS</c>
    /// when <c>thinking=false</c>. 128 matches the safety-verdict JSON size.
    /// </summary>
    public int MaxOutputTokens { get; init; } = 128;

    /// <summary>
    /// Hard cap on output tokens when <c>thinking=true</c> — the model needs room to think
    /// before emitting JSON. 1024 is a placeholder; the right ceiling is one of D9's open
    /// sub-questions and needs a benchmark pass to confirm.
    /// </summary>
    public int ThinkingMaxOutputTokens { get; init; } = 1024;

    /// <summary>
    /// Override the resolved <c>claude</c> executable. Mainly for tests pointing at FakeClaude.
    /// </summary>
    public string? ClaudeExecutable { get; init; }

    /// <summary>
    /// Working directory the child runs in. <c>null</c> resolves to
    /// <c>%USERPROFILE%/.infinicontext/light-cwd/</c> (auto-created). The recipe requires a managed
    /// empty directory to avoid <c>CLAUDE.md</c> auto-discovery and <c>.mcp.json</c> auto-load.
    /// </summary>
    public string? ManagedCwd { get; init; }

    /// <summary>
    /// Extra env vars merged into the spawned child's environment <em>after</em> the recipe vars.
    /// Test-only: production callers should leave this null. Lets tests inject a per-spawn knob
    /// (e.g. <c>FAKE_CLAUDE_SCRIPT</c>) without mutating the test process's global env, which
    /// otherwise races between parallel test classes.
    /// </summary>
    public IReadOnlyDictionary<string, string>? ExtraEnv { get; init; }
}

public enum CliLightModelMode
{
    SingleShot,
    LongLived,
}

public sealed record McpOptions
{
    public int SidecarConnectTimeoutSeconds { get; init; } = 10;
    public int ApprovalChannelDisposeTimeoutSeconds { get; init; } = 2;
}

public sealed record VersioningOptions
{
    public string MinClaudeVersion { get; init; } = "2.0.0";
    public string TestedAgainstVersion { get; init; } = "2.1.112";
}

public sealed record CosmeticsOptions
{
    public ReplCosmetics Repl { get; init; } = new();
    public ApprovalCosmetics Approval { get; init; } = new();
}

public sealed record ReplCosmetics
{
    public string Prompt { get; init; } = "> ";
}

public sealed record ApprovalCosmetics
{
    public string TimeoutMessage { get; init; } = "[approval] timeout \u2192 deny";
    public string ApprovedMessage { get; init; } = "[approval] allowed";
    public string DeniedMessage { get; init; } = "[approval] denied";
}

public sealed record TasksOptions
{
    /// <summary>
    /// Override for the SQLite registry path. <c>null</c> resolves to
    /// <c>%USERPROFILE%/.infinicontext/infinicontext.db</c>. Pass <c>":memory:"</c> for an in-memory
    /// store (test-only). Additional Stage 2A settings land in their respective phases.
    /// </summary>
    public string? DbPath { get; init; }

    /// <summary>
    /// Stage 2A Phase 7: how long a granted task-artifact lock stays valid before auto-expiry.
    /// Short TTL caps the blast radius of a crashed session that didn't release its lock.
    /// </summary>
    public int LockTtlSeconds { get; init; } = 30;

    /// <summary>
    /// Stage 2A Phase 7: how long a competing session waits for a contended lock before giving
    /// up and reporting a timeout error to the agent.
    /// </summary>
    public int LockMaxWaitSeconds { get; init; } = 60;

    /// <summary>
    /// Stage 2A Phase 8: percentage of the model's context window at which the host injects a
    /// polite "flush at next stopping point" nudge. Fires once per session.
    /// </summary>
    public int SoftThresholdPct { get; init; } = 60;

    /// <summary>
    /// Stage 2A Phase 8: percentage of the model's context window at which the host injects a
    /// firm "flush now, no new work" nudge. Fires once per session, only after — or instead of —
    /// the soft nudge.
    /// </summary>
    public int HardThresholdPct { get; init; } = 85;

    /// <summary>
    /// Task 0012 finding #2: tighter soft threshold (in percent) for models with a large context
    /// window (>= <see cref="LargeContextWindowTokens"/>). 1M-token windows have many more
    /// absolute tokens at the same percentage as a 200k window, so a 60% soft nudge on 1M is
    /// far too late - the agent has burned 600k tokens before the nudge. Default 45 keeps the
    /// flush window short enough that the agent can still produce a useful summary.
    /// </summary>
    public int SoftThresholdPctLargeContext { get; init; } = 45;

    /// <summary>
    /// Task 0012 finding #2: tighter hard threshold for large-context models. See
    /// <see cref="SoftThresholdPctLargeContext"/>. Default 65 keeps the operator's "you should
    /// have flushed by now" headroom proportionally similar to the small-context 85.
    /// </summary>
    public int HardThresholdPctLargeContext { get; init; } = 65;

    /// <summary>
    /// Task 0012 finding #2: the cutoff (in tokens) above which a model is treated as
    /// "large context" and uses <see cref="SoftThresholdPctLargeContext"/> /
    /// <see cref="HardThresholdPctLargeContext"/> instead of the default
    /// <see cref="SoftThresholdPct"/> / <see cref="HardThresholdPct"/>. Default 1,000,000
    /// matches Anthropic's 1M-beta context window. Set to 0 to disable the large-context
    /// override (every model uses the default pair).
    /// </summary>
    public int LargeContextWindowTokens { get; init; } = 1_000_000;

    /// <summary>
    /// Stage 2A Phase 8: fallback context-window map keyed by model id. Used only when claude's
    /// own <c>result.modelUsage[&lt;model&gt;].contextWindow</c> field is missing. Defaults
    /// cover the current Anthropic line; users can override per-model in
    /// <c>settings.user.json</c>.
    /// </summary>
    public IReadOnlyDictionary<string, int> ModelContextWindows { get; init; } =
        new Dictionary<string, int>(StringComparer.Ordinal)
        {
            [KnownModels.Reviewer] = 1_000_000,
            [KnownModels.ReviewerLargeContext] = 1_000_000,
            [KnownModels.Main] = 1_000_000,
            [KnownModels.MainLargeContext] = 1_000_000,
            [KnownModels.Light] = 200_000,
            [KnownModels.Fable] = 1_000_000,
        };

    /// <summary>
    /// Resolve <see cref="DbPath"/> against the per-user default. Pure function — does no I/O.
    /// </summary>
    public string ResolvedDbPath()
    {
        if (!string.IsNullOrWhiteSpace(DbPath)) return DbPath;
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".infinicontext",
            "infinicontext.db");
    }
}

/// <summary>
/// Settings for the <c>/task review</c> multi-agent review command (the
/// <see cref="InfiniContext.Core.Review"/> subsystem). Defaults match the design's
/// <c>review-feature-design.md</c> "Settings additions" section.
///
/// <para>
/// Named <c>ReviewSettings</c> rather than <c>ReviewOptions</c> to avoid colliding with
/// <see cref="InfiniContext.Core.Review.ReviewOptions"/>, the orchestrator's per-run knobs.
/// </para>
/// </summary>
public sealed record ReviewSettings
{
    /// <summary>
    /// Anthropic model id used by each reviewer agent. <c>null</c> inherits from
    /// <see cref="MainModelOptions.Id"/>, the user's main reasoning model. The light-model
    /// default (Haiku-class) is deliberately not the inherited value here; per the design,
    /// reviewers need reasoning, not classification.
    /// </summary>
    public string? ReviewerModel { get; init; }

    /// <summary>
    /// Model used by the coordinator agent. <c>null</c> = same as <see cref="ReviewerModel"/>
    /// (which in turn falls back to <see cref="MainModelOptions.Id"/> when null).
    /// </summary>
    public string? CoordinatorModel { get; init; }

    /// <summary>
    /// Auto-apply gate: a finding's <c>confidence</c> AND the consolidator's
    /// <c>disposition_confidence</c> must both meet or exceed this floor. v1 default 0.9
    /// matches the design.
    /// </summary>
    public double ConfidenceFloor { get; init; } = 0.9;

    /// <summary>
    /// Auto-apply size guard: per-finding max lines a fix may touch (sum of changed lines).
    /// The coordinator's prompt also enforces this; the host enforces it again defensively in
    /// step 7's executor. Default 50 lines.
    /// </summary>
    public int AutoApplyMaxLinesPerFinding { get; init; } = 50;

    /// <summary>
    /// Auto-apply size guard: per-finding max files a fix may touch. Default 3.
    /// </summary>
    public int AutoApplyMaxFilesPerFinding { get; init; } = 3;

    /// <summary>
    /// <see cref="InfiniContext.Core.Review.ScopeResolver"/> byte cap per file (in bytes). Files
    /// exceeding this go to <c>ExcludedFiles</c> with reason <c>"exceeds byte cap"</c>. Default
    /// 200 KB, large enough for normal source files and small enough to bound reviewer prompt
    /// size.
    /// </summary>
    public int PerFileByteCap { get; init; } = 204_800;

    /// <summary>
    /// Wall-clock budget for each reviewer / coordinator invocation. Default 600s (10 min):
    /// reviewers reading a few-hundred-line packet should finish well inside this; the budget
    /// exists to catch a wedged child process.
    /// </summary>
    public int InvocationTimeoutSeconds { get; init; } = 600;

    /// <summary>
    /// Test-only escape hatch to disable the post-auto-apply build + test gate. Production
    /// should always leave this <c>false</c>; the gate is the safety net that reverts edits
    /// when they break the suite.
    /// </summary>
    public bool SkipBuildTestGate { get; init; } = false;
}
