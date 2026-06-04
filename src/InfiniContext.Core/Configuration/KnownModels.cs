namespace InfiniContext.Core.Configuration;

/// <summary>
/// Single source of truth for the Anthropic model ids InfiniContext spawns. Model ids used to be
/// inlined as string literals across <see cref="InfiniContextSettings"/>, the reviewer options, the
/// context-window fallback map and <c>settings.json</c>; bumping a model meant hunting them all
/// down. Centralizing them here makes a future upgrade a one-line edit in code (the committed
/// <c>settings.json</c> mirrors these values and is the operator-facing override surface).
/// </summary>
public static class KnownModels
{
    /// <summary>The host's main session model. Spawned by every <c>claude</c> child unless an
    /// explicit <c>session.model</c> setting or <c>--model</c> CLI flag overrides it.</summary>
    public const string Main = "claude-opus-4-8";

    /// <summary>The 1M-context beta variant <c>claude</c> reports for the main model (e.g. in
    /// <c>system/init</c> and <c>result.modelUsage</c>). Used as a context-window map key.</summary>
    public const string MainLargeContext = Main + "[1m]";

    /// <summary>Cheap model for <see cref="LightModel.ILightModelService"/> one-shot tasks
    /// (auto-approval safety review, title generation).</summary>
    public const string Light = "claude-haiku-4-5";

    /// <summary>Default model for <c>/task review</c> reviewer/coordinator agents. A Sonnet-class
    /// id: reviewers reason rather than classify, so the Haiku light model is deliberately not
    /// reused here.</summary>
    public const string Reviewer = "claude-sonnet-4-6";
}
