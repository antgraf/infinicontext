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

    /// <summary>The 1M-context variant <c>claude</c> reports for the reviewer model (Claude Code
    /// offers Sonnet as separate 200K/1M entries, so <c>system/init</c> can carry the suffixed id).
    /// Used as a context-window map key, exactly like <see cref="MainLargeContext"/>.</summary>
    public const string ReviewerLargeContext = Reviewer + "[1m]";

    /// <summary>Fable 5 вЂ” a newer, large-context model offered as a selectable option in the GUI
    /// model dropdowns (task 0016). NOT the shipped default (<see cref="Main"/> stays Opus 4.8 вЂ”
    /// switching the default away is explicitly out of scope). Specifics flagged for awareness:
    /// 1M context, ~2x Opus 4.8 cost, new tokenizer (~30% more tokens per the 0015 discussion).</summary>
    public const string Fable = "claude-fable-5";

    /// <summary>
    /// Curated, static list of models the operator can pick from in the GUI (per-session dropdown
    /// and the task-creation default). Single source of truth for the selectable set: adding a model
    /// is one edit here. <see cref="ModelsOptions.Available"/> defaults to this list and is the
    /// operator-facing override surface (settings.json / settings.user.json). Each entry's
    /// <see cref="ModelCatalogEntry.ContextWindowTokens"/> mirrors the <see cref="TasksOptions.ModelContextWindows"/>
    /// fallback map so context-pressure thresholds resolve correctly for every offered model.
    /// </summary>
    public static IReadOnlyList<ModelCatalogEntry> Catalog { get; } = new[]
    {
        new ModelCatalogEntry(Main, "Opus 4.8", 1_000_000),
        new ModelCatalogEntry(Reviewer, "Sonnet 4.6", 1_000_000),
        new ModelCatalogEntry(Light, "Haiku 4.5", 200_000),
        new ModelCatalogEntry(Fable, "Fable 5", 1_000_000),
    };
}
