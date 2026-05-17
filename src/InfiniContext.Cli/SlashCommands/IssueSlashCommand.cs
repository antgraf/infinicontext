using InfiniContext.Cli.Issues;

namespace InfiniContext.Cli.SlashCommands;

/// <summary>
/// Facade slash-command for the <c>/issue</c> sub-grammar (issues.md feature, v1). The actual
/// parser lives in <see cref="IssueCommand"/> and the formatter in <see cref="IssueCommandHandler"/>;
/// this class only routes input.
///
/// <para>
/// The aliased form <c>/task issue ...</c> is dispatched by <see cref="TaskSlashCommand"/>
/// directly into the same handler - both surfaces share one parser + handler so behavior is
/// identical.
/// </para>
/// </summary>
internal sealed class IssueSlashCommand : ISlashCommand
{
    public string Name => "/issue";
    public IReadOnlyList<string> Aliases { get; } = Array.Empty<string>();
    public string Summary => "Log out-of-scope findings to the focused task's issues.md. /help issue for the full sub-grammar.";
    public string Details => IssueCommandHandler.HelpText.TrimEnd();

    public async Task ExecuteAsync(SlashCommandContext ctx, string argLine, CancellationToken ct)
    {
        if (ctx.IssueHandler is null)
        {
            ctx.Write("[issue] subsystem is unavailable (see startup warnings).\n");
            return;
        }

        // IssueCommand.TryParse accepts both "/issue <args>" and the bare "<args>" form. Pass the
        // arg portion with the canonical "/issue" prefix so the parser sees the same shape it
        // gets in tests.
        var raw = string.IsNullOrWhiteSpace(argLine) ? "/issue" : "/issue " + argLine.Trim();

        if (!IssueCommand.TryParse(raw, out var cmd, out var error))
        {
            ctx.Write($"[issue] {error}\n");
            return;
        }

        await ctx.IssueHandler.HandleAsync(cmd, ctx.ActiveTaskId, ct).ConfigureAwait(false);
    }
}
