using InfiniContext.Core.Configuration;
using InfiniContext.Core.LightModel;
using InfiniContext.Core.Supervision;
using Serilog;

namespace InfiniContext.Core.Review;

/// <summary>
/// Default <see cref="IReviewerAgentService"/> implementation. Spawns one <c>claude --print</c>
/// child per call using the same recipe as <see cref="CliLightModelInvocation"/> (no tools, no
/// MCP, no slash-commands, isolated cwd) but driven by the user's full reasoning model rather
/// than the Haiku-class light-model default.
///
/// <para>
/// Reuses <see cref="CliLightModelChild"/> as the wire-layer тАФ the spawn/turn-drive/dispose
/// loop is identical; the only meaningful difference between a light-model invocation and a
/// reviewer invocation is which <c>--model</c> id flows through, so duplicating the spawn loop
/// would be pure churn. The architectural distinction the user called out ("agents must not be
/// light models") lives in <em>what model</em> gets passed, not in <em>which spawn machinery</em>.
/// </para>
/// </summary>
public sealed class CliReviewerAgentService : IReviewerAgentService
{
    private readonly ReviewerAgentOptions _options;
    private readonly IProcessSupervisor? _supervisor;
    private readonly ILogger _logger;

    public CliReviewerAgentService(
        ReviewerAgentOptions options,
        IProcessSupervisor? supervisor = null,
        ILogger? logger = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        if (string.IsNullOrWhiteSpace(_options.Model))
            throw new ArgumentException("ReviewerAgentOptions.Model must be non-empty.", nameof(options));
        if (_options.MaxOutputTokens <= 0)
            throw new ArgumentException("ReviewerAgentOptions.MaxOutputTokens must be positive.", nameof(options));
        if (_options.InvocationTimeoutSeconds <= 0)
            throw new ArgumentException("ReviewerAgentOptions.InvocationTimeoutSeconds must be positive.", nameof(options));

        _supervisor = supervisor;
        _logger = logger ?? Log.ForContext<CliReviewerAgentService>();
    }

    public async Task<string> RunOneShotAsync(string systemPrompt, string userPayload, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(systemPrompt);
        ArgumentException.ThrowIfNullOrWhiteSpace(userPayload);

        var lightOptions = BuildLightModelOptions(_options);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(_options.InvocationTimeoutSeconds));

        _logger.Debug(
            "reviewer-agent: spawn model={Model} thinking={Thinking} maxOutputTokens={Max}",
            _options.Model, _options.ThinkingEnabled, _options.MaxOutputTokens);

        await using var child = CliLightModelChild.Spawn(
            lightOptions,
            thinking: _options.ThinkingEnabled,
            systemPrompt: systemPrompt,
            supervisor: _supervisor,
            logger: _logger);

        return await child.RunTurnAsync(userPayload, cts.Token).ConfigureAwait(false);
    }

    /// <summary>
    /// Translate the public <see cref="ReviewerAgentOptions"/> into the
    /// <see cref="LightModelOptions"/> shape that <see cref="CliLightModelChild"/> consumes.
    /// Exposed as <c>internal</c> for tests тАФ verifying the translation is much cheaper than
    /// spawning a real child.
    /// </summary>
    internal static LightModelOptions BuildLightModelOptions(ReviewerAgentOptions opts) => new()
    {
        Id = opts.Model,
        Cli = new CliLightModelOptions
        {
            Mode = CliLightModelMode.SingleShot,
            MaxOutputTokens = opts.MaxOutputTokens,
            ThinkingMaxOutputTokens = opts.MaxOutputTokens,
            ClaudeExecutable = opts.ClaudeExecutable,
            ManagedCwd = opts.ManagedCwd ?? DefaultManagedCwd(),
            ExtraEnv = opts.ExtraEnv,
        },
    };

    private static string DefaultManagedCwd() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".infinicontext",
        "review-cwd");
}
