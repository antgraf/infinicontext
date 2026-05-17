using InfiniContext.Cli.Issues;
using InfiniContext.Cli.Tasks;
using InfiniContext.Core.Tasks;

namespace InfiniContext.Cli.SlashCommands;

/// <summary>
/// Façade slash-command for the <c>/task</c> sub-grammar. The actual parser lives in
/// <see cref="TaskCommand"/> and the formatter in <see cref="TaskCommandHandler"/>; this class
/// only routes input + intercepts <c>switch</c> (which has to drive the host's outer loop).
///
/// Class name is <c>TaskSlashCommand</c> rather than <c>TaskCommand</c> to avoid collision with
/// the existing parser type. See <c>decisions.md</c> D12.
/// </summary>
internal sealed class TaskSlashCommand : ISlashCommand
{
    public string Name => "/task";
    public IReadOnlyList<string> Aliases { get; } = new[] { "/tasks" };
    public string Summary => "Manage host-side tasks (create, list, fork, switch, …). /help task for the full sub-grammar.";
    public string Details => TaskCommandHandler.HelpText.TrimEnd();

    public async Task ExecuteAsync(SlashCommandContext ctx, string argLine, CancellationToken ct)
    {
        if (ctx.TaskHandler is null)
        {
            ctx.Write("[task] subsystem is unavailable (see startup warnings).\n");
            return;
        }

        // `/task issue ...` is the cross-namespace alias for `/issue ...` - route the remainder
        // straight into IssueCommand so both surfaces share one parser + handler. The user-facing
        // help is identical between the two forms.
        var trimmed = (argLine ?? string.Empty).TrimStart();
        if (trimmed.StartsWith("issue", StringComparison.OrdinalIgnoreCase)
            && (trimmed.Length == 5 || char.IsWhiteSpace(trimmed[5])))
        {
            if (ctx.IssueHandler is null)
            {
                ctx.Write("[issue] subsystem is unavailable (see startup warnings).\n");
                return;
            }
            var issueRaw = "/issue" + trimmed[5..];
            if (!IssueCommand.TryParse(issueRaw, out var issueCmd, out var issueErr))
            {
                ctx.Write($"[issue] {issueErr}\n");
                return;
            }
            await ctx.IssueHandler.HandleAsync(issueCmd, ctx.ActiveTaskId, ct).ConfigureAwait(false);
            return;
        }

        // TaskCommand.TryParse accepts both "/task <subcmd>" and the bare "<subcmd>" form.
        // We pass the arg portion directly so the parser doesn't see the "/task" prefix twice.
        var raw = string.IsNullOrWhiteSpace(argLine) ? "/task" : "/task " + argLine.Trim();

        if (!TaskCommand.TryParse(raw, out var cmd, out var error))
        {
            ctx.Write($"[task] {error}\n");
            return;
        }

        // /task switch is the one subcommand that needs to drive the outer host loop (re-spawn
        // a fresh ClaudeSession with a FirstUserMessage pointer). Intercept here so the rest of
        // the handler stays presentation-only.
        if (cmd is TaskCommand.Switch sw)
        {
            await TryStartTaskSwitchAsync(ctx, sw.Id, ct).ConfigureAwait(false);
            return;
        }

        await ctx.TaskHandler.HandleAsync(cmd, ct).ConfigureAwait(false);
    }

    private static async Task TryStartTaskSwitchAsync(SlashCommandContext ctx, int id, CancellationToken ct)
    {
        if (ctx.PrepareTaskSwitch is null)
        {
            ctx.Write("[task] switch is unavailable in this context.\n");
            return;
        }

        TaskSwitchPlan? plan;
        try
        {
            plan = await ctx.PrepareTaskSwitch(id, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            ctx.Write($"[task] switch failed: {ex.Message}\n");
            return;
        }

        if (plan is null)
        {
            ctx.Write($"[task] {id:D4} not found.\n");
            return;
        }

        // The host's outer loop spawns the next session with cwd = plan.Task.HomeCwd, so the
        // relative `.task/<id>-<slug>/` pointer in FirstUserMessage resolves correctly regardless
        // of where InfiniContext itself was launched from. Tasks own their home; sessions follow.
        ctx.Write($"[task] switching to {plan.Task.Id:D4} \"{plan.Task.Title}\"\n");
        if (!PathsAreEquivalent(plan.Task.HomeCwd, Environment.CurrentDirectory))
            ctx.Write($"       cwd: {plan.Task.HomeCwd}\n");
        if (plan.Drift.Count > 0)
            ctx.Write($"       drift: {string.Join(", ", plan.Drift)} (will ask the agent to reconcile)\n");

        ctx.Actions.RequestSwitch(plan);
    }

    private static bool PathsAreEquivalent(string a, string b)
    {
        // Normalise via GetFullPath (resolves relative segments and trailing slashes). Stage 2A
        // is Windows-only, so case-insensitive compare is correct here; revisit when 2B lands.
        var fa = Path.GetFullPath(a).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var fb = Path.GetFullPath(b).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return string.Equals(fa, fb, StringComparison.OrdinalIgnoreCase);
    }
}
