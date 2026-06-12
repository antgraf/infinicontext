namespace InfiniContext.Core.Configuration;

/// <summary>
/// One selectable model in the host's curated catalog: the Anthropic model <see cref="Id"/> the
/// <c>claude</c> child is spawned with, a human-readable <see cref="DisplayName"/> for the GUI
/// dropdowns, and the <see cref="ContextWindowTokens"/> used to pick the context-pressure nudge
/// thresholds (<see cref="Tasks.ContextPressureMonitor"/> selects the pair from the window it
/// resolves via <see cref="TasksOptions.ModelContextWindows"/>, which the catalog seeds).
///
/// <para>The catalog is static-first by design (see task 0016 / the 0015 centralization win): a new
/// model is one edit to <see cref="KnownModels.Catalog"/> rather than an HTTP call to the Anthropic
/// Models API вЂ” the host spawns the <c>claude</c> CLI and has no API-key/HTTP path. A live
/// Models-API fetch is a possible follow-on, not a gate.</para>
/// </summary>
/// <param name="Id">Anthropic model id passed to <c>claude --model</c> (e.g. <c>claude-opus-4-8</c>).</param>
/// <param name="DisplayName">Short label shown in the GUI dropdowns (e.g. <c>Opus 4.8</c>).</param>
/// <param name="ContextWindowTokens">Total context window in tokens; seeds <see cref="TasksOptions.ModelContextWindows"/>.</param>
public sealed record ModelCatalogEntry(string Id, string DisplayName, int ContextWindowTokens);
