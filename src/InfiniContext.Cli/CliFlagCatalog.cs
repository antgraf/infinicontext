namespace InfiniContext.Cli;

/// <summary>
/// Single source of truth for the <c>infinicontext</c> CLI flags. <see cref="PrintUsageText"/>
/// renders this catalog; a drift test in <c>tests/</c> asserts every <c>case</c> label in
/// <see cref="RunArgs.TryParse"/> has a matching entry here. Adding or renaming a flag means
/// updating one place.
/// </summary>
internal static class CliFlagCatalog
{
    public sealed record Entry(
        string Name,        // "--resume" or "<cwd>" or "--"
        string? ArgSpec,    // "<session_id>" or null for boolean / positional / separator
        string Summary,     // shown in the usage table
        string Details);    // longer prose, used by future docs generators

    public static IReadOnlyList<Entry> All { get; } = new[]
    {
        new Entry("<cwd>", null,
            "Working directory for the spawned claude. Optional; defaults to current dir.",
            "Positional argument. Defaults to the current directory if omitted. Must exist; the host validates this before spawning claude."),
        new Entry("--resume", "<session_id>",
            "Resume a prior session by id (UUID).",
            "Looks up ~/.claude/projects/<encoded-cwd>/<session_id>.jsonl. The host does not mirror Claude's session store; the file must exist."),
        new Entry("--model", "<name>",
            "Override the model used for this session.",
            "Overrides session.model from settings. Use Anthropic model ids, e.g. claude-opus-4-8 or claude-sonnet-4-6."),
        new Entry("--include-partial-messages", null,
            "Stream stream_event deltas instead of per-turn batched messages.",
            "Default mode is per-turn batched (one assistant event per turn). Only set this if you actually consume deltas — adds noise to the log otherwise."),
        new Entry("--claude-exe", "<path>",
            "Override the path to the claude executable.",
            "Default discovers claude via PATH. Use this when you want to point at a specific build."),
        new Entry("--settings", "<path>",
            "Bypass settings auto-discovery and load this exact file.",
            "Skips the settings.user.json overlay. INFINICONTEXT__* env-var overrides still apply on top of the loaded file."),
        new Entry("--approval-mode", "<ask|deny|allow|auto>",
            "Tool-approval policy for this session.",
            "ask = prompt the user; deny = reject all; allow = approve all; auto = light-model safety review (requires an Anthropic API key)."),
        new Entry("--", null,
            "Forward everything after this verbatim to claude.",
            "Use for flags that InfiniContext does not recognise — they are passed through to the spawned claude unmodified."),
        new Entry("--help, -h, help", null,
            "Print this usage and exit.",
            "All three forms are equivalent. Handled before subcommand routing."),
    };

    /// <summary>
    /// Render the full usage text. Output lives in this class (not <c>Program.cs</c>) so the
    /// flag list and the usage banner are physically next to each other.
    /// </summary>
    public static string PrintUsageText()
    {
        var sb = new System.Text.StringBuilder();
        sb.Append("Usage: infinicontext [<cwd>] [options] [-- <args passed to claude>]\n");
        sb.Append("       infinicontext run [<cwd>] [options]      # explicit subcommand, equivalent\n");
        sb.Append('\n');
        sb.Append("Options:\n");

        var nameWidth = All.Max(e => RenderTrigger(e).Length);
        foreach (var entry in All)
        {
            var trigger = RenderTrigger(entry);
            sb.Append("  ");
            sb.Append(trigger.PadRight(nameWidth));
            sb.Append("  ");
            sb.Append(entry.Summary);
            sb.Append('\n');
        }

        sb.Append('\n');
        sb.Append("For REPL slash-commands, type /help inside the REPL.\n");
        sb.Append('\n');
        sb.Append("Settings: settings.json (committed defaults) + settings.user.json (gitignored\n");
        sb.Append("overrides) auto-discovered in the working directory. INFINICONTEXT__SECTION__KEY\n");
        sb.Append("env vars overlay both. Use --settings to load an exact file.\n");
        return sb.ToString();
    }

    private static string RenderTrigger(Entry e) =>
        e.ArgSpec is null ? e.Name : $"{e.Name} {e.ArgSpec}";
}
