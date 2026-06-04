using InfiniContext.Core.Issues;
using InfiniContext.Core.SlashCommands;

namespace InfiniContext.Core.SlashCommands;

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
public sealed class IssueSlashCommand : ISlashCommand
{
    public string Name => "/issue";
    public IReadOnlyList<string> Aliases { get; } = Array.Empty<string>();
    public string Summary => "Log out-of-scope findings to the focused task's issues.md. /help issue for the full sub-grammar.";
    public string Details => IssueCommandHandler.HelpText.TrimEnd();

    public async Task ExecuteAsync(SlashCommandContext ctx, string argLine, CancellationToken ct)
    {
        // Slice A: SlashCommandContext.IssueHandler is typed object? in Core; cast back to the
        // concrete Cli handler here. Slice C promotes the handler to Core and tightens this.
        if (ctx.IssueHandler is not IssueCommandHandler issueHandler)
        {
            ctx.Renderer.Write("[issue] subsystem is unavailable (see startup warnings).\n");
            return;
        }

        // IssueCommand.TryParse accepts both "/issue <args>" and the bare "<args>" form. Pass the
        // arg portion with the canonical "/issue" prefix so the parser sees the same shape it
        // gets in tests.
        var raw = string.IsNullOrWhiteSpace(argLine) ? "/issue" : "/issue " + argLine.Trim();

        if (!IssueCommand.TryParse(raw, out var cmd, out var error))
        {
            ctx.Renderer.Write($"[issue] {error}\n");
            return;
        }

        await issueHandler.HandleAsync(cmd, ctx.ActiveTaskId, ct).ConfigureAwait(false);
    }
}
