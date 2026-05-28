using InfiniContext.Core.SlashCommands;

namespace InfiniContext.Cli.SlashCommands;

internal sealed class ApprovalCommand : ISlashCommand
{
    public string Name => "/approval";
    public IReadOnlyList<string> Aliases { get; } = Array.Empty<string>();
    public string Summary => "Get or set the tool-approval mode (ask, deny, allow, auto).";
    public string Details =>
        """
        /approval                      print the current approval mode
        /approval <ask|deny|allow|auto> switch to a new mode for the rest of the session

        Modes:
          ask    Prompt the user for each tool call. Default for interactive runs.
          deny   Reject every tool call. Useful for read-only or "explain only" sessions.
          allow  Approve every tool call. Use only when you fully trust the prompt and tools.
          auto   Forward each request to a light-model safety review (requires an API key).

        See settings.json -> approval.* for the auto-mode model configuration.
        """;

    public Task ExecuteAsync(SlashCommandContext ctx, string argLine, CancellationToken ct)
    {
        var arg = argLine.Trim();
        if (arg.Length == 0)
        {
            var current = ctx.GetApprovalMode?.Invoke().ToString().ToLowerInvariant() ?? "<unknown>";
            ctx.Renderer.Write($"[approval] mode={current}. Usage: /approval <ask|deny|allow|auto>\n");
            return Task.CompletedTask;
        }

        if (ctx.SwitchApprovalMode is null)
        {
            ctx.Renderer.Write("[approval] mode switching is unavailable in this context\n");
            return Task.CompletedTask;
        }

        if (!RunArgs.TryParseApprovalMode(arg, out var requested))
        {
            ctx.Renderer.Write($"[approval] '{arg}' is not one of ask, deny, allow, auto\n");
            return Task.CompletedTask;
        }

        try
        {
            var effective = ctx.SwitchApprovalMode(requested);
            if (effective != requested)
                ctx.Renderer.Write($"[approval] requested={requested.ToString().ToLowerInvariant()} but using {effective.ToString().ToLowerInvariant()} (see warnings above)\n");
            else
                ctx.Renderer.Write($"[approval] mode={effective.ToString().ToLowerInvariant()}\n");
        }
        catch (Exception ex)
        {
            ctx.Renderer.Write($"[approval] switch failed: {ex.Message}\n");
        }
        return Task.CompletedTask;
    }
}
